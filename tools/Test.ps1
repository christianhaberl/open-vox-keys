param([string]$Executable = '')
$ErrorActionPreference = 'Stop'
if (!$Executable) { $Executable = Join-Path (Split-Path $PSScriptRoot -Parent) 'bin\Release\net10.0-windows\OpenVoxKeys.exe' }
foreach ($test in @('--selftest', '--test-session')) {
    $process = Start-Process -FilePath $Executable -ArgumentList $test -PassThru
    if (!$process.WaitForExit(60000)) { $process.Kill(); throw "Test timed out: $test" }
    if ($process.ExitCode -ne 0) { throw "Test failed: $test (exit $($process.ExitCode))" }
    Write-Output "Passed: $test"
}
