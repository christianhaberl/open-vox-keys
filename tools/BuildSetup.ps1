# Rebuild the payload here so an older ZIP cannot be mislabeled as a new installer.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'Publish.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Installer payload publication failed' }
[xml]$project = Get-Content (Join-Path $root 'OpenVoxKeys.csproj')
$version = $project.Project.PropertyGroup.Version
$destination = Join-Path $root 'dist\setup'
if (Test-Path $destination) { Remove-Item $destination -Recurse -Force }
dotnet publish (Join-Path $PSScriptRoot 'Setup\OpenVoxKeys.Setup.csproj') -c Release -r win-x64 --self-contained true "-p:Version=$version" -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
$exe = Join-Path $root "dist\OpenVoxKeys-$version-Setup.exe"
Copy-Item (Join-Path $destination 'OpenVoxKeysSetup.exe') $exe -Force
$process = Start-Process $exe -ArgumentList '--verify-package' -PassThru
if (!$process.WaitForExit(120000)) { $process.Kill(); throw 'Installer payload checks timed out' }
if ($process.ExitCode -ne 0) { throw 'Installer payload checks failed' }
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$exe.sha256", "$hash  $([IO.Path]::GetFileName($exe))`n")
Write-Output "Verified installer: $exe"
