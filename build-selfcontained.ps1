param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$DotNetPath,
    [string]$VisualStudioPath,
    [string]$WindowsSdkPath
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $projectRoot 'build-tools.ps1')
$dotnetExecutable = Resolve-DotNetSdk $projectRoot $DotNetPath
Write-Host "Using .NET SDK: $dotnetExecutable"
$vcvars = Resolve-VisualCppToolchain $projectRoot $VisualStudioPath
$windowsBuildSdk = Resolve-WindowsBuildSdk $projectRoot $WindowsSdkPath
$projectFile = Join-Path $projectRoot "TCPTunnel.csproj"
$stagingDirectory = Join-Path $projectRoot "obj\SelfContainedPublish"
$publishDirectory = Join-Path $projectRoot "bin\$Configuration\net10.0-windows\win-x64\publish"
$outputExecutable = Join-Path $publishDirectory "TCPTunnel-selfcontained.exe"
$maximumBytes = 64 * 1024 * 1024

function Reset-BuildDirectory([string]$Path) {
    $objectRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot "obj"))
    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $requiredPrefix = $objectRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a directory outside the project obj folder: $resolvedPath"
    }
    if (Test-Path -LiteralPath $resolvedPath) {
        $item = Get-Item -LiteralPath $resolvedPath -Force
        if (-not $item.PSIsContainer -or ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw "Unexpected build directory type: $resolvedPath"
        }
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force -ErrorAction Stop
    }
    New-Item -ItemType Directory -Path $resolvedPath -Force | Out-Null
}

Reset-BuildDirectory $stagingDirectory
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

& $dotnetExecutable publish $projectFile `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $stagingDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Self-contained publish failed with exit code $LASTEXITCODE."
}

$payloadExecutable = Join-Path $stagingDirectory "TCPTunnel.exe"
$payloadHash = (Get-FileHash -LiteralPath $payloadExecutable -Algorithm SHA256).Hash
$payloadIdFile = Join-Path $stagingDirectory "payload.id"
[IO.File]::WriteAllText($payloadIdFile, $payloadHash, [Text.Encoding]::ASCII)
$compressedPayload = Join-Path $stagingDirectory "payload.bin"
Initialize-PayloadCompression
[TCPTunnelPayloadCompression]::Pack($payloadExecutable, $compressedPayload, 5, 128MB)

$resourceFile = Join-Path $stagingDirectory "TCPTunnel.Resources.rc"
$resourceLines = @(
    ('101 RCDATA "{0}"' -f (ConvertTo-RcPath $compressedPayload))
    ('105 RCDATA "{0}"' -f (ConvertTo-RcPath $payloadIdFile))
)
[IO.File]::WriteAllLines($resourceFile, $resourceLines, [Text.Encoding]::Unicode)
$bootstrapperSource = Join-Path $projectRoot "Bootstrapper\TCPTunnel.Bootstrapper.c"
$stagedExecutable = Join-Path $stagingDirectory "TCPTunnel-selfcontained.exe"
Build-PayloadBootstrapper $stagingDirectory $bootstrapperSource $resourceFile $stagedExecutable $vcvars $windowsBuildSdk '/DTCPTUNNEL_SELF_CONTAINED'
$staged = Get-Item -LiteralPath $stagedExecutable
if ($staged.Length -gt $maximumBytes) {
    throw ("Self-contained executable exceeds the hard 64 MiB limit: {0:N0} > {1:N0} bytes." -f $staged.Length, $maximumBytes)
}

& $stagedExecutable -self-test | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "Self-contained executable failed its self-test with exit code $LASTEXITCODE."
}
& $stagedExecutable -e2e-test | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Self-contained encrypted transport checks failed.' }

$cacheRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "TCPTunnel\self-contained"
$cachedExecutable = Join-Path $cacheRoot "$payloadHash\TCPTunnel.exe"
if ((Get-FileHash -LiteralPath $cachedExecutable -Algorithm SHA256).Hash -ne $payloadHash) {
    throw 'Self-contained payload failed SHA-256 verification.'
}

Copy-Item -LiteralPath $stagedExecutable -Destination $outputExecutable -Force

$result = Get-Item -LiteralPath $outputExecutable
$hash = (Get-FileHash -LiteralPath $outputExecutable -Algorithm SHA256).Hash
[IO.File]::WriteAllText($outputExecutable + '.sha256', "$hash  $($result.Name)`n", [Text.Encoding]::ASCII)
Write-Host "Self-contained TCPTunnel build created: $($result.FullName)"
Write-Host ("Size: {0:N0} bytes ({1:N2} MiB)" -f $result.Length, ($result.Length / 1MB))
