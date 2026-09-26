param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectRoot "TCPTunnel.csproj"
$stagingDirectory = Join-Path $projectRoot "obj\SelfContainedPublish"
$publishDirectory = Join-Path $projectRoot "bin\$Configuration\net8.0-windows\win-x64\publish"
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

& dotnet publish $projectFile `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $stagingDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Self-contained publish failed with exit code $LASTEXITCODE."
}

$stagedExecutable = Join-Path $stagingDirectory "TCPTunnel.exe"
$staged = Get-Item -LiteralPath $stagedExecutable
if ($staged.Length -gt $maximumBytes) {
    throw ("Self-contained executable exceeds the hard 64 MiB limit: {0:N0} > {1:N0} bytes." -f $staged.Length, $maximumBytes)
}

& $stagedExecutable -self-test | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "Self-contained executable failed its self-test with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath $stagedExecutable -Destination $outputExecutable -Force

$result = Get-Item -LiteralPath $outputExecutable
Write-Host "Self-contained TCPTunnel build created: $($result.FullName)"
Write-Host ("Size: {0:N0} bytes ({1:N2} MiB)" -f $result.Length, ($result.Length / 1MB))
