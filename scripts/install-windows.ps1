$ErrorActionPreference = "Stop"

$sourceDir = $PSScriptRoot
$installDir = Join-Path $env:LOCALAPPDATA "SwiftBeam"
New-Item -ItemType Directory -Force -Path $installDir | Out-Null

$exeSrc = Join-Path $sourceDir "SwiftBeam.exe"
if (-not (Test-Path $exeSrc)) {
    throw "SwiftBeam.exe not found in $sourceDir"
}

Copy-Item $exeSrc (Join-Path $installDir "SwiftBeam.exe") -Force
$cliSrc = Join-Path $sourceDir "swiftbeam-cli.exe"
if (Test-Path $cliSrc) {
    Copy-Item $cliSrc (Join-Path $installDir "swiftbeam-cli.exe") -Force
}

# Add to User PATH if missing
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if (-not ($userPath -split ';' | Where-Object { $_ -eq $installDir })) {
    [Environment]::SetEnvironmentVariable("Path", ($userPath.TrimEnd(';') + ";" + $installDir), "User")
}

# Create Start Menu shortcut
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$shortcutPath = Join-Path $startMenu "SwiftBeam.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $installDir "SwiftBeam.exe"
$shortcut.WorkingDirectory = $installDir
$shortcut.Description = "SwiftBeam — Fast Cross-Platform File Transfer"
$shortcut.Save()

Write-Host "Installed SwiftBeam to $installDir" -ForegroundColor Green
Write-Host "Start Menu shortcut created: $shortcutPath" -ForegroundColor Green
Write-Host "If Windows Defender Firewall prompts on first run, allow Private networks so discovery & transfers work." -ForegroundColor Yellow
