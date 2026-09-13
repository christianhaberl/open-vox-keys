# Per-user installation of a published portable build. No administrator required.
param([switch]$Uninstall, [switch]$Autostart)
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\OpenVoxKeys'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Open Vox Keys.lnk'
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($Uninstall) {
    $active = Get-Process OpenVoxKeys,VoicePoc -ErrorAction SilentlyContinue | Where-Object { $_.Path -in @((Join-Path $target 'OpenVoxKeys.exe'), (Join-Path $target 'VoicePoc.exe')) }
    if ($active) { throw 'Quit Open Vox Keys from the tray menu before uninstalling.' }
    Remove-ItemProperty -Path $run -Name OpenVoxKeys -ErrorAction SilentlyContinue
    Remove-Item $shortcut -ErrorAction SilentlyContinue
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Write-Output 'Open Vox Keys uninstalled. Personal settings and recordings have been retained.'
    exit
}
$source = Split-Path $PSScriptRoot -Parent
if (!(Test-Path (Join-Path $source 'OpenVoxKeys.exe'))) { throw 'Run Install.ps1 from the extracted Windows package.' }
$running = Get-Process OpenVoxKeys,VoicePoc -ErrorAction SilentlyContinue | Where-Object { $_.Path -in @((Join-Path $target 'OpenVoxKeys.exe'), (Join-Path $target 'VoicePoc.exe')) }
if ($running) { throw 'Quit Open Vox Keys from the tray menu before installing.' }
New-Item $target -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $source '*') $target -Recurse -Force
New-Item (Split-Path $shortcut -Parent) -ItemType Directory -Force | Out-Null
foreach ($legacy in @('VoicePoc.exe', 'VoicePoc.dll', 'VoicePoc.deps.json', 'VoicePoc.runtimeconfig.json', 'VoicePoc.pdb')) {
    Remove-Item (Join-Path $target $legacy) -ErrorAction SilentlyContinue
}
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = Join-Path $target 'OpenVoxKeys.exe'
$link.WorkingDirectory = $target
$link.Save()
if ($Autostart) {
    $settingsPath = Join-Path $env:LOCALAPPDATA 'OpenVoxKeys\settings.json'
    New-Item (Split-Path $settingsPath -Parent) -ItemType Directory -Force | Out-Null
    $settings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
    $settings | Add-Member -NotePropertyName StartWithWindows -NotePropertyValue $true -Force
    $settings | ConvertTo-Json -Depth 20 | Set-Content $settingsPath -Encoding UTF8
    New-ItemProperty -Path $run -Name OpenVoxKeys -Value ('"' + $link.TargetPath + '"') -PropertyType String -Force | Out-Null }
Write-Output "Installed: $target"
Write-Output 'Start menu -> Open Vox Keys. Open Settings from the system tray icon.'
