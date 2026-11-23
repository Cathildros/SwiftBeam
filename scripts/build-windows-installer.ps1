param(
    [string]$DistDir = "",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($DistDir)) {
    $DistDir = Join-Path $root "dist\win-x64"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $propsPath = Join-Path $root "Directory.Build.props"
    if (Test-Path $propsPath) {
        [xml]$propsXml = Get-Content $propsPath -Raw
        $Version = $propsXml.Project.PropertyGroup.Version
    }
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = "1.0.0"
}
$Version = $Version.Trim().TrimStart('v', 'V')

$guiExe = Join-Path $DistDir "SwiftBeam.exe"
$cliExe = Join-Path $DistDir "swiftbeam-cli.exe"
if (-not (Test-Path $guiExe)) {
    throw "SwiftBeam.exe not found in $DistDir. Publish win-x64 first."
}

$cscCandidates = @(
    "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) {
    throw ".NET Framework csc.exe was not found on this machine."
}

$installerSrc = Join-Path $root "installer\windows\SwiftBeamInstaller.cs"
$manifestPath = Join-Path $root "installer\windows\installer.manifest"
$iconPath = Join-Path $root "src\SwiftBeam.Desktop\Assets\SwiftBeam.ico"

$stageDir = Join-Path $env:TEMP ("swiftbeam-setup-build-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

try {
    $versionCs = Join-Path $stageDir "VersionInfo.cs"
    $asmNumericVer = if ($Version -match '^\d+\.\d+\.\d+') { "$($Matches[0]).0" } else { "1.0.0.0" }
    @"
using System.Reflection;
[assembly: AssemblyVersion("$asmNumericVer")]
[assembly: AssemblyFileVersion("$asmNumericVer")]
[assembly: AssemblyInformationalVersion("$Version")]
"@ | Set-Content -Path $versionCs -Encoding UTF8

    $uninstallExe = Join-Path $stageDir "Uninstall.exe"
    $payloadZip = Join-Path $stageDir "SwiftBeamPayload.zip"
    $setupExe = Join-Path $DistDir "SwiftBeam-Setup.exe"

    Write-Host "`n=== Compiling SwiftBeam Uninstaller v$Version (Uninstall.exe) ===" -ForegroundColor Cyan
    & $csc /nologo /target:winexe /optimize+ /platform:x64 /define:UNINSTALLER_ONLY `
        "/win32icon:$iconPath" `
        "/win32manifest:$manifestPath" `
        "/out:$uninstallExe" `
        /reference:System.dll `
        /reference:System.Drawing.dll `
        /reference:System.Windows.Forms.dll `
        /reference:System.IO.Compression.dll `
        /reference:System.IO.Compression.FileSystem.dll `
        "$installerSrc" "$versionCs"
    if ($LASTEXITCODE -ne 0) { throw "Failed to compile Uninstall.exe (exit $LASTEXITCODE)" }

    Write-Host "Packaging SwiftBeamPayload.zip..." -ForegroundColor Cyan
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::Open($payloadZip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $guiExe, "SwiftBeam.exe", [System.IO.Compression.CompressionLevel]::NoCompression) | Out-Null
        if (Test-Path $cliExe) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $cliExe, "swiftbeam-cli.exe", [System.IO.Compression.CompressionLevel]::NoCompression) | Out-Null
        }
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $uninstallExe, "Uninstall.exe", [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    finally {
        $zip.Dispose()
    }

    Write-Host "`n=== Compiling SwiftBeam Windows Installer v$Version (SwiftBeam-Setup.exe) ===" -ForegroundColor Cyan
    & $csc /nologo /target:winexe /optimize+ /platform:x64 `
        "/win32icon:$iconPath" `
        "/win32manifest:$manifestPath" `
        "/resource:$payloadZip,SwiftBeamPayload.zip" `
        "/out:$setupExe" `
        /reference:System.dll `
        /reference:System.Drawing.dll `
        /reference:System.Windows.Forms.dll `
        /reference:System.IO.Compression.dll `
        /reference:System.IO.Compression.FileSystem.dll `
        "$installerSrc" "$versionCs"
    if ($LASTEXITCODE -ne 0) { throw "Failed to compile SwiftBeam-Setup.exe (exit $LASTEXITCODE)" }

    $sizeMb = [math]::Round((Get-Item $setupExe).Length / 1MB, 2)
    Write-Host "Built installer: $setupExe ($sizeMb MB)" -ForegroundColor Green
}
finally {
    Remove-Item -Path $stageDir -Recurse -Force -ErrorAction SilentlyContinue
}
