param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$ProjectFile = Join-Path $RepoRoot "src\WinNotch\WinNotch.csproj"
$IconFile = Join-Path $RepoRoot "src\WinNotch\Assets\app.ico"
$ArtifactsDir = Join-Path $RepoRoot "artifacts"
$PublishDir = Join-Path $ArtifactsDir "_staging-publish"
$SingleFileDir = Join-Path $ArtifactsDir "_staging-single"
$VelopackOutDir = Join-Path $ArtifactsDir "_staging-velopack"
$ToolsDir = Join-Path $RepoRoot ".tools"

Write-Host "==> [1/4] Stopping running WinNotch instance (if any) & cleaning artifacts..." -ForegroundColor Cyan
Get-Process -Name "WinNotch" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
if (Test-Path $ArtifactsDir) {
    Remove-Item -Recurse -Force $ArtifactsDir
}
New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null
New-Item -ItemType Directory -Force -Path $SingleFileDir | Out-Null
New-Item -ItemType Directory -Force -Path $VelopackOutDir | Out-Null

Write-Host "==> [2/4] Publishing standalone Single-File WinNotch.exe..." -ForegroundColor Cyan
dotnet publish $ProjectFile `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $SingleFileDir

if ($LASTEXITCODE -ne 0) {
    throw "Single-file dotnet publish failed with exit code $LASTEXITCODE"
}

$StandaloneExePath = Join-Path $ArtifactsDir "WinNotch.exe"
Copy-Item -Path (Join-Path $SingleFileDir "WinNotch.exe") -Destination $StandaloneExePath -Force

Write-Host "==> [3/4] Creating Portable ZIP bundle (WinNotch-Portable-win-x64.zip)..." -ForegroundColor Cyan
Set-Content -Path (Join-Path $SingleFileDir "portable.mode") -Value "WinNotch Portable Mode" -Encoding UTF8
$PortableZipPath = Join-Path $ArtifactsDir "WinNotch-Portable-win-x64.zip"
Compress-Archive -Path "$SingleFileDir\*" -DestinationPath $PortableZipPath -Force

Write-Host "==> [4/4] Packaging 1-click installer (WinNotch-Setup.exe) via Velopack..." -ForegroundColor Cyan
dotnet publish $ProjectFile `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $PublishDir

if ($LASTEXITCODE -ne 0) {
    throw "Velopack staging publish failed with exit code $LASTEXITCODE"
}

$VpkExe = Join-Path $ToolsDir "vpk.exe"
if (-not (Test-Path $VpkExe)) {
    New-Item -ItemType Directory -Force -Path $ToolsDir | Out-Null
    dotnet tool update --tool-path $ToolsDir vpk --version 0.0.1053
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to install Velopack CLI (vpk)"
    }
}

& $VpkExe pack `
    --packId "WinNotch" `
    --packVersion $Version `
    --packDir $PublishDir `
    --mainExe "WinNotch.exe" `
    --packTitle "WinNotch" `
    --icon $IconFile `
    --outputDir $VelopackOutDir

if ($LASTEXITCODE -ne 0) {
    throw "vpk pack failed with exit code $LASTEXITCODE"
}

$GeneratedSetup = Get-ChildItem -Path $VelopackOutDir -Filter "*Setup.exe" | Select-Object -First 1
if ($null -eq $GeneratedSetup) {
    throw "Could not locate generated Velopack Setup.exe in $VelopackOutDir"
}

$FinalSetupPath = Join-Path $ArtifactsDir "WinNotch-Setup.exe"
Copy-Item -Path $GeneratedSetup.FullName -Destination $FinalSetupPath -Force

# Clean all temporary staging folders so artifacts/ contains ONLY the 3 clean deliverables
Remove-Item -Recurse -Force $PublishDir
Remove-Item -Recurse -Force $SingleFileDir
Remove-Item -Recurse -Force $VelopackOutDir

$ExeMb = [math]::Round((Get-Item $StandaloneExePath).Length / 1MB, 1)
$SetupMb = [math]::Round((Get-Item $FinalSetupPath).Length / 1MB, 1)
$ZipMb = [math]::Round((Get-Item $PortableZipPath).Length / 1MB, 1)

Write-Host ""
Write-Host "BUILD RELEASE COMPLETED SUCCESSFULLY!" -ForegroundColor Green
Write-Host "  -> Run Now   : $StandaloneExePath ($ExeMb MB)" -ForegroundColor Green
Write-Host "  -> Installer : $FinalSetupPath ($SetupMb MB)" -ForegroundColor Green
Write-Host "  -> Portable  : $PortableZipPath ($ZipMb MB)" -ForegroundColor Green
