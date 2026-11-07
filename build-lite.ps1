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
Write-Host "Using Visual C++ tools: $vcvars"
$windowsBuildSdk = Resolve-WindowsBuildSdk $projectRoot $WindowsSdkPath
Write-Host "Using Windows SDK: $($windowsBuildSdk.Include)"
$projectFile = Join-Path $projectRoot "TCPTunnel.csproj"
$payloadDirectory = Join-Path $projectRoot "obj\LitePayload"
$bootstrapperDirectory = Join-Path $projectRoot "obj\LiteBootstrapper"
$publishDirectory = Join-Path $projectRoot "bin\$Configuration\net10.0-windows\win-x64\publish"
$bootstrapperSource = Join-Path $projectRoot "Bootstrapper\TCPTunnel.Bootstrapper.c"
$maximumLiteBytes = 32 * 1024 * 1024
$recordedBaselineBytes = 442368

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

Reset-BuildDirectory $payloadDirectory
Reset-BuildDirectory $bootstrapperDirectory
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

& $dotnetExecutable publish $projectFile `
    -c $Configuration `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=false `
    -p:UseAppHost=false `
    -o $payloadDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Managed payload publish failed with exit code $LASTEXITCODE."
}

$payloadFiles = [ordered]@{
    '101' = Join-Path $payloadDirectory "TCPTunnel.dll"
    '102' = Join-Path $payloadDirectory "TCPTunnel.deps.json"
    '103' = Join-Path $payloadDirectory "TCPTunnel.runtimeconfig.json"
    '104' = Join-Path $payloadDirectory "SharpOpenNat.dll"
    '106' = Join-Path $payloadDirectory "Microsoft.Windows.SDK.NET.dll"
    '107' = Join-Path $payloadDirectory "WinRT.Runtime.dll"
    '108' = Join-Path $payloadDirectory "tcptunnel_crypto.dll"
}
foreach ($payloadFile in $payloadFiles.Values) {
    if (-not (Test-Path -LiteralPath $payloadFile -PathType Leaf)) {
        throw "Required payload file is missing: $payloadFile"
    }
}

$payloadSignature = ($payloadFiles.GetEnumerator() | ForEach-Object {
    $file = Get-Item -LiteralPath $_.Value
    $hash = (Get-FileHash -LiteralPath $_.Value -Algorithm SHA256).Hash
    "$($_.Key):$($file.Length):$hash"
}) -join '|'
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $payloadDigest = $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($payloadSignature))
}
finally {
    $sha256.Dispose()
}
$payloadId = ([BitConverter]::ToString($payloadDigest).Replace('-', '')).Substring(0, 16)
$payloadIdFile = Join-Path $bootstrapperDirectory "payload.id"
[System.IO.File]::WriteAllText($payloadIdFile, $payloadId, [System.Text.Encoding]::ASCII)

Initialize-PayloadCompression

$resourceFile = Join-Path $bootstrapperDirectory "TCPTunnel.Resources.rc"
$resourceLines = foreach ($payload in $payloadFiles.GetEnumerator()) {
    $resourcePath = $payload.Value
    if ([IO.Path]::GetExtension($resourcePath) -eq '.dll') {
        $resourcePath = Join-Path $bootstrapperDirectory ($payload.Key + '.bin')
        [TCPTunnelPayloadCompression]::Pack($payload.Value, $resourcePath, 4, 32MB)
    }
    '{0} RCDATA "{1}"' -f $payload.Key, (ConvertTo-RcPath $resourcePath)
}
$resourceLines += '105 RCDATA "{0}"' -f (ConvertTo-RcPath $payloadIdFile)
$resourceText = $resourceLines -join [Environment]::NewLine
[System.IO.File]::WriteAllText($resourceFile, $resourceText, [System.Text.Encoding]::Unicode)

$outputExecutable = Join-Path $publishDirectory "TCPTunnel.exe"
$stagedExecutable = Join-Path $bootstrapperDirectory "TCPTunnel.exe"
Build-PayloadBootstrapper $bootstrapperDirectory $bootstrapperSource $resourceFile $stagedExecutable $vcvars $windowsBuildSdk

$stagedResult = Get-Item -LiteralPath $stagedExecutable
if ($stagedResult.Length -gt $maximumLiteBytes) {
    throw ("Lite executable exceeds the hard 32 MiB limit: {0:N0} > {1:N0} bytes." -f $stagedResult.Length, $maximumLiteBytes)
}
$previousDotNetRoot = $env:DOTNET_ROOT
try {
    $env:DOTNET_ROOT = Split-Path -Parent $dotnetExecutable
    & $stagedExecutable -e2e-test | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Lite encrypted transport checks failed.' }
}
finally { $env:DOTNET_ROOT = $previousDotNetRoot }
Copy-Item -LiteralPath $stagedExecutable -Destination $outputExecutable -Force

$result = Get-Item -LiteralPath $outputExecutable
$hash = (Get-FileHash -LiteralPath $outputExecutable -Algorithm SHA256).Hash
[IO.File]::WriteAllText($outputExecutable + '.sha256', "$hash  $($result.Name)`n", [Text.Encoding]::ASCII)
$delta = $result.Length - $recordedBaselineBytes
Write-Host "Lite TCPTunnel build created: $($result.FullName)"
Write-Host ("Size: {0:N0} bytes ({1:N2} MiB)" -f $result.Length, ($result.Length / 1MB))
Write-Host ("Delta from recorded 442,368-byte baseline: {0:+#,#;-#,#;0} bytes" -f $delta)
