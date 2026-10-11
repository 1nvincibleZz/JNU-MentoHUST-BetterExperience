param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskExecutable = Join-Path $taskOutput 'ReconnectionChecks.exe'
$taskRoot = Split-Path $PSScriptRoot
& $taskCompiler /nologo /target:exe /codepage:65001 "/out:$taskExecutable" (Join-Path $taskRoot 'AutomaticReconnection.cs') (Join-Path $taskRoot 'WindowsNetworkStatus.cs') (Join-Path $taskRoot 'EngineContract.cs') (Join-Path $PSScriptRoot 'ReconnectionChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Reconnection checks build failed' }
& $taskExecutable | Tee-Object -FilePath (Join-Path $taskOutput 'reconnection-checks.txt')
if ($LASTEXITCODE -ne 0) { throw 'Reconnection checks failed' }
