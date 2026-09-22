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
function InstallHelper([string]$mode, [string]$setup = '') {
    $extra = if ($setup) { @('-SetupExecutable', $setup) } else { @() }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $helper $mode @extra
    if ($LASTEXITCODE -ne 0) { throw "Installation helper failed: $mode" }
}
try {
    New-Item $data -ItemType Directory -Force | Out-Null
    '{"Language":"de","GatewayUrl":"http://localhost:9999","StartWithWindows":false}' | Set-Content $settings -Encoding UTF8
    New-Item $run -Force | Out-Null
    $setup = Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\setup\OpenVoxKeysSetup.exe'
    if (!(Test-Path $setup)) { throw 'BuildSetup.ps1 must complete before installation tests.' }
    InstallHelper '-Autostart' $setup
    $expected = '"' + (Join-Path $target 'OpenVoxKeys.exe') + '"'
    $config = Get-Content $settings -Raw | ConvertFrom-Json
    if (!(Test-Path (Join-Path $target 'OpenVoxKeys.exe')) -or !(Test-Path $shortcut) -or !$config.StartWithWindows -or $config.Language -ne 'de' -or (Get-ItemProperty $run).OpenVoxKeys -ne $expected) { throw 'Fresh installation/autostart checks failed.' }
    # A locked old DLL must not leave a mixed old/new application.
    $package = Split-Path (Split-Path $helper -Parent) -Parent
    $probe = 'A-upgrade-probe.txt'
    Set-Content (Join-Path $target $probe) 'old'
    Set-Content (Join-Path $package $probe) 'new'
    $locked = [IO.File]::Open((Join-Path $target 'OpenVoxKeys.dll'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        $errorFile = Join-Path $env:TEMP 'ovk-expected-install-error.txt'
        $p = Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$helper+'"'),'-DisableAutostart') -PassThru -Wait -RedirectStandardError $errorFile
        if ($p.ExitCode -eq 0 -or (Get-Content (Join-Path $target $probe)).Trim() -ne 'old' -or !(Test-Path (Join-Path $target 'OpenVoxKeys.exe'))) { throw 'Failed upgrade damaged the previous installation.' }
        $p = Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$helper+'"'),'-Uninstall') -PassThru -Wait -RedirectStandardError $errorFile
        if ($p.ExitCode -eq 0 -or !(Test-Path (Join-Path $target 'OpenVoxKeys.exe')) -or !(Test-Path $shortcut)) { throw 'Failed removal damaged the previous installation.' }
    }
    finally { $locked.Dispose(); Remove-Item (Join-Path $package $probe) -Force }
    # Late integration failure must restore files AND registration, even when
    # restoring the still-locked shortcut itself cannot run until its owner closes.
    $lockedShortcut = [IO.File]::Open($shortcut, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        $p = Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$helper+'"'),'-Uninstall') -PassThru -Wait -RedirectStandardError $errorFile
        if ($p.ExitCode -eq 0 -or !(Test-Path (Join-Path $target 'OpenVoxKeys.exe')) -or (Get-ItemProperty $run).OpenVoxKeys -ne $expected -or (Get-ItemProperty $uninstall).DisplayName -ne 'Open Vox Keys') { throw 'Late uninstall failure did not restore application and registration.' }
    }
    finally { $lockedShortcut.Dispose() }
    # Corrupt settings are detected before any upgrade mutation.
    $settingsBytes = [IO.File]::ReadAllBytes($settings)
    try {
        Set-Content $settings '{broken-json'
        $p = Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$helper+'"'),'-Autostart') -PassThru -Wait -RedirectStandardError $errorFile
        if ($p.ExitCode -eq 0 -or (Get-Content (Join-Path $target $probe)).Trim() -ne 'old') { throw 'Corrupt settings did not stop upgrade before replacement.' }
    }
    finally { [IO.File]::WriteAllBytes($settings, $settingsBytes) }
    InstallHelper '-DisableAutostart'
    $config = Get-Content $settings -Raw | ConvertFrom-Json
    if ($config.StartWithWindows -or $config.GatewayUrl -ne 'http://localhost:9999' -or (Get-ItemProperty $run -Name OpenVoxKeys -ErrorAction SilentlyContinue)) { throw 'Upgrade/preferences/autostart-off checks failed.' }
    if (!(Test-Path (Join-Path $target 'OpenVoxKeysSetup.exe')) -or (Get-ItemProperty $uninstall).DisplayName -ne 'Open Vox Keys') { throw 'Graphical setup registration was not preserved during upgrade.' }
    InstallHelper '-Uninstall'
    if ((Test-Path $target) -or (Test-Path $shortcut) -or (Test-Path $uninstall) -or !(Test-Path $settings)) { throw 'Removal or personal-data retention checks failed.' }
    Write-Output 'PASS installation, failed upgrade/removal preservation, corrupt-settings preflight, autostart on/off, settings preservation, removal and data retention.'
}
finally {
    if (Test-Path $target) { InstallHelper '-Uninstall' }
    Remove-Item $settings -ErrorAction SilentlyContinue
}
