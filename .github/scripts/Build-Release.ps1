#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z')]
    [string] $Version,

    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64')]
    [string] $RuntimeIdentifier,

    [Parameter(Mandatory)]
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'

$windowsTarget = $RuntimeIdentifier -eq 'win-x64'
if (($windowsTarget -and -not $IsWindows) -or (-not $windowsTarget -and -not $IsLinux)) {
    throw "Run this packaging script on the target OS to verify that the $RuntimeIdentifier executable starts."
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).ProviderPath
$project = Join-Path $repoRoot 'src/VsixDownloader/VsixDownloader.csproj'
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    if (-not (Test-Path -LiteralPath $output -PathType Container) -or (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1)) {
        throw "Use a new or empty output directory: $output"
    }
}

$bundle = Join-Path $output 'bundle'
$assets = Join-Path $output 'assets'
$suffix = if ($windowsTarget) { '.exe' } else { '' }
$publishedName = "vsix-downloader$suffix"
$assetName = "vsix-downloader-$RuntimeIdentifier$suffix"

dotnet publish $project --configuration Release "-p:PublishProfile=$RuntimeIdentifier" "-p:Version=$Version" -p:ContinuousIntegrationBuild=true --output $bundle
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with code $LASTEXITCODE." }

$files = @(Get-ChildItem -LiteralPath $bundle -Force)
if ($files.Count -ne 1 -or $files[0].PSIsContainer -or $files[0].Name -cne $publishedName) {
    throw "Expected exactly one executable named $publishedName (and no sidecar files) in the publish directory."
}

# Redirected input exercises startup without entering the interactive menu or using the network.
$startInfo = [Diagnostics.ProcessStartInfo]::new($files[0].FullName)
$startInfo.WorkingDirectory = $bundle
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardError = $true
$process = [Diagnostics.Process]::Start($startInfo)
try {
    $process.StandardInput.Close()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(15000)) {
        $process.Kill()
        throw 'The executable did not exit in non-interactive mode.'
    }
    if ($process.ExitCode -ne 1 -or $stderr.GetAwaiter().GetResult() -notmatch '^vsix-downloader ') {
        throw "The executable did not reach the expected startup guard (exit code $($process.ExitCode))."
    }
}
finally {
    $process.Dispose()
}

$null = New-Item -ItemType Directory -Path $assets
$exe = Join-Path $assets $assetName
Move-Item -LiteralPath $files[0].FullName -Destination $exe
$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $assets 'SHA256SUMS.txt'), "$hash  $assetName`n")
Write-Output "Release files are ready in $assets"
