# Per-user installation of a published portable build. No administrator required.
param([switch]$Uninstall, [switch]$Autostart, [switch]$DisableAutostart, [string]$SetupExecutable = '')
$ErrorActionPreference = 'Stop'
if ($Autostart -and $DisableAutostart) { throw 'Choose only one autostart option.' }
$target = Join-Path $env:LOCALAPPDATA 'Programs\OpenVoxKeys'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Open Vox Keys.lnk'
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\OpenVoxKeys'
$settingsPath = Join-Path $env:LOCALAPPDATA 'OpenVoxKeys\settings.json'
$mutex = New-Object System.Threading.Mutex($false, 'Local\OpenVoxKeys.Install')
$owned = $false
function Restore-File($path, $bytes) {
    if ($null -eq $bytes) { if (Test-Path $path) { Remove-Item $path -Force } }
    else { [IO.File]::WriteAllBytes($path, $bytes) }
}
function Remove-RunEntry {
    if (Test-Path $run) {
        $item = Get-Item $run
        try { if ($item.GetValueNames() -contains 'OpenVoxKeys') { Remove-ItemProperty $run -Name OpenVoxKeys } }
        finally { $item.Close() }
    }
}

try {
    try { $owned = $mutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $owned = $true }
    if (!$owned) { throw 'Another Open Vox Keys installation or removal is already running.' }
    $active = Get-Process OpenVoxKeys,VoicePoc -ErrorAction SilentlyContinue | Where-Object { $_.Path -in @((Join-Path $target 'OpenVoxKeys.exe'), (Join-Path $target 'VoicePoc.exe')) }
    if ($active) { throw 'Quit Open Vox Keys from the tray menu before installing or uninstalling.' }

    # Snapshot only this product's integration state for rollback.
    $shortcutBefore = if (Test-Path $shortcut) { [IO.File]::ReadAllBytes($shortcut) } else { $null }
    $settingsBefore = if (Test-Path $settingsPath) { [IO.File]::ReadAllBytes($settingsPath) } else { $null }
    $runItem = Get-Item $run -ErrorAction SilentlyContinue
    $runBefore = if ($runItem -and $runItem.GetValueNames() -contains 'OpenVoxKeys') { @{ Value=$runItem.GetValue('OpenVoxKeys'); Kind=$runItem.GetValueKind('OpenVoxKeys') } } else { $null }
    if ($runItem) { $runItem.Close() }
    $uninstallBefore = $null
    if (Test-Path $uninstallKey) {
        $uninstallBefore = @{}
        $key = Get-Item $uninstallKey
        try { foreach ($name in $key.GetValueNames()) { $uninstallBefore[$name] = @{ Value=$key.GetValue($name); Kind=$key.GetValueKind($name) } } }
        finally { $key.Close() }
    }
    $parent = Split-Path $target -Parent
    New-Item $parent -ItemType Directory -Force | Out-Null
    $stage = Join-Path $parent ('OpenVoxKeys-stage-' + [guid]::NewGuid().ToString('N'))
    $backup = Join-Path $parent ('OpenVoxKeys-backup-' + [guid]::NewGuid().ToString('N'))
    $reserved = $false; $installed = $false; $integrationStarted = $false
    try {
        if (!$Uninstall) {
            $source = Split-Path $PSScriptRoot -Parent
            if (!(Test-Path (Join-Path $source 'OpenVoxKeys.exe'))) { throw 'Run Install.ps1 from the extracted Windows package.' }
            $settings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
            if ($null -eq $settings -or $settings -isnot [pscustomobject]) { throw 'Existing settings are not a JSON object. Recover them before installing.' }
            New-Item $stage -ItemType Directory | Out-Null
            Copy-Item (Join-Path $source '*') $stage -Recurse -Force
            $existingSetup = Join-Path $target 'OpenVoxKeysSetup.exe'
            if ($SetupExecutable) { Copy-Item $SetupExecutable (Join-Path $stage 'OpenVoxKeysSetup.exe') -Force }
            elseif (Test-Path $existingSetup) { Copy-Item $existingSetup $stage }
        }
        # Directory.Move is a rename. PowerShell Move-Item can move children
        # individually and leave a partially moved application on a file lock.
        if (Test-Path $target) { [IO.Directory]::Move($target, $backup); $reserved = $true }
        if (!$Uninstall) { [IO.Directory]::Move($stage, $target); $installed = $true }
        $integrationStarted = $true
        if ($Uninstall) {
            Remove-RunEntry
            if (Test-Path $uninstallKey) { Remove-Item $uninstallKey -Recurse }
            if (Test-Path $shortcut) { Remove-Item $shortcut }
        }
        else {
            New-Item (Split-Path $shortcut -Parent) -ItemType Directory -Force | Out-Null
            $shell = New-Object -ComObject WScript.Shell
            $link = $shell.CreateShortcut($shortcut)
            $link.TargetPath = Join-Path $target 'OpenVoxKeys.exe'; $link.WorkingDirectory = $target; $link.Save()
            if ($Autostart -or $DisableAutostart) {
                New-Item (Split-Path $settingsPath -Parent) -ItemType Directory -Force | Out-Null
                $settings | Add-Member -NotePropertyName StartWithWindows -NotePropertyValue ([bool]$Autostart) -Force
                $settings | ConvertTo-Json -Depth 20 | Set-Content $settingsPath -Encoding UTF8
                if ($Autostart) {
                    if (!(Test-Path $run)) { New-Item $run -Force | Out-Null }
                    New-ItemProperty -Path $run -Name OpenVoxKeys -Value ('"' + $link.TargetPath + '"') -PropertyType String -Force | Out-Null
                }
                else { Remove-RunEntry }
            }
            if ($SetupExecutable) {
                New-Item $uninstallKey -Force | Out-Null
                $properties = @{
                    DisplayName='Open Vox Keys'; DisplayVersion=(Get-Item $link.TargetPath).VersionInfo.ProductVersion;
                    Publisher='Christian Haberl'; InstallLocation=$target; DisplayIcon=$link.TargetPath;
                    UninstallString=('"' + (Join-Path $target 'OpenVoxKeysSetup.exe') + '" --uninstall');
                    URLInfoAbout='https://github.com/christianhaberl/open-vox-keys'
                }
                foreach ($name in $properties.Keys) { New-ItemProperty $uninstallKey -Name $name -Value $properties[$name] -PropertyType String -Force | Out-Null }
                foreach ($name in @('NoModify','NoRepair')) { New-ItemProperty $uninstallKey -Name $name -Value 1 -PropertyType DWord -Force | Out-Null }
            }
        }
    }
    catch {
        $failure = $_
        $rollbackErrors = @()
        $restoreSteps = @(
            { if ($installed) { [IO.Directory]::Move($target, $stage) } },
            { if ($reserved) { [IO.Directory]::Move($backup, $target) } },
            { if ($integrationStarted) { Restore-File $shortcut $shortcutBefore } },
            { if ($integrationStarted -and !$Uninstall -and ($Autostart -or $DisableAutostart)) { Restore-File $settingsPath $settingsBefore } },
            { if ($integrationStarted) {
                if ($runBefore) { New-ItemProperty $run -Name OpenVoxKeys -Value $runBefore.Value -PropertyType $runBefore.Kind -Force | Out-Null }
                else { Remove-RunEntry }
            } },
            { if ($integrationStarted) {
                if (Test-Path $uninstallKey) { Remove-Item $uninstallKey -Recurse }
                if ($null -ne $uninstallBefore) {
                    New-Item $uninstallKey -Force | Out-Null
                    foreach ($name in $uninstallBefore.Keys) { New-ItemProperty $uninstallKey -Name $name -Value $uninstallBefore[$name].Value -PropertyType $uninstallBefore[$name].Kind -Force | Out-Null }
                }
            } }
        )
        foreach ($restore in $restoreSteps) { try { & $restore } catch { $rollbackErrors += $_.Exception.Message } }
        if ($rollbackErrors.Count) { throw "Installation failed; rollback needs attention. Check $target and preserve $backup if present. $($failure.Exception.Message) / $($rollbackErrors -join '; ')" }
        throw $failure
    }
    finally {
        if (Test-Path $stage) { try { Remove-Item $stage -Recurse -Force } catch { Write-Warning "Temporary files remain at $stage" } }
    }
    if (Test-Path $backup) { try { Remove-Item $backup -Recurse -Force } catch { Write-Warning "Old files remain at $backup because they are in use." } }
    if ($Uninstall) { Write-Output 'Open Vox Keys uninstalled. Personal settings and recordings have been retained.' }
    else { Write-Output "Installed: $target"; Write-Output 'Start menu -> Open Vox Keys. Open Settings from the system tray icon.' }
}
finally { if ($owned) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
