namespace WinNotch.Models;

public sealed record SystemTelemetrySnapshot
{
    public string CpuName { get; init; } = "CPU";
    public int LogicalCores { get; init; } = Environment.ProcessorCount;
    public int CpuPercent { get; init; }
    public double[] CpuHistory { get; init; } = [];

    public string GpuName { get; init; } = "GPU";
    public string GpuVramLabel { get; init; } = string.Empty;
    public int GpuPercent { get; init; }
    public bool IsGpuAvailable { get; init; }

    public double RamUsedGb { get; init; }
    public double RamTotalGb { get; init; }
    public int RamPercent { get; init; }

    public string SystemDriveName { get; init; } = "C:";
    public double DriveFreeGb { get; init; }
    public double DriveTotalGb { get; init; }
    public int DriveUsedPercent { get; init; }
    public double DiskReadBytesPerSec { get; init; }
    public double DiskWriteBytesPerSec { get; init; }

    public string TopAppName { get; init; } = "Hệ thống rảnh";
    public double TopAppCpuPercent { get; init; }
    public double TopAppRamMb { get; init; }

    public bool IsNetworkConnected { get; init; }
    public bool IsWifi { get; init; }
    public string NetworkAdapterName { get; init; } = "Không có mạng";
    public string LocalIpv4 { get; init; } = "—";
    public double DownloadBytesPerSec { get; init; }
    public double UploadBytesPerSec { get; init; }
    public long SessionDownloadedBytes { get; init; }
    public long SessionUploadedBytes { get; init; }
    public double[] DownloadHistory { get; init; } = [];
    public double[] UploadHistory { get; init; } = [];

    public TimeSpan Uptime { get; init; }
}
