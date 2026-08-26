[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Version,

    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$Offline
)

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 does not auto-load System.Net.Http when a script first
# references HttpClient types. PowerShell 7 already loads it, but Add-Type is
# harmless there and keeps the installer build compatible with both hosts.
Add-Type -AssemblyName System.Net.Http

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$runtimeIdentifier = "win-$Architecture"
$appProject = Join-Path $repositoryRoot 'src\FluxReader\FluxReader.csproj'
$installerProject = Join-Path $repositoryRoot 'installer\FluxReader.Installer\FluxReader.Installer.wixproj'
$setupProject = Join-Path $repositoryRoot 'installer\FluxReader.Setup\FluxReader.Setup.wixproj'
$nugetConfig = Join-Path $repositoryRoot 'NuGet.Config'
$runtimePublishRoot = Join-Path $repositoryRoot "artifacts\publish\$runtimeIdentifier"
$publishDirectory = Join-Path $runtimePublishRoot 'setup'
$setupRoot = Join-Path $repositoryRoot "artifacts\setup\$runtimeIdentifier\$Version"
$internalMsiDirectory = Join-Path $setupRoot 'application'
$prerequisiteCacheDirectory = Join-Path $repositoryRoot "artifacts\cache\prerequisites\$Architecture"
$setupOutputDirectory = Join-Path $setupRoot 'output'
$installerDirectory = Join-Path $repositoryRoot 'artifacts\installers'
$bundleIcon = Join-Path $repositoryRoot 'assets\brand\fluxreader-icon.ico'

# Pin immutable Microsoft payloads so an existing Setup.exe keeps matching the
# hashes WiX records even after the public "latest" aliases move forward. The
# explicit SHA-256 values also make the persistent build cache safe to reuse.
$prerequisitePayloads = @{
    x64 = @{
        DotNet = @{
            FileName = 'dotnet-runtime-win-x64.exe'
            Url = 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.11/dotnet-runtime-10.0.11-win-x64.exe'
            Sha256 = '33DE99EEDA0F06F4B4AD43A1FD23977343E1358F5DBB4B0D5E1B84850DC18AFC'
        }
        VCRedist = @{
            FileName = 'vc-redist-x64.exe'
            Url = 'https://download.visualstudio.microsoft.com/download/pr/ebdab8e5-1d7b-4d9f-a11b-cbb1720c3b12/843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C/VC_redist.x64.exe'
            Sha256 = '843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C'
        }
        WindowsAppRuntime = @{
            FileName = 'windows-app-runtime-x64.exe'
            Url = 'https://download.microsoft.com/download/097dbd99-ea76-49de-994b-eb935c72dcf1/WindowsAppRuntimeInstall-x64.exe'
            Sha256 = '851C35B0B0A59CE4C55F9171F601193322FC3413143B0DC3390EA11E14CFA7FC'
        }
    }
    arm64 = @{
        DotNet = @{
            FileName = 'dotnet-runtime-win-arm64.exe'
            Url = 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.11/dotnet-runtime-10.0.11-win-arm64.exe'
            Sha256 = 'F9C492A4D5E286641E6D9B697D730F3066194138CC83CD290B058D0961E067C9'
        }
        VCRedist = @{
            FileName = 'vc-redist-arm64.exe'
            Url = 'https://download.visualstudio.microsoft.com/download/pr/355d2512-13c2-400a-bf9f-8a296abb5932/B70EF586669A620A0A30A1156969C05C6A3831DC8F8BC992DA75779D2A92F944/VC_redist.arm64.exe'
            Sha256 = 'B70EF586669A620A0A30A1156969C05C6A3831DC8F8BC992DA75779D2A92F944'
        }
        WindowsAppRuntime = @{
            FileName = 'windows-app-runtime-arm64.exe'
            Url = 'https://download.microsoft.com/download/2f7e2917-37ac-43a3-990e-73838adaf281/WindowsAppRuntimeInstall-arm64.exe'
            Sha256 = '788665585DCBC2844E99483FDA27809A91C2F36235B799B104D6649B68EB61B0'
        }
    }
}
$windowsAppRuntimePackageVersion = '2.4.0.0'
$windowsAppRuntimeDdlmArchitectureCode = @{
    x64 = 'x6'
    arm64 = 'a6'
}[$Architecture]

$versionParts = $Version.Split('.') | ForEach-Object { [int]$_ }
if ($versionParts[0] -gt 255 -or $versionParts[1] -gt 255 -or $versionParts[2] -gt 65535) {
    throw "Version '$Version' exceeds Windows Installer limits (major/minor <= 255, patch <= 65535)."
}

$productCodeHasher = [System.Security.Cryptography.SHA256]::Create()
try {
    $productCodeHash = $productCodeHasher.ComputeHash(
        [System.Text.Encoding]::UTF8.GetBytes("FluxReader|$Architecture|$Version")
    )
}
finally {
    $productCodeHasher.Dispose()
}
$productCodeBytes = [byte[]]::new(16)
[System.Array]::Copy($productCodeHash, $productCodeBytes, $productCodeBytes.Length)
$productCode = [System.Guid]::new($productCodeBytes).ToString().ToUpperInvariant()

foreach ($directory in @($runtimePublishRoot, $setupRoot)) {
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
}

New-Item -ItemType Directory -Force -Path @(
    $publishDirectory,
    $internalMsiDirectory,
    $prerequisiteCacheDirectory,
    $setupOutputDirectory,
    $installerDirectory
) | Out-Null

foreach ($obsoleteArtifact in @(
    (Join-Path $installerDirectory "FluxReader-$Version-$Architecture.msi"),
    (Join-Path $installerDirectory "FluxReader-$Version-$Architecture.wixpdb"),
    (Join-Path $installerDirectory "FluxReader-$Version-$Architecture-framework-dependent.msi"),
    (Join-Path $installerDirectory "FluxReader-$Version-$Architecture-framework-dependent.wixpdb"),
    (Join-Path $installerDirectory "FluxReader-$Version-$Architecture-standalone.msi"),
    (Join-Path $installerDirectory "FluxReader-$Version-$Architecture-standalone.wixpdb")
)) {
    if (Test-Path -LiteralPath $obsoleteArtifact) {
        Remove-Item -LiteralPath $obsoleteArtifact -Force
    }
}

function Invoke-DotNet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE."
    }
}

function Test-FileSha256 {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedSha256
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    $actualSha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    return [string]::Equals(
        $actualSha256,
        $ExpectedSha256,
        [System.StringComparison]::OrdinalIgnoreCase
    )
}

function Get-CachedRemoteInstaller {
    param(
        [Parameter(Mandatory = $true)][uri]$Uri,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedSha256,
        [switch]$Offline
    )

    if (Test-FileSha256 -Path $Destination -ExpectedSha256 $ExpectedSha256) {
        Write-Output "Using cached prerequisite '$Destination'."
        return
    }

    $cachedFileExists = Test-Path -LiteralPath $Destination -PathType Leaf
    if ($Offline) {
        $reason = if ($cachedFileExists) { 'has an unexpected SHA-256 hash' } else { 'is missing' }
        throw "Offline build cannot continue because prerequisite '$Destination' $reason. Run the build once without -Offline to populate the cache."
    }

    if ($cachedFileExists) {
        Write-Warning "Discarding prerequisite with an unexpected SHA-256 hash: '$Destination'."
        Remove-Item -LiteralPath $Destination -Force
    }

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $true
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('FluxReader-installer-build/1.0')

    try {
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            $response = $null
            $temporaryPath = Join-Path `
                (Split-Path -Parent $Destination) `
                ([System.IO.Path]::GetRandomFileName())

            try {
                $response = $client.GetAsync(
                    $Uri,
                    [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead
                ).GetAwaiter().GetResult()
                $response.EnsureSuccessStatusCode() | Out-Null

                $sourceStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                $destinationStream = [System.IO.File]::Create($temporaryPath)
                try {
                    $sourceStream.CopyTo($destinationStream)
                }
                finally {
                    $destinationStream.Dispose()
                    $sourceStream.Dispose()
                }

                if (-not (Test-FileSha256 -Path $temporaryPath -ExpectedSha256 $ExpectedSha256)) {
                    throw "Downloaded prerequisite from '$Uri' did not match the expected SHA-256 hash."
                }

                Move-Item -LiteralPath $temporaryPath -Destination $Destination -Force
                Write-Output "Downloaded and cached prerequisite '$Destination'."
                return
            }
            catch {
                if ($attempt -eq 3) {
                    throw
                }

                Write-Warning "Download attempt $attempt failed for '$Uri'. Retrying..."
                Start-Sleep -Seconds (2 * $attempt)
            }
            finally {
                if (Test-Path -LiteralPath $temporaryPath) {
                    Remove-Item -LiteralPath $temporaryPath -Force
                }

                if ($null -ne $response) {
                    $response.Dispose()
                }
            }
        }
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }
}

Push-Location $repositoryRoot
try {
    if ($Offline) {
        Write-Output 'Offline build: skipping NuGet restore and requiring existing restore assets.'
    }
    else {
        Invoke-DotNet @(
            'restore', $appProject,
            '--runtime', $runtimeIdentifier,
            '--configfile', $nugetConfig,
            '-p:EnableMsixTooling=true',
            '-p:WindowsAppSDKSelfContained=false'
        )
    }

    Invoke-DotNet @(
        'publish', $appProject,
        '--configuration', $Configuration,
        '--runtime', $runtimeIdentifier,
        '--self-contained', 'false',
        '--no-restore',
        "-p:Version=$Version",
        '-p:EnableMsixTooling=true',
        '-p:WindowsAppSDKSelfContained=false',
        "-p:PublishDir=$publishDirectory"
    )

    $runtimeConfigPath = Join-Path $publishDirectory 'FluxReader.runtimeconfig.json'
    $requiredPublishFiles = @(
        (Join-Path $publishDirectory 'FluxReader.exe'),
        $runtimeConfigPath,
        (Join-Path $publishDirectory 'FluxReader.pri')
    )

    foreach ($requiredPublishFile in $requiredPublishFiles) {
        if (-not (Test-Path -LiteralPath $requiredPublishFile)) {
            throw "Required publish output was not produced at '$requiredPublishFile'."
        }
    }

    $runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
    $requiredDotNetVersion = [string]$runtimeConfig.runtimeOptions.framework.version
    if ($requiredDotNetVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw "Unable to determine the required .NET runtime version from '$runtimeConfigPath'."
    }

    if (-not $Offline) {
        Invoke-DotNet @(
            'restore', $installerProject,
            '--configfile', $nugetConfig
        )
    }

    $internalMsiOutputName = "FluxReader-$Version-$Architecture-application"
    Invoke-DotNet @(
        'build', $installerProject,
        '--configuration', $Configuration,
        '--no-restore',
        "-p:InstallerPlatform=$Architecture",
        "-p:ProductVersion=$Version",
        "-p:ProductCode=$productCode",
        "-p:InstallerOutputName=$internalMsiOutputName",
        "-p:PublishDir=$publishDirectory",
        "-p:OutputPath=$internalMsiDirectory"
    )

    $architecturePrerequisites = $prerequisitePayloads[$Architecture]
    $dotNetInstaller = Join-Path $prerequisiteCacheDirectory $architecturePrerequisites.DotNet.FileName
    $vcRedistInstaller = Join-Path $prerequisiteCacheDirectory $architecturePrerequisites.VCRedist.FileName
    $windowsAppRuntimeInstaller = Join-Path $prerequisiteCacheDirectory $architecturePrerequisites.WindowsAppRuntime.FileName

    Write-Output "Preparing prerequisite payloads for the online setup ($Architecture)..."
    Get-CachedRemoteInstaller `
        -Uri $architecturePrerequisites.DotNet.Url `
        -Destination $dotNetInstaller `
        -ExpectedSha256 $architecturePrerequisites.DotNet.Sha256 `
        -Offline:$Offline
    Get-CachedRemoteInstaller `
        -Uri $architecturePrerequisites.VCRedist.Url `
        -Destination $vcRedistInstaller `
        -ExpectedSha256 $architecturePrerequisites.VCRedist.Sha256 `
        -Offline:$Offline
    Get-CachedRemoteInstaller `
        -Uri $architecturePrerequisites.WindowsAppRuntime.Url `
        -Destination $windowsAppRuntimeInstaller `
        -ExpectedSha256 $architecturePrerequisites.WindowsAppRuntime.Sha256 `
        -Offline:$Offline

    $dotNetDownloadUrl = $architecturePrerequisites.DotNet.Url
    $vcRedistDownloadUrl = $architecturePrerequisites.VCRedist.Url
    $windowsAppRuntimeDownloadUrl = $architecturePrerequisites.WindowsAppRuntime.Url

    $vcRedistVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($vcRedistInstaller).FileVersion
    if ($vcRedistVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw "Unable to determine the Visual C++ Redistributable version from '$vcRedistInstaller'."
    }

    if (-not $Offline) {
        Invoke-DotNet @(
            'restore', $setupProject,
            '--configfile', $nugetConfig
        )
    }

    $internalMsiPath = Join-Path $internalMsiDirectory "$internalMsiOutputName.msi"
    Invoke-DotNet @(
        'build', $setupProject,
        '--configuration', $Configuration,
        '--no-restore',
        "-p:InstallerPlatform=$Architecture",
        "-p:ProductVersion=$Version",
        "-p:ApplicationMsi=$internalMsiPath",
        "-p:DotNetRuntimeInstaller=$dotNetInstaller",
        "-p:DotNetRuntimeDownloadUrl=$dotNetDownloadUrl",
        "-p:DotNetRuntimeVersion=$requiredDotNetVersion",
        "-p:VCRedistInstaller=$vcRedistInstaller",
        "-p:VCRedistDownloadUrl=$vcRedistDownloadUrl",
        "-p:VCRedistVersion=$vcRedistVersion",
        "-p:WindowsAppRuntimeInstaller=$windowsAppRuntimeInstaller",
        "-p:WindowsAppRuntimeDownloadUrl=$windowsAppRuntimeDownloadUrl",
        "-p:WindowsAppRuntimePackageVersion=$windowsAppRuntimePackageVersion",
        "-p:WindowsAppRuntimeDdlmArchitectureCode=$windowsAppRuntimeDdlmArchitectureCode",
        "-p:BundleIcon=$bundleIcon",
        "-p:OutputPath=$setupOutputDirectory"
    )
}
finally {
    Pop-Location
}

$builtSetupPath = Join-Path $setupOutputDirectory "FluxReaderSetup-$Version-$Architecture.exe"
$setupPath = Join-Path $installerDirectory "FluxReaderSetup-$Version-$Architecture.exe"

if (-not (Test-Path -LiteralPath $builtSetupPath)) {
    throw "Installer build completed without producing '$builtSetupPath'."
}

Copy-Item -LiteralPath $builtSetupPath -Destination $setupPath -Force

Write-Output $setupPath
