[CmdletBinding()]
param(
    [string] $SigningCertificateThumbprint = $env:AFKPOWERSAVER_SIGNING_CERT_THUMBPRINT,
    [string] $TimestampUrl = $env:AFKPOWERSAVER_TIMESTAMP_URL,
    [ValidateSet('CurrentUser', 'LocalMachine')]
    [string] $SigningCertificateStore = 'CurrentUser',
    [switch] $RequireSigning
)

$ErrorActionPreference = 'Stop'
$releaseVersion = '1.25.0'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnetHome = Join-Path $repositoryRoot '.dotnet-home'
$env:DOTNET_CLI_HOME = $dotnetHome
$env:NUGET_PACKAGES = Join-Path $dotnetHome 'packages'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$workRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "work\release-$releaseVersion"))
$publishRoot = Join-Path $workRoot 'publish'
$payloadRoot = Join-Path $workRoot 'payload'
$installTestRoot = Join-Path $workRoot 'install-test'
$uninstallTestRoot = Join-Path $workRoot 'uninstall-test'
$payloadArchive = Join-Path $workRoot 'AFKPowerSaver.Payload.zip'
$artifactRoot = Join-Path $repositoryRoot 'artifacts'
$setupPath = Join-Path $artifactRoot "AFK-Power-Saver-$releaseVersion-Setup.exe"
$releaseNotesPath = Join-Path $artifactRoot "AFK-Power-Saver-$releaseVersion-README.txt"

function Assert-ChildPath([string] $Path, [string] $Parent) {
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedParent = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $resolvedPath.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the release workspace: $resolvedPath"
    }
}

function Find-SignTool {
    $windowsSdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $candidate = Get-ChildItem -LiteralPath $windowsSdkBin -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $candidate) {
        throw 'The x64 Windows SDK SignTool is required for a signed release.'
    }
    return $candidate.FullName
}

function Resolve-SigningCertificate([string] $Thumbprint, [string] $StoreLocation) {
    $normalized = ($Thumbprint -replace '\s', '').ToUpperInvariant()
    if ($normalized -notmatch '^[0-9A-F]{40}$') {
        throw 'The signing certificate thumbprint must contain exactly 40 hexadecimal characters.'
    }

    $certificatePath = "Cert:\$StoreLocation\My\$normalized"
    $certificate = Get-Item -LiteralPath $certificatePath -ErrorAction SilentlyContinue
    if (-not $certificate -or -not $certificate.HasPrivateKey) {
        throw "No signing certificate with an accessible private key was found at $certificatePath."
    }
    if ($certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date)) {
        throw "The signing certificate at $certificatePath is not currently valid."
    }
    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    if (-not ($certificate.EnhancedKeyUsageList | Where-Object { $_.ObjectId.Value -eq $codeSigningOid })) {
        throw "The certificate at $certificatePath is not valid for code signing."
    }

    return [pscustomobject]@{
        Certificate = $certificate
        Thumbprint = $normalized
        StoreLocation = $StoreLocation
    }
}

function Invoke-AuthenticodeSign(
    [string] $Path,
    [string] $SignTool,
    [object] $SigningIdentity,
    [string] $Rfc3161TimestampUrl) {
    $arguments = @(
        'sign',
        '/fd', 'SHA256',
        '/sha1', $SigningIdentity.Thumbprint,
        '/s', 'My'
    )
    if ($SigningIdentity.StoreLocation -eq 'LocalMachine') {
        $arguments += '/sm'
    }
    $arguments += @(
        '/tr', $Rfc3161TimestampUrl,
        '/td', 'SHA256',
        '/d', 'AFK Power Saver',
        $Path
    )

    & $SignTool @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode signing failed for $Path with exit code $LASTEXITCODE."
    }

    & $SignTool verify /pa /all $Path
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode verification failed for $Path with exit code $LASTEXITCODE."
    }
}

$signingRequested = -not [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)
if ($RequireSigning -and -not $signingRequested) {
    throw 'A signed release was required, but no signing certificate thumbprint was supplied.'
}

$signTool = $null
$signingIdentity = $null
if ($signingRequested) {
    if ([string]::IsNullOrWhiteSpace($TimestampUrl)) {
        throw 'An RFC 3161 timestamp URL is required when signing a release.'
    }
    $timestampUri = $null
    if (-not [Uri]::TryCreate($TimestampUrl, [UriKind]::Absolute, [ref] $timestampUri) -or
        $timestampUri.Scheme -notin @('http', 'https')) {
        throw 'The timestamp URL must be an absolute HTTP or HTTPS URI supplied by the certificate provider.'
    }
    $signTool = Find-SignTool
    $signingIdentity = Resolve-SigningCertificate $SigningCertificateThumbprint $SigningCertificateStore
    Write-Output "Signing identity: $($signingIdentity.Certificate.Subject)"
}

Assert-ChildPath $workRoot (Join-Path $repositoryRoot 'work')
if (Test-Path -LiteralPath $workRoot) {
    Remove-Item -LiteralPath $workRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $dotnetHome, $env:NUGET_PACKAGES, $publishRoot, $payloadRoot, $artifactRoot -Force | Out-Null

& (Join-Path $PSScriptRoot 'Test-Safety.ps1')
if ($LASTEXITCODE -ne 0) {
    throw "Building the AMD ADLX bridge failed with exit code $LASTEXITCODE."
}

$projects = [ordered]@{
    CpuRecovery = 'src\AFKPowerSaver.CpuRecovery\AFKPowerSaver.CpuRecovery.csproj'
    Desktop = 'src\EcoPause.Desktop\EcoPause.Desktop.csproj'
    Probe = 'src\EcoPause.Probe\EcoPause.Probe.csproj'
    LiveSession = 'src\EcoPause.LiveRecoveryDrill\EcoPause.LiveRecoveryDrill.csproj'
    ElevatedHost = 'src\EcoPause.ElevatedHost.LiveRecoveryDrill\EcoPause.ElevatedHost.LiveRecoveryDrill.csproj'
}

& dotnet restore (Join-Path $repositoryRoot 'EcoPause.sln')
if ($LASTEXITCODE -ne 0) {
    throw "Restoring the package-free solution failed with exit code $LASTEXITCODE."
}

foreach ($entry in $projects.GetEnumerator()) {
    $destination = Join-Path $publishRoot $entry.Key
    & dotnet publish (Join-Path $repositoryRoot $entry.Value) `
        --configuration Release `
        --self-contained false `
        --no-restore `
        -p:DebugSymbols=false `
        -p:DebugType=None `
        --output $destination
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing $($entry.Key) failed with exit code $LASTEXITCODE."
    }
}

$requiredExecutables = @(
    'AFKPowerSaver.exe',
    'AFKPowerSaver.CpuRecovery.exe',
    'AFKPowerSaver.Probe.exe',
    'AFKPowerSaver.LiveSession.exe',
    'AFKPowerSaver.ElevatedHost.exe'
)
$requiredNativeLibraries = @(
    'AFKPowerSaver.AmdAdlx.Native.dll'
)

foreach ($source in Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Where-Object Extension -ne '.pdb') {
    $destination = Join-Path $payloadRoot $source.Name
    if (Test-Path -LiteralPath $destination) {
        $sourceHash = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash
        $destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        if ($sourceHash -ne $destinationHash) {
            throw "Published components disagree about shared runtime file $($source.Name)."
        }
    }
    else {
        Copy-Item -LiteralPath $source.FullName -Destination $destination
    }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.txt') -Destination (Join-Path $payloadRoot 'README.txt')

if ($signingRequested) {
    $payloadSignTargets = Get-ChildItem -LiteralPath $payloadRoot -File |
        Where-Object { $_.Extension -in @('.exe', '.dll') } |
        Sort-Object FullName
    foreach ($target in $payloadSignTargets) {
        Invoke-AuthenticodeSign $target.FullName $signTool $signingIdentity $TimestampUrl
    }
    Write-Output "Signed and verified payload files: $($payloadSignTargets.Count)"
}

$payloadFiles = Get-ChildItem -LiteralPath $payloadRoot -File
if ($payloadFiles.Name -match '\.pdb$') {
    throw 'The release payload must not contain debug symbol files.'
}
foreach ($executable in $requiredExecutables) {
    if (-not (Test-Path -LiteralPath (Join-Path $payloadRoot $executable))) {
        throw "The release payload is missing $executable."
    }
}
foreach ($library in $requiredNativeLibraries) {
    if (-not (Test-Path -LiteralPath (Join-Path $payloadRoot $library))) {
        throw "The release payload is missing $library."
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory(
    $payloadRoot,
    $payloadArchive,
    [IO.Compression.CompressionLevel]::Optimal,
    $false)

$compilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) {
    throw 'The Windows C# setup compiler is unavailable.'
}

& $compiler `
    /nologo `
    /target:winexe `
    /platform:anycpu `
    /optimize+ `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.IO.Compression.dll `
    /reference:System.IO.Compression.FileSystem.dll `
    /reference:System.Windows.Forms.dll `
    /reference:Microsoft.CSharp.dll `
    "/resource:$payloadArchive,AFKPowerSaver.Payload.zip" `
    "/out:$setupPath" `
    (Join-Path $PSScriptRoot 'AFKPowerSaver.Setup\Program.cs')
if ($LASTEXITCODE -ne 0) {
    throw "Compiling setup failed with exit code $LASTEXITCODE."
}

if ($signingRequested) {
    Invoke-AuthenticodeSign $setupPath $signTool $signingIdentity $TimestampUrl
    Write-Output 'Signed and verified the outer setup executable.'
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.txt') -Destination $releaseNotesPath -Force
$setupTest = Start-Process `
    -FilePath $setupPath `
    -ArgumentList @('--test-install', ('"' + $installTestRoot + '"')) `
    -WindowStyle Hidden `
    -PassThru `
    -Wait
if ($setupTest.ExitCode -ne 0) {
    throw "The setup installation smoke test failed with exit code $($setupTest.ExitCode)."
}

$installedFiles = Get-ChildItem -LiteralPath $installTestRoot -File
$expectedInstalledFileCount = $payloadFiles.Count + 2 # installed uninstaller and test safety marker
if ($installedFiles.Count -ne $expectedInstalledFileCount) {
    throw "The setup installed $($installedFiles.Count) files but $expectedInstalledFileCount were expected."
}
foreach ($executable in $requiredExecutables) {
    if (-not (Test-Path -LiteralPath (Join-Path $installTestRoot $executable))) {
        throw "The setup smoke test did not install $executable."
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $installTestRoot 'Uninstall AFK Power Saver.exe'))) {
    throw 'The setup smoke test did not install its uninstaller.'
}

$installedApplication = Join-Path $installTestRoot 'AFKPowerSaver.exe'
$companionTest = Start-Process -FilePath (Join-Path $installTestRoot 'AFKPowerSaver.CpuRecovery.exe') `
    -ArgumentList '--self-test' -WindowStyle Hidden -PassThru -Wait
if ($companionTest.ExitCode -ne 0) { throw 'The packaged CPU companion self-test failed.' }
$smokeModes = @('ACTIVITY', 'HOTKEY', 'UI', 'CLOSE', 'TRAY')
foreach ($smokeMode in $smokeModes) {
    $variableName = "AFKPOWERSAVER_${smokeMode}_SMOKE_TEST"
    $previousValue = [Environment]::GetEnvironmentVariable($variableName, 'Process')
    try {
        [Environment]::SetEnvironmentVariable($variableName, '1', 'Process')
        $smokeProcess = Start-Process `
            -FilePath $installedApplication `
            -WorkingDirectory $installTestRoot `
            -WindowStyle Hidden `
            -PassThru `
            -Wait
        if ($smokeProcess.ExitCode -ne 0) {
            throw "$smokeMode packaged smoke test failed with exit code $($smokeProcess.ExitCode)."
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable($variableName, $previousValue, 'Process')
    }
}

$uninstallSetupTest = Start-Process `
    -FilePath $setupPath `
    -ArgumentList @('--test-install', ('"' + $uninstallTestRoot + '"')) `
    -WindowStyle Hidden `
    -PassThru `
    -Wait
if ($uninstallSetupTest.ExitCode -ne 0) {
    throw "The uninstall round-trip setup failed with exit code $($uninstallSetupTest.ExitCode)."
}
$uninstallTest = Start-Process `
    -FilePath $setupPath `
    -ArgumentList @('--test-uninstall', ('"' + $uninstallTestRoot + '"')) `
    -WindowStyle Hidden `
    -PassThru `
    -Wait
if ($uninstallTest.ExitCode -ne 0 -or (Test-Path -LiteralPath $uninstallTestRoot)) {
    throw "The setup uninstall smoke test failed with exit code $($uninstallTest.ExitCode)."
}

$hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash
Write-Output "Release candidate: $setupPath"
Write-Output "Installed payload files: $($installedFiles.Count)"
Write-Output "Packaged desktop smoke modes: $($smokeModes -join ', ') PASS"
Write-Output 'Install/uninstall round trip: PASS'
Write-Output "Authenticode: $(if ($signingRequested) { 'SIGNED AND VERIFIED' } else { 'unsigned development build' })"
Write-Output "SHA256: $hash"
