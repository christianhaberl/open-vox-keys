# Run only on a disposable Windows CI runner, after Publish.ps1.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Installation lifecycle test requires a disposable GitHub Actions runner.' }
$target = Join-Path $env:LOCALAPPDATA 'Programs\OpenVoxKeys'
$data = Join-Path $env:LOCALAPPDATA 'OpenVoxKeys'
$settings = Join-Path $data 'settings.json'
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstall = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\OpenVoxKeys'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Open Vox Keys.lnk'
$helper = Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\OpenVoxKeys-win-x64\tools\Install.ps1'
if ((Test-Path $target) -or (Test-Path $settings) -or (Test-Path $uninstall) -or (Test-Path $shortcut) -or (Get-ItemProperty $run -Name OpenVoxKeys -ErrorAction SilentlyContinue)) { throw 'Existing installation/configuration found; refusing to overwrite it.' }
function InstallHelper([string]$mode) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $helper $mode
    if ($LASTEXITCODE -ne 0) { throw "Installation helper failed: $mode" }
}
try {
    New-Item $data -ItemType Directory -Force | Out-Null
    '{"Language":"de","GatewayUrl":"http://localhost:9999","StartWithWindows":false}' | Set-Content $settings -Encoding UTF8
    New-Item $run -Force | Out-Null
    InstallHelper '-Autostart'
    $expected = '"' + (Join-Path $target 'OpenVoxKeys.exe') + '"'
    $config = Get-Content $settings -Raw | ConvertFrom-Json
    if (!(Test-Path (Join-Path $target 'OpenVoxKeys.exe')) -or !(Test-Path $shortcut) -or !$config.StartWithWindows -or $config.Language -ne 'de' -or (Get-ItemProperty $run).OpenVoxKeys -ne $expected) { throw 'Fresh installation/autostart checks failed.' }
    InstallHelper '-DisableAutostart'
    $config = Get-Content $settings -Raw | ConvertFrom-Json
    if ($config.StartWithWindows -or $config.GatewayUrl -ne 'http://localhost:9999' -or (Get-ItemProperty $run -Name OpenVoxKeys -ErrorAction SilentlyContinue)) { throw 'Upgrade/preferences/autostart-off checks failed.' }
    # GUI setup registers this key; removal must remove it too.
    New-Item $uninstall -Force | Out-Null
    InstallHelper '-Uninstall'
    if ((Test-Path $target) -or (Test-Path $shortcut) -or (Test-Path $uninstall) -or !(Test-Path $settings)) { throw 'Removal or personal-data retention checks failed.' }
    Write-Output 'PASS installation, autostart on/off, settings preservation, removal and data retention.'
}
finally {
    if (Test-Path $target) { InstallHelper '-Uninstall' }
    Remove-Item $settings -ErrorAction SilentlyContinue
}
