param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectRoot "TCPTunnel.csproj"
$payloadDirectory = Join-Path $projectRoot "obj\LitePayload"
$bootstrapperDirectory = Join-Path $projectRoot "obj\LiteBootstrapper"
$publishDirectory = Join-Path $projectRoot "bin\$Configuration\net8.0-windows\win-x64\publish"
$bootstrapperSource = Join-Path $projectRoot "Bootstrapper\TCPTunnel.Bootstrapper.c"
$maximumLiteBytes = 20 * 1024 * 1024
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

& dotnet publish $projectFile `
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
$payloadId = ([Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($payloadSignature)))).Substring(0, 16)
$payloadIdFile = Join-Path $bootstrapperDirectory "payload.id"
[System.IO.File]::WriteAllText($payloadIdFile, $payloadId, [System.Text.Encoding]::ASCII)

function ConvertTo-RcPath([string]$Path) {
    return ([System.IO.Path]::GetFullPath($Path)).Replace("\", "\\")
}

$resourceFile = Join-Path $bootstrapperDirectory "TCPTunnel.Resources.rc"
$resourceText = @(
    '101 RCDATA "' + (ConvertTo-RcPath $payloadFiles['101']) + '"',
    '102 RCDATA "' + (ConvertTo-RcPath $payloadFiles['102']) + '"',
    '103 RCDATA "' + (ConvertTo-RcPath $payloadFiles['103']) + '"',
    '104 RCDATA "' + (ConvertTo-RcPath $payloadFiles['104']) + '"',
    '105 RCDATA "' + (ConvertTo-RcPath $payloadIdFile) + '"'
) -join [Environment]::NewLine
[System.IO.File]::WriteAllText($resourceFile, $resourceText, [System.Text.Encoding]::Unicode)

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
    throw "Visual Studio Installer (vswhere.exe) was not found. Install Desktop development with C++."
}
$visualStudioPath = (& $vswhere -latest -products * -property installationPath).Trim()
if ([String]::IsNullOrWhiteSpace($visualStudioPath)) {
    throw "Visual Studio was not found. Install Desktop development with C++."
}
$vcvars = Join-Path $visualStudioPath "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path -LiteralPath $vcvars -PathType Leaf)) {
    throw "The Visual C++ x64 toolchain was not found. Install Desktop development with C++."
}

$resourceObject = Join-Path $bootstrapperDirectory "TCPTunnel.Resources.res"
$objectFile = Join-Path $bootstrapperDirectory "TCPTunnel.Bootstrapper.obj"
$outputExecutable = Join-Path $publishDirectory "TCPTunnel.exe"
$stagedExecutable = Join-Path $bootstrapperDirectory "TCPTunnel.exe"
$nativeBuildScript = Join-Path $bootstrapperDirectory "BuildBootstrapper.cmd"
$nativeCommands = @"
@echo off
call "$vcvars" >nul
if errorlevel 1 exit /b %errorlevel%
rc.exe /nologo /fo"$resourceObject" "$resourceFile"
if errorlevel 1 exit /b %errorlevel%
cl.exe /nologo /O1 /MT /W4 /WX /utf-8 /DUNICODE /D_UNICODE /Fo"$objectFile" /Fe"$stagedExecutable" "$bootstrapperSource" "$resourceObject" /link /SUBSYSTEM:CONSOLE /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA shell32.lib advapi32.lib user32.lib
exit /b %errorlevel%
"@
[System.IO.File]::WriteAllText($nativeBuildScript, $nativeCommands, [System.Text.Encoding]::ASCII)

& $nativeBuildScript
if ($LASTEXITCODE -ne 0) {
    throw "Native bootstrapper build failed with exit code $LASTEXITCODE."
}

$stagedResult = Get-Item -LiteralPath $stagedExecutable
if ($stagedResult.Length -gt $maximumLiteBytes) {
    throw ("Lite executable exceeds the hard 20 MiB limit: {0:N0} > {1:N0} bytes." -f $stagedResult.Length, $maximumLiteBytes)
}
Copy-Item -LiteralPath $stagedExecutable -Destination $outputExecutable -Force

$result = Get-Item -LiteralPath $outputExecutable
$delta = $result.Length - $recordedBaselineBytes
Write-Host "Lite TCPTunnel build created: $($result.FullName)"
Write-Host ("Size: {0:N0} bytes ({1:N2} MiB)" -f $result.Length, ($result.Length / 1MB))
Write-Host ("Delta from recorded 442,368-byte baseline: {0:+#,#;-#,#;0} bytes" -f $delta)
