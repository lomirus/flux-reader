[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Version,

    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
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
$prerequisiteDirectory = Join-Path $setupRoot 'prerequisites'
$setupOutputDirectory = Join-Path $setupRoot 'output'
$installerDirectory = Join-Path $repositoryRoot 'artifacts\installers'
$bundleIcon = Join-Path $repositoryRoot 'assets\brand\fluxreader-icon.ico'

# Pin immutable Microsoft payloads so an existing Setup.exe keeps matching the
# hashes WiX records even after the public "latest" aliases move forward.
$prerequisiteDownloadUrls = @{
    x64 = @{
        DotNet = 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.11/dotnet-runtime-10.0.11-win-x64.exe'
        VCRedist = 'https://download.visualstudio.microsoft.com/download/pr/ebdab8e5-1d7b-4d9f-a11b-cbb1720c3b12/843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C/VC_redist.x64.exe'
        WindowsAppRuntime = 'https://download.microsoft.com/download/097dbd99-ea76-49de-994b-eb935c72dcf1/WindowsAppRuntimeInstall-x64.exe'
    }
    arm64 = @{
        DotNet = 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.11/dotnet-runtime-10.0.11-win-arm64.exe'
        VCRedist = 'https://download.visualstudio.microsoft.com/download/pr/355d2512-13c2-400a-bf9f-8a296abb5932/B70EF586669A620A0A30A1156969C05C6A3831DC8F8BC992DA75779D2A92F944/VC_redist.arm64.exe'
        WindowsAppRuntime = 'https://download.microsoft.com/download/2f7e2917-37ac-43a3-990e-73838adaf281/WindowsAppRuntimeInstall-arm64.exe'
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
    $prerequisiteDirectory,
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

function Save-RemoteInstaller {
    param(
        [Parameter(Mandatory = $true)][uri]$Uri,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $true
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('FluxReader-installer-build/1.0')

    try {
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            $response = $null

            try {
                $response = $client.GetAsync(
                    $Uri,
                    [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead
                ).GetAwaiter().GetResult()
                $response.EnsureSuccessStatusCode() | Out-Null

                $sourceStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                $destinationStream = [System.IO.File]::Create($Destination)
                try {
                    $sourceStream.CopyTo($destinationStream)
                }
                finally {
                    $destinationStream.Dispose()
                    $sourceStream.Dispose()
                }

                return $response.RequestMessage.RequestUri.AbsoluteUri
            }
            catch {
                if ($attempt -eq 3) {
                    throw
                }

                Write-Warning "Download attempt $attempt failed for '$Uri'. Retrying..."
                Start-Sleep -Seconds (2 * $attempt)
            }
            finally {
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
    Invoke-DotNet @(
        'restore', $appProject,
        '--runtime', $runtimeIdentifier,
        '--configfile', $nugetConfig,
        '-p:EnableMsixTooling=true',
        '-p:WindowsAppSDKSelfContained=false'
    )

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

    Invoke-DotNet @(
        'restore', $installerProject,
        '--configfile', $nugetConfig
    )

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

    $dotNetInstaller = Join-Path $prerequisiteDirectory "dotnet-runtime-win-$Architecture.exe"
    $vcRedistInstaller = Join-Path $prerequisiteDirectory "vc-redist-$Architecture.exe"
    $windowsAppRuntimeInstaller = Join-Path $prerequisiteDirectory "windows-app-runtime-$Architecture.exe"

    Write-Output "Downloading installer metadata for the online setup ($Architecture)..."
    $dotNetDownloadUrl = Save-RemoteInstaller `
        -Uri $prerequisiteDownloadUrls[$Architecture].DotNet `
        -Destination $dotNetInstaller
    $vcRedistDownloadUrl = Save-RemoteInstaller `
        -Uri $prerequisiteDownloadUrls[$Architecture].VCRedist `
        -Destination $vcRedistInstaller
    $windowsAppRuntimeDownloadUrl = Save-RemoteInstaller `
        -Uri $prerequisiteDownloadUrls[$Architecture].WindowsAppRuntime `
        -Destination $windowsAppRuntimeInstaller

    $vcRedistVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($vcRedistInstaller).FileVersion
    if ($vcRedistVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw "Unable to determine the Visual C++ Redistributable version from '$vcRedistInstaller'."
    }

    Invoke-DotNet @(
        'restore', $setupProject,
        '--configfile', $nugetConfig
    )

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
