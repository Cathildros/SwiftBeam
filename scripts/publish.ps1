param(
    [string[]]$Runtimes = @("win-x64", "linux-x64"),
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"

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

foreach ($rid in $Runtimes) {
    $outDir = Join-Path $dist $rid
    Write-Host "`n=== Publishing SwiftBeam Desktop v$Version ($rid) ===" -ForegroundColor Cyan
    dotnet publish (Join-Path $root "src\SwiftBeam.Desktop\SwiftBeam.Desktop.csproj") `
        -c Release `
        -r $rid `
        --self-contained true `
        -p:Version=$Version `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $outDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish Desktop failed for $rid" }

    Write-Host "`n=== Publishing SwiftBeam CLI v$Version ($rid) ===" -ForegroundColor Cyan
    dotnet publish (Join-Path $root "src\SwiftBeam.Cli\SwiftBeam.Cli.csproj") `
        -c Release `
        -r $rid `
        --self-contained true `
        -p:Version=$Version `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $outDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish CLI failed for $rid" }

    if ($rid -like "linux-*") {
        Copy-Item (Join-Path $PSScriptRoot "install-linux.sh") (Join-Path $outDir "install.sh") -Force
        Copy-Item (Join-Path $root "src\SwiftBeam.Desktop\Assets\SwiftBeam.png") (Join-Path $outDir "swiftbeam.png") -Force
    } elseif ($rid -like "win-*") {
        Copy-Item (Join-Path $PSScriptRoot "install-windows.ps1") (Join-Path $outDir "install.ps1") -Force
        & (Join-Path $PSScriptRoot "build-windows-installer.ps1") -DistDir $outDir -Version $Version
    }
}

Write-Host "`nDone! Standalone binaries are in: $dist" -ForegroundColor Green
Get-ChildItem $dist -Recurse -File | Select-Object FullName, @{Name='MB';Expression={[math]::Round($_.Length/1MB, 2)}} | Format-Table -AutoSize
