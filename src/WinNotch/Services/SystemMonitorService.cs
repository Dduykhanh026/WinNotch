using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using WinNotch.Interop;
using WinNotch.Models;

namespace WinNotch.Services;

/// <summary>
/// 2-Tier 100% Offline, User-Mode System &amp; Network Telemetry Engine:
/// - Tier 1 (1s ultra-light Win32/IPv4 ring buffer): CPU %, RAM GB, Network Down/Up (&lt; 0.05 ms/tick, ~0% CPU).
/// - Tier 2 (Active ONLY while Notch is Expanded): PDH GPU Engine %, Disk Read/Write MB/s, grouped Top App CPU/RAM.
/// </summary>
public sealed class SystemMonitorService : IDisposable
{
    public const int HistoryPoints = 32;

    private static readonly HashSet<string> IgnoredProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "_Total", "Idle", "System", "Secure System", "Registry", "Memory Compression", "WinNotch"
    };

    private readonly SettingsService _settingsService;
    private readonly object _syncLock = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    // Static hardware descriptors (probed once)
    private string _cpuName = "CPU";
    private readonly int _logicalCores = Math.Max(1, Environment.ProcessorCount);
    private string _gpuName = "GPU tích hợp";
    private string _gpuVramLabel = string.Empty;

    // Tier-1 state (CPU, RAM, Network)
    private ulong _prevCpuIdleTicks;
    private ulong _prevCpuTotalTicks;
    private bool _hasCpuBaseline;
    private int _cpuPercent;
    private double _ramUsedGb;
    private double _ramTotalGb;
    private int _ramPercent;

    private readonly double[] _cpuHistory = new double[HistoryPoints];
    private readonly double[] _downHistory = new double[HistoryPoints];
    private readonly double[] _upHistory = new double[HistoryPoints];
    private int _ringHead;

    private NetworkInterface[] _activeAdapters = [];
    private string _adapterSignature = string.Empty;
    private bool _isNetworkConnected;
    private bool _isWifi;
    private string _adapterDisplayName = "Không có mạng";
    private string _localIpv4 = "—";
    private volatile bool _networkDirty = true;
    private long _lastAdapterProbeTimestamp;

    private long _prevRxBytes;
    private long _prevTxBytes;
    private long _lastNetSampleTimestamp;
    private bool _hasNetBaseline;
    private double _downloadBps;
    private double _uploadBps;
    private long _sessionRxBytes;
    private long _sessionTxBytes;

    // Tier-2 state (PDH GPU, Disk R/W, Drive capacity, Top App) — active ONLY while Expanded
    private volatile bool _isDeepActive;
    private IntPtr _pdhQuery;
    private IntPtr _gpuCounter;
    private IntPtr _diskReadCounter;
    private IntPtr _diskWriteCounter;
    private IntPtr _procCpuCounter;
    private IntPtr _procMemCounter;
    private IntPtr _pdhBuffer;
    private uint _pdhBufferCapacity;
    private bool _pdhInitialized;
    private bool _pdhSeeded;

    private bool _isGpuAvailable;
    private int _gpuPercent;
    private double _diskReadBps;
    private double _diskWriteBps;
    private string _systemDriveName = "C:";
    private double _driveFreeGb;
    private double _driveTotalGb;
    private int _driveUsedPercent;
    private long _lastDriveProbeTimestamp;

    private string _topAppName = "Hệ thống rảnh";
    private double _topAppCpuPercent;
    private double _topAppRamMb;
    private int _tickCounter;

    public SystemTelemetrySnapshot LatestSnapshot { get; private set; } = new();

    public event Action<SystemTelemetrySnapshot>? SnapshotUpdated;

    private int _isOptimizingRam; // 0 = idle, 1 = running

    /// <summary>
    /// Event fired when RAM optimization completes with total reclaimed bytes.
    /// </summary>
    public event Action<long>? WorkingSetOptimized;

    public SystemMonitorService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>
    /// Asynchronously trims the working set of all eligible user-mode processes,
    /// measures physical RAM reclaimed, and pushes an immediate telemetry update.
    /// 100% User-Mode safe — zero UAC elevation required.
    /// </summary>
    /// <returns>Reclaimed bytes as long (convert to MB via bytes / (1024 * 1024)).</returns>
    public async Task<long> OptimizeWorkingSetAsync()
    {
        // Reentrancy guard: if already optimizing, return 0 immediately
        if (Interlocked.CompareExchange(ref _isOptimizingRam, 1, 0) != 0)
        {
            return 0L;
        }

        try
        {
            return await Task.Run(() =>
            {
                ulong availBefore = ReadAvailablePhysicalBytes();
                int currentPid = Environment.ProcessId;

                Process[] processes = Process.GetProcesses();
                try
                {
                    const uint desiredAccess = NativeMethods.PROCESS_SET_QUOTA | NativeMethods.PROCESS_QUERY_INFORMATION;

                    foreach (Process proc in processes)
                    {
                        int pid = proc.Id;

                        // 1. Skip system idle (0) and kernel system (4)
                        if (pid <= 4) continue;

                        // 2. Skip WinNotch itself to preserve 240Hz UI smoothness and prevent DirectX cache purge
                        if (pid == currentPid) continue;

                        // 3. Skip known system daemons
                        try
                        {
                            string procName = proc.ProcessName;
                            if (IgnoredProcessNames.Contains(procName)) continue;
                        }
                        catch
                        {
                            // Process may have exited before inspection
                            continue;
                        }

                        // 4. Open process with exact required rights (non-elevated safe)
                        IntPtr hProcess = NativeMethods.OpenProcess(desiredAccess, false, pid);
                        if (hProcess == IntPtr.Zero)
                        {
                            // Access denied on elevated/system processes — gracefully ignore
                            continue;
                        }

                        try
                        {
                            NativeMethods.EmptyWorkingSet(hProcess);
                        }
                        catch
                        {
                            // Guard against process terminating mid-operation
                        }
                        finally
                        {
                            NativeMethods.CloseHandle(hProcess);
                        }
                    }
                }
                finally
                {
                    foreach (Process proc in processes)
                    {
                        try { proc.Dispose(); } catch { }
                    }
                }

                // Settle Windows memory manager working set counters
                Thread.Sleep(50);

                ulong availAfter = ReadAvailablePhysicalBytes();
                long reclaimedBytes = availAfter > availBefore ? (long)(availAfter - availBefore) : 0L;

                // Force Tier-1 telemetry refresh and immediate UI update
                SampleTier1();
                EmitSnapshotToUi();

                WorkingSetOptimized?.Invoke(reclaimedBytes);
                return reclaimedBytes;
            }).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _isOptimizingRam, 0);
        }
    }

    private static ulong ReadAvailablePhysicalBytes()
    {
        var mem = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };
        return NativeMethods.GlobalMemoryStatusEx(ref mem) ? mem.ullAvailPhys : 0UL;
    }

    public void Start()
    {
        if (_cts != null) return;

        NetworkChange.NetworkAddressChanged += OnSystemNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnSystemNetworkAvailabilityChanged;

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        _loopTask = Task.Run(async () =>
        {
            ProbeStaticHardwareInfo();
            SampleDriveCapacity();
            SampleTier1();
            EmitSnapshotToUi();

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0));
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                        break;

                    _tickCounter++;
                    SampleTier1();

                    bool deep = _isDeepActive;
                    if (deep)
                    {
                        SampleTier2Deep();
                    }

                    // Dispatch to UI every 1s when Expanded or when Collapsed Network Speed is ON;
                    // otherwise every 4s to keep Dashboard CPU/RAM fresh at ~0.00% idle UI cost.
                    bool needEverySecond = deep || _settingsService.Current.ShowCollapsedNetworkSpeed;
                    if (needEverySecond || (_tickCounter % 4 == 0))
                    {
                        EmitSnapshotToUi();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Guard background telemetry loop against transient OS counter glitches
                }
            }
        }, token);
    }

    public void SetDeepMonitoringActive(bool active)
    {
        if (_isDeepActive == active) return;
        _isDeepActive = active;

        if (active)
        {
            Task.Run(async () =>
            {
                try
                {
                    SampleDriveCapacity();
                    SampleTier2Deep();
                    await Task.Delay(220).ConfigureAwait(false);
                    if (_isDeepActive)
                    {
                        SampleTier2Deep();
                        EmitSnapshotToUi();
                    }
                }
                catch { }
            });
        }
        else
        {
            _pdhSeeded = false;
        }
    }

    private void OnSystemNetworkChanged(object? sender, EventArgs e) => _networkDirty = true;
    private void OnSystemNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => _networkDirty = true;

    private void ProbeStaticHardwareInfo()
    {
        // 1. CPU Name from Registry (0 WMI overhead)
        try
        {
            using RegistryKey? cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", writable: false);
            string? rawCpu = cpuKey?.GetValue("ProcessorNameString") as string;
            if (!string.IsNullOrWhiteSpace(rawCpu))
            {
                _cpuName = CleanCpuModelName(rawCpu);
            }
        }
        catch { }

        // 2. GPU Name & 64-bit VRAM from Display Class Registry
        try
        {
            using RegistryKey? dispClass = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}",
                writable: false);

            if (dispClass != null)
            {
                string bestGpu = string.Empty;
                long bestVram = -1;

                foreach (string sub in dispClass.GetSubKeyNames())
                {
                    if (sub.Length != 4 || !char.IsDigit(sub[0])) continue;
                    using RegistryKey? adapterKey = dispClass.OpenSubKey(sub, writable: false);
                    string? desc = adapterKey?.GetValue("DriverDesc") as string;
                    if (string.IsNullOrWhiteSpace(desc)) continue;
                    if (desc.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
                        desc.Contains("Remote", StringComparison.OrdinalIgnoreCase))
                        continue;

                    long vramBytes = ReadRegistryQword(adapterKey?.GetValue("HardwareInformation.qwMemorySize"));
                    bool isDiscrete = desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                                      desc.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) ||
                                      desc.Contains("Arc ", StringComparison.OrdinalIgnoreCase);
                    long score = vramBytes + (isDiscrete ? (1L << 40) : 0);

                    if (score > bestVram)
                    {
                        bestVram = score;
                        bestGpu = CleanGpuModelName(desc);
                        _gpuVramLabel = vramBytes >= (512L * 1024 * 1024)
                            ? $"{vramBytes / (1024.0 * 1024.0 * 1024.0):0.#} GB"
                            : string.Empty;
                    }
                }

                if (!string.IsNullOrWhiteSpace(bestGpu))
                {
                    _gpuName = bestGpu;
                }
            }
        }
        catch { }
    }

    private void SampleTier1()
    {
        // 1. CPU % via GetSystemTimes
        if (NativeMethods.GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
        {
            ulong idle = idleFt.ToUInt64();
            ulong total = kernelFt.ToUInt64() + userFt.ToUInt64();

            if (_hasCpuBaseline && total > _prevCpuTotalTicks)
            {
                ulong totalDelta = total - _prevCpuTotalTicks;
                ulong idleDelta = idle >= _prevCpuIdleTicks ? idle - _prevCpuIdleTicks : 0;
                ulong busyDelta = totalDelta > idleDelta ? totalDelta - idleDelta : 0;
                _cpuPercent = (int)Math.Clamp(Math.Round((double)busyDelta / totalDelta * 100.0), 0, 100);
            }

            _prevCpuIdleTicks = idle;
            _prevCpuTotalTicks = total;
            _hasCpuBaseline = true;
        }

        // 2. Physical RAM via GlobalMemoryStatusEx
        var mem = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };
        if (NativeMethods.GlobalMemoryStatusEx(ref mem) && mem.ullTotalPhys > 0)
        {
            const double OneGb = 1024.0 * 1024.0 * 1024.0;
            ulong usedBytes = mem.ullTotalPhys > mem.ullAvailPhys ? mem.ullTotalPhys - mem.ullAvailPhys : 0;
            _ramTotalGb = mem.ullTotalPhys / OneGb;
            _ramUsedGb = usedBytes / OneGb;
            _ramPercent = (int)Math.Clamp(mem.dwMemoryLoad, 0u, 100u);
        }

        // 3. Network Adapter Discovery (every 10s or on OS network change event) + 1s Byte Throughput
        long nowTs = Stopwatch.GetTimestamp();
        double sinceProbeSec = (double)(nowTs - _lastAdapterProbeTimestamp) / Stopwatch.Frequency;
        if (_networkDirty || sinceProbeSec >= 10.0)
        {
            RefreshActiveNetworkAdapters(nowTs);
        }

        SampleNetworkThroughput(nowTs);

        // 4. Push into 32-second ring buffers
        lock (_syncLock)
        {
            _cpuHistory[_ringHead] = _cpuPercent;
            _downHistory[_ringHead] = _downloadBps;
            _upHistory[_ringHead] = _uploadBps;
            _ringHead = (_ringHead + 1) % HistoryPoints;
        }
    }

    private void RefreshActiveNetworkAdapters(long nowTs)
    {
        _networkDirty = false;
        _lastAdapterProbeTimestamp = nowTs;

        try
        {
            NetworkInterface[] all = NetworkInterface.GetAllNetworkInterfaces();
            var candidates = new List<(NetworkInterface Adapter, bool HasGateway)>();

            foreach (NetworkInterface ni in all)
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                string desc = ni.Description ?? string.Empty;
                if (desc.Contains("Filter", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("QoS", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("WFP", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("Virtual WiFi", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool hasGw = false;
                try
                {
                    IPInterfaceProperties ipProps = ni.GetIPProperties();
                    foreach (GatewayIPAddressInformation gw in ipProps.GatewayAddresses)
                    {
                        if (!gw.Address.Equals(IPAddress.Any) && !gw.Address.Equals(IPAddress.IPv6Any))
                        {
                            hasGw = true;
                            break;
                        }
                    }
                }
                catch { }

                candidates.Add((ni, hasGw));
            }

            // Prefer real gateway-routed interfaces so WSL / Hyper-V / VMware virtual switches never double-count
            NetworkInterface[] chosen = candidates.Any(c => c.HasGateway)
                ? candidates.Where(c => c.HasGateway).Select(c => c.Adapter).ToArray()
                : candidates.Where(c => !IsVirtualSwitchDescription(c.Adapter.Description)).Select(c => c.Adapter).ToArray();

            string newSig = string.Join("|", chosen.Select(a => a.Id));
            if (newSig != _adapterSignature)
            {
                _adapterSignature = newSig;
                _hasNetBaseline = false; // Re-baseline without spike when switching NICs
            }

            _activeAdapters = chosen;
            if (chosen.Length == 0)
            {
                _isNetworkConnected = false;
                _isWifi = false;
                _adapterDisplayName = "Không có mạng";
                _localIpv4 = "—";
                return;
            }

            NetworkInterface primary = chosen
                .OrderByDescending(a => a.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                .ThenByDescending(a => a.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .First();

            _isNetworkConnected = true;
            _isWifi = primary.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            _adapterDisplayName = string.IsNullOrWhiteSpace(primary.Name)
                ? (_isWifi ? "Wi-Fi" : "Ethernet")
                : primary.Name;
            _localIpv4 = ExtractIpv4Address(primary);
        }
        catch
        {
            _isNetworkConnected = false;
        }
    }

    private void SampleNetworkThroughput(long nowTs)
    {
        if (_activeAdapters.Length == 0)
        {
            _downloadBps = 0;
            _uploadBps = 0;
            _hasNetBaseline = false;
            return;
        }

        long rxSum = 0;
        long txSum = 0;
        bool anyRead = false;

        foreach (NetworkInterface ni in _activeAdapters)
        {
            try
            {
                IPInterfaceStatistics stats = ni.GetIPStatistics();
                rxSum += stats.BytesReceived;
                txSum += stats.BytesSent;
                anyRead = true;
            }
            catch
            {
                _networkDirty = true;
            }
        }

        if (!anyRead) return;

        if (_hasNetBaseline && nowTs > _lastNetSampleTimestamp)
        {
            double dt = (double)(nowTs - _lastNetSampleTimestamp) / Stopwatch.Frequency;
            if (dt >= 0.15)
            {
                long rxDelta = rxSum >= _prevRxBytes ? rxSum - _prevRxBytes : 0;
                long txDelta = txSum >= _prevTxBytes ? txSum - _prevTxBytes : 0;

                _downloadBps = rxDelta / dt;
                _uploadBps = txDelta / dt;
                _sessionRxBytes += rxDelta;
                _sessionTxBytes += txDelta;
            }
        }

        _prevRxBytes = rxSum;
        _prevTxBytes = txSum;
        _lastNetSampleTimestamp = nowTs;
        _hasNetBaseline = true;
    }

    private void SampleTier2Deep()
    {
        lock (_syncLock)
        {
            long nowTs = Stopwatch.GetTimestamp();
            if (((double)(nowTs - _lastDriveProbeTimestamp) / Stopwatch.Frequency) >= 5.0)
            {
                SampleDriveCapacity();
            }

            EnsurePdhInitialized();
            if (_pdhQuery == IntPtr.Zero) return;

            if (NativeMethods.PdhCollectQueryData(_pdhQuery) != 0)
                return;

            if (!_pdhSeeded)
            {
                // First collection primes rate-based PDH counters
                _pdhSeeded = true;
                return;
            }

            // 1. Disk Read / Write Bytes/sec
            if (_diskReadCounter != IntPtr.Zero &&
                NativeMethods.PdhGetFormattedCounterValue(_diskReadCounter, NativeMethods.PDH_FMT_DOUBLE, out _, out var rdVal) == 0 &&
                rdVal.CStatus is NativeMethods.PDH_CSTATUS_VALID_DATA or NativeMethods.PDH_CSTATUS_NEW_DATA)
            {
                _diskReadBps = Math.Max(0.0, rdVal.doubleValue);
            }

            if (_diskWriteCounter != IntPtr.Zero &&
                NativeMethods.PdhGetFormattedCounterValue(_diskWriteCounter, NativeMethods.PDH_FMT_DOUBLE, out _, out var wrVal) == 0 &&
                wrVal.CStatus is NativeMethods.PDH_CSTATUS_VALID_DATA or NativeMethods.PDH_CSTATUS_NEW_DATA)
            {
                _diskWriteBps = Math.Max(0.0, wrVal.doubleValue);
            }

            // 2. GPU Engine Utilization % (Summed per engine type, max across 3D/Compute/VideoDecode/Copy like Task Manager)
            if (_gpuCounter != IntPtr.Zero)
            {
                SampleGpuCounterArray();
            }

            // 3. Top Resource App (Grouped by base app name across multi-process apps like Chrome/Edge)
            if (_procCpuCounter != IntPtr.Zero)
            {
                SampleTopProcessCounters();
            }
        }
    }

    private void EnsurePdhInitialized()
    {
        if (_pdhInitialized) return;
        _pdhInitialized = true;

        if (NativeMethods.PdhOpenQueryW(null, IntPtr.Zero, out _pdhQuery) != 0 || _pdhQuery == IntPtr.Zero)
        {
            _pdhQuery = IntPtr.Zero;
            return;
        }

        if (NativeMethods.PdhAddEnglishCounterW(_pdhQuery, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) == 0 &&
            _gpuCounter != IntPtr.Zero)
        {
            _isGpuAvailable = true;
        }

        NativeMethods.PdhAddEnglishCounterW(_pdhQuery, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", IntPtr.Zero, out _diskReadCounter);
        NativeMethods.PdhAddEnglishCounterW(_pdhQuery, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", IntPtr.Zero, out _diskWriteCounter);
        NativeMethods.PdhAddEnglishCounterW(_pdhQuery, @"\Process(*)\% Processor Time", IntPtr.Zero, out _procCpuCounter);
        NativeMethods.PdhAddEnglishCounterW(_pdhQuery, @"\Process(*)\Working Set - Private", IntPtr.Zero, out _procMemCounter);

        _pdhBufferCapacity = 64 * 1024;
        _pdhBuffer = Marshal.AllocHGlobal((int)_pdhBufferCapacity);
    }

    private bool TryReadPdhArray(IntPtr counter, uint formatFlags, out uint itemCount)
    {
        itemCount = 0;
        if (counter == IntPtr.Zero || _pdhBuffer == IntPtr.Zero) return false;

        uint bufSize = _pdhBufferCapacity;
        uint status = NativeMethods.PdhGetFormattedCounterArrayW(counter, formatFlags, ref bufSize, out itemCount, _pdhBuffer);

        if (status == NativeMethods.PDH_MORE_DATA && bufSize > _pdhBufferCapacity && bufSize <= 2 * 1024 * 1024)
        {
            Marshal.FreeHGlobal(_pdhBuffer);
            _pdhBufferCapacity = bufSize + (16 * 1024);
            _pdhBuffer = Marshal.AllocHGlobal((int)_pdhBufferCapacity);
            bufSize = _pdhBufferCapacity;
            status = NativeMethods.PdhGetFormattedCounterArrayW(counter, formatFlags, ref bufSize, out itemCount, _pdhBuffer);
        }

        return status == 0 && itemCount > 0;
    }

    private void SampleGpuCounterArray()
    {
        if (!TryReadPdhArray(_gpuCounter, NativeMethods.PDH_FMT_DOUBLE, out uint count))
            return;

        int itemSize = Marshal.SizeOf<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>();
        double sum3D = 0, sumVideo = 0, sumCompute = 0, sumCopy = 0, sumOther = 0;

        for (uint i = 0; i < count; i++)
        {
            var item = Marshal.PtrToStructure<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>(_pdhBuffer + (int)(i * itemSize));
            double val = item.FmtValue.doubleValue;
            if (val <= 0.01 || double.IsNaN(val)) continue;

            string? name = Marshal.PtrToStringUni(item.szName);
            if (string.IsNullOrEmpty(name)) continue;

            if (name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                sum3D += val;
            else if (name.Contains("engtype_Video", StringComparison.OrdinalIgnoreCase))
                sumVideo += val;
            else if (name.Contains("engtype_Compute", StringComparison.OrdinalIgnoreCase))
                sumCompute += val;
            else if (name.Contains("engtype_Copy", StringComparison.OrdinalIgnoreCase))
                sumCopy += val;
            else
                sumOther += val;
        }

        double peakEngine = Math.Max(sum3D, Math.Max(sumVideo, Math.Max(sumCompute, Math.Max(sumCopy, sumOther))));
        _gpuPercent = (int)Math.Clamp(Math.Round(peakEngine), 0, 100);
        _isGpuAvailable = true;
    }

    private void SampleTopProcessCounters()
    {
        var cpuByApp = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var ramByApp = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        int itemSize = Marshal.SizeOf<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>();

        if (TryReadPdhArray(_procCpuCounter, NativeMethods.PDH_FMT_DOUBLE | NativeMethods.PDH_FMT_NOCAP100, out uint cpuCount))
        {
            for (uint i = 0; i < cpuCount; i++)
            {
                var item = Marshal.PtrToStructure<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>(_pdhBuffer + (int)(i * itemSize));
                double val = item.FmtValue.doubleValue;
                if (val <= 0.01 || double.IsNaN(val)) continue;

                string baseName = NormalizePdhProcessName(Marshal.PtrToStringUni(item.szName));
                if (baseName.Length == 0 || IgnoredProcessNames.Contains(baseName)) continue;

                cpuByApp[baseName] = cpuByApp.GetValueOrDefault(baseName) + (val / _logicalCores);
            }
        }

        if (_procMemCounter != IntPtr.Zero &&
            TryReadPdhArray(_procMemCounter, NativeMethods.PDH_FMT_DOUBLE, out uint memCount))
        {
            for (uint i = 0; i < memCount; i++)
            {
                var item = Marshal.PtrToStructure<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>(_pdhBuffer + (int)(i * itemSize));
                double bytes = item.FmtValue.doubleValue;
                if (bytes <= 0 || double.IsNaN(bytes)) continue;

                string baseName = NormalizePdhProcessName(Marshal.PtrToStringUni(item.szName));
                if (baseName.Length == 0 || IgnoredProcessNames.Contains(baseName)) continue;

                ramByApp[baseName] = ramByApp.GetValueOrDefault(baseName) + (bytes / (1024.0 * 1024.0));
            }
        }

        string bestApp = string.Empty;
        double bestCpu = 0;
        foreach (var kv in cpuByApp)
        {
            if (kv.Value > bestCpu)
            {
                bestCpu = kv.Value;
                bestApp = kv.Key;
            }
        }

        // If CPU is very quiet (< 0.8%), surface the top RAM-consuming active app instead
        if (bestCpu < 0.8 && ramByApp.Count > 0)
        {
            double bestRam = 0;
            foreach (var kv in ramByApp)
            {
                if (kv.Value > bestRam)
                {
                    bestRam = kv.Value;
                    bestApp = kv.Key;
                }
            }
            bestCpu = cpuByApp.GetValueOrDefault(bestApp);
        }

        if (!string.IsNullOrWhiteSpace(bestApp))
        {
            _topAppName = FormatFriendlyAppName(bestApp);
            _topAppCpuPercent = Math.Clamp(bestCpu, 0.0, 100.0);
            _topAppRamMb = ramByApp.GetValueOrDefault(bestApp);
        }
    }

    private void SampleDriveCapacity()
    {
        _lastDriveProbeTimestamp = Stopwatch.GetTimestamp();
        try
        {
            string sysRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            var di = new DriveInfo(sysRoot);
            if (di.IsReady && di.TotalSize > 0)
            {
                const double OneGb = 1024.0 * 1024.0 * 1024.0;
                _systemDriveName = sysRoot.TrimEnd('\\');
                _driveTotalGb = di.TotalSize / OneGb;
                _driveFreeGb = di.AvailableFreeSpace / OneGb;
                double usedRatio = (double)(di.TotalSize - di.AvailableFreeSpace) / di.TotalSize;
                _driveUsedPercent = (int)Math.Clamp(Math.Round(usedRatio * 100.0), 0, 100);
            }
        }
        catch { }
    }

    private void EmitSnapshotToUi()
    {
        double[] cpuCopy;
        double[] downCopy;
        double[] upCopy;

        lock (_syncLock)
        {
            cpuCopy = CopyRingChronological(_cpuHistory, _ringHead);
            downCopy = CopyRingChronological(_downHistory, _ringHead);
            upCopy = CopyRingChronological(_upHistory, _ringHead);
        }

        var snap = new SystemTelemetrySnapshot
        {
            CpuName = _cpuName,
            LogicalCores = _logicalCores,
            CpuPercent = _cpuPercent,
            CpuHistory = cpuCopy,
            GpuName = _gpuName,
            GpuVramLabel = _gpuVramLabel,
            GpuPercent = _gpuPercent,
            IsGpuAvailable = _isGpuAvailable,
            RamUsedGb = _ramUsedGb,
            RamTotalGb = _ramTotalGb,
            RamPercent = _ramPercent,
            SystemDriveName = _systemDriveName,
            DriveFreeGb = _driveFreeGb,
            DriveTotalGb = _driveTotalGb,
            DriveUsedPercent = _driveUsedPercent,
            DiskReadBytesPerSec = _diskReadBps,
            DiskWriteBytesPerSec = _diskWriteBps,
            TopAppName = _topAppName,
            TopAppCpuPercent = _topAppCpuPercent,
            TopAppRamMb = _topAppRamMb,
            IsNetworkConnected = _isNetworkConnected,
            IsWifi = _isWifi,
            NetworkAdapterName = _adapterDisplayName,
            LocalIpv4 = _localIpv4,
            DownloadBytesPerSec = _downloadBps,
            UploadBytesPerSec = _uploadBps,
            SessionDownloadedBytes = _sessionRxBytes,
            SessionUploadedBytes = _sessionTxBytes,
            DownloadHistory = downCopy,
            UploadHistory = upCopy,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64)
        };

        LatestSnapshot = snap;
        Application.Current?.Dispatcher.BeginInvoke(() => SnapshotUpdated?.Invoke(snap));
    }

    private static double[] CopyRingChronological(double[] ring, int head)
    {
        var result = new double[HistoryPoints];
        int tailLen = HistoryPoints - head;
        Array.Copy(ring, head, result, 0, tailLen);
        if (head > 0)
        {
            Array.Copy(ring, 0, result, tailLen, head);
        }
        return result;
    }

    private static string NormalizePdhProcessName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        int hashIdx = raw.IndexOf('#');
        return hashIdx > 0 ? raw[..hashIdx] : raw;
    }

    private static string FormatFriendlyAppName(string procBase)
    {
        if (procBase.Length <= 1) return procBase;
        return procBase.ToLowerInvariant() switch
        {
            "msedge" => "Microsoft Edge",
            "chrome" => "Google Chrome",
            "firefox" => "Firefox",
            "code" => "VS Code",
            "devenv" => "Visual Studio",
            "explorer" => "Windows Explorer",
            "spotify" => "Spotify",
            "zalo" => "Zalo",
            "discord" => "Discord",
            "dwm" => "Desktop Window Mgr",
            _ => char.ToUpperInvariant(procBase[0]) + procBase[1..]
        };
    }

    private static string ExtractIpv4Address(NetworkInterface ni)
    {
        try
        {
            foreach (UnicastIPAddressInformation uni in ni.GetIPProperties().UnicastAddresses)
            {
                if (uni.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    string ip = uni.Address.ToString();
                    if (!ip.StartsWith("169.254.", StringComparison.Ordinal))
                        return ip;
                }
            }
        }
        catch { }
        return "—";
    }

    private static bool IsVirtualSwitchDescription(string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return false;
        return desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
               desc.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
               desc.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
               desc.Contains("WSL", StringComparison.OrdinalIgnoreCase);
    }

    private static long ReadRegistryQword(object? val) => val switch
    {
        long l => l,
        int i => i,
        byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
        _ => 0
    };

    private static string CleanCpuModelName(string raw)
    {
        string s = raw
            .Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Processor", string.Empty, StringComparison.OrdinalIgnoreCase);

        s = Regex.Replace(s, @"^\d+th\s+Gen\s+", string.Empty, RegexOptions.IgnoreCase);
        int atIdx = s.IndexOf('@');
        if (atIdx > 0) s = s[..atIdx];
        int withIdx = s.IndexOf("w/", StringComparison.OrdinalIgnoreCase);
        if (withIdx > 0) s = s[..withIdx];

        s = Regex.Replace(s, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(s) ? "CPU" : s;
    }

    private static string CleanGpuModelName(string raw)
    {
        string s = raw
            .Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("NVIDIA GeForce ", "NVIDIA ", StringComparison.OrdinalIgnoreCase)
            .Replace("AMD Radeon ", "Radeon ", StringComparison.OrdinalIgnoreCase)
            .Replace("Graphics", string.Empty, StringComparison.OrdinalIgnoreCase);

        s = Regex.Replace(s, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(s) ? "GPU" : s;
    }

    public static string FormatSpeed(double bytesPerSec, bool compact = false)
    {
        if (bytesPerSec < 512.0)
            return compact ? "0 KB/s" : "0 KB/s";

        double kb = bytesPerSec / 1024.0;
        if (kb < 1000.0)
            return kb < 10.0 ? $"{kb:0.0} KB/s" : $"{kb:0} KB/s";

        double mb = kb / 1024.0;
        if (mb < 1000.0)
            return mb < 100.0 ? $"{mb:0.0} MB/s" : $"{mb:0} MB/s";

        double gb = mb / 1024.0;
        return $"{gb:0.00} GB/s";
    }

    public static string FormatBytesTotal(long bytes)
    {
        if (bytes < 1024L * 1024L)
            return $"{bytes / 1024.0:0} KB";

        double mb = bytes / (1024.0 * 1024.0);
        if (mb < 1000.0)
            return $"{mb:0.0} MB";

        double gb = mb / 1024.0;
        return $"{gb:0.00} GB";
    }

    public static string FormatUptime(TimeSpan ts)
    {
        if (ts.TotalDays >= 1.0)
            return $"{(int)ts.TotalDays}n {ts.Hours:00}g {ts.Minutes:00}p";
        return $"{ts.Hours:00}g {ts.Minutes:00}p";
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnSystemNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnSystemNetworkAvailabilityChanged;

        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        lock (_syncLock)
        {
            if (_pdhQuery != IntPtr.Zero)
            {
                NativeMethods.PdhCloseQuery(_pdhQuery);
                _pdhQuery = IntPtr.Zero;
            }

            if (_pdhBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_pdhBuffer);
                _pdhBuffer = IntPtr.Zero;
                _pdhBufferCapacity = 0;
            }
        }
    }
}
