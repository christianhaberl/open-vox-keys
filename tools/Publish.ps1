param([ValidateSet('win-x64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$destination = Join-Path $root "dist\OpenVoxKeys-$Runtime"
if (Test-Path $destination) { Remove-Item $destination -Recurse -Force }
dotnet publish (Join-Path $root 'OpenVoxKeys.csproj') -c Release -r $Runtime --self-contained true -p:RestoreLockedMode=true -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
if (!(Test-Path (Join-Path $root 'LICENSE'))) { throw 'Release license missing' }
New-Item (Join-Path $destination 'tools') -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'Install.ps1') (Join-Path $destination 'tools\Install.ps1')
foreach ($name in @('README.md', 'LICENSE', 'CHANGELOG.md', 'THIRD-PARTY-NOTICES.md', 'licenses', 'docs')) { Copy-Item (Join-Path $root $name) $destination -Recurse -Force }
$zip = "$destination.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $destination '*') -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$hash  $([IO.Path]::GetFileName($zip))`n")
Write-Output "$hash  $zip"
