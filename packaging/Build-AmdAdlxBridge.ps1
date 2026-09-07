[CmdletBinding()]
param([switch] $RunTests)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourcePath = Join-Path $repositoryRoot 'native\AFKPowerSaver.AmdAdlx.Native\AmdAdlxBridge.cpp'
$outputDirectory = Join-Path $repositoryRoot 'native\AFKPowerSaver.AmdAdlx.Native\bin\x64\Release'
$outputPath = Join-Path $outputDirectory 'AFKPowerSaver.AmdAdlx.Native.dll'
$objectPath = Join-Path $outputDirectory 'AFKPowerSaver.AmdAdlx.Native.obj'
$importLibraryPath = Join-Path $outputDirectory 'AFKPowerSaver.AmdAdlx.Native.lib'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'

if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Build Tools discovery is unavailable.'
}

$visualStudio = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $visualStudio) {
    throw 'The Visual Studio Desktop development with C++ workload is required to build AMD support.'
}

$developerCommand = Join-Path $visualStudio 'Common7\Tools\VsDevCmd.bat'
if (-not (Test-Path -LiteralPath $developerCommand)) {
    throw 'The Visual Studio C++ developer command environment is unavailable.'
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$compileCommand = 'call "{0}" -arch=x64 -host_arch=x64 >nul && cl.exe /nologo /std:c++20 /O2 /guard:cf /GS /EHsc /LD /MT /Fo:"{2}" "{1}" /link /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /IMPLIB:"{3}" /OUT:"{4}"' -f $developerCommand, $sourcePath, $objectPath, $importLibraryPath, $outputPath
& $env:ComSpec /d /s /c $compileCommand
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $outputPath)) {
    throw "Compiling the AMD ADLX bridge failed with exit code $LASTEXITCODE."
}

Write-Output "AMD ADLX bridge: $outputPath"

if ($RunTests) {
    $testSource = Join-Path $repositoryRoot 'tests\native\AdlxBridgeTests.cpp'
    $testObject = Join-Path $outputDirectory 'AdlxBridgeTests.obj'
    $testExecutable = Join-Path $outputDirectory 'AdlxBridgeTests.exe'
    $testImportLibrary = Join-Path $outputDirectory 'AdlxBridgeTests.lib'
    $testCompile = 'call "{0}" -arch=x64 -host_arch=x64 >nul && cl.exe /nologo /std:c++20 /O2 /EHsc /MT /Fo:"{2}" "{1}" /link /IMPLIB:"{4}" /OUT:"{3}"' -f $developerCommand, $testSource, $testObject, $testExecutable, $testImportLibrary
    & $env:ComSpec /d /s /c $testCompile
    if ($LASTEXITCODE -ne 0) { throw 'AMD contract test compilation failed.' }
    & $testExecutable
    if ($LASTEXITCODE -ne 0) { throw 'AMD default-query contract tests failed.' }
}
