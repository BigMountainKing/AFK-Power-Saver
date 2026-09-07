[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$env:DOTNET_CLI_HOME = Join-Path $repositoryRoot '.dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
& (Join-Path $PSScriptRoot 'Build-AmdAdlxBridge.ps1') -RunTests
& dotnet build (Join-Path $repositoryRoot 'EcoPause.sln') -c Release --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'The release build failed.' }
foreach ($suite in @('EcoPause.Core.Tests', 'EcoPause.Hardware.Tests', 'EcoPause.Lifecycle.Tests')) {
    & dotnet run --project (Join-Path $repositoryRoot "tests\$suite") -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "$suite failed." }
}
