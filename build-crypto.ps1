param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$UpdateLock
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $projectRoot 'prepare-crypto-provider.ps1') -ProjectRoot $projectRoot
. (Join-Path $projectRoot 'build-tools.ps1')
$vcvars = Resolve-VisualCppToolchain $projectRoot $null
$sdk = Resolve-WindowsBuildSdk $projectRoot $null
$localCargo = Join-Path $projectRoot 'obj\e2e-tools\cargo\bin\cargo.exe'
$cargoCommand = Get-Command cargo -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
$cargo = if ($cargoCommand) { $cargoCommand.Source } elseif (Test-Path -LiteralPath $localCargo -PathType Leaf) { $localCargo } else {
    throw 'Rust with the x86_64-pc-windows-msvc target is required to build the crypto prototype.'
}
$environmentBefore = @{}
foreach ($name in @('CARGO_HOME', 'RUSTUP_HOME', 'CARGO_TARGET_DIR', 'RUSTFLAGS', 'CARGO_ENCODED_RUSTFLAGS', 'DOTNET_ROOT', 'PATH')) { $environmentBefore[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:RUSTFLAGS = $null
    $env:CARGO_ENCODED_RUSTFLAGS = $null
    if ($cargo -eq $localCargo) {
        $env:CARGO_HOME = Join-Path $projectRoot 'obj\e2e-tools\cargo'
        $env:RUSTUP_HOME = Join-Path $projectRoot 'obj\e2e-tools\rustup'
    }
    $env:CARGO_TARGET_DIR = Join-Path $projectRoot 'obj\NativeCrypto'
    $env:PATH = (Split-Path -Parent $cargo) + [IO.Path]::PathSeparator + $env:PATH
    $nativeRoot = Join-Path $projectRoot 'NativeCrypto'
    New-Item -ItemType Directory -Path $env:CARGO_TARGET_DIR -Force | Out-Null
    $buildScript = Join-Path $env:CARGO_TARGET_DIR 'BuildCrypto.cmd'
    $lockFlag = if ($UpdateLock) { '' } else { '--locked' }
    $releaseFlag = if ($Configuration -eq 'Release') { '--release' } else { '' }
    $commands = @"
@echo off
call "$vcvars" >nul
if errorlevel 1 exit /b %errorlevel%
set "LIB=$($sdk.UmLib);$($sdk.UcrtLib);%LIB%"
set "PATH=$($sdk.Bin);%PATH%"
cd /d "$nativeRoot"
"$cargo" test --target x86_64-pc-windows-msvc $releaseFlag $lockFlag
if errorlevel 1 exit /b %errorlevel%
"$cargo" build --target x86_64-pc-windows-msvc $releaseFlag $lockFlag
exit /b %errorlevel%
"@
    [IO.File]::WriteAllText($buildScript, $commands, [Text.Encoding]::Default)
    & $buildScript
    if ($LASTEXITCODE -ne 0) { throw "Crypto prototype build failed: $LASTEXITCODE" }
    $auditCommand = Get-Command cargo-audit -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    $audit = if ($auditCommand) { $auditCommand.Source } else {
        Join-Path $projectRoot 'obj\e2e-tools\audit\cargo-audit-x86_64-pc-windows-msvc-v0.22.2\cargo-audit.exe'
    }
    if (-not (Test-Path -LiteralPath $audit -PathType Leaf)) { throw 'cargo-audit is required. Install it with cargo install cargo-audit --locked.' }
    & $audit audit --file (Join-Path $nativeRoot 'Cargo.lock') --db (Join-Path $projectRoot 'obj\e2e-tools\advisory-db') --deny warnings
    if ($LASTEXITCODE -ne 0) { throw 'Crypto dependency audit failed; update dependencies before shipping.' }
    $profile = if ($Configuration -eq 'Release') { 'release' } else { 'debug' }
    $library = Get-Item (Join-Path $env:CARGO_TARGET_DIR "x86_64-pc-windows-msvc\$profile\tcptunnel_crypto.dll")
    Write-Host "Crypto module: $($library.FullName)"
    Write-Host ("Native DLL size: {0:N0} bytes ({1:N2} MiB)" -f $library.Length, ($library.Length / 1MB))
    $dotnet = Resolve-DotNetSdk $projectRoot $null
    $env:DOTNET_ROOT = Split-Path -Parent $dotnet
    & $dotnet run --project (Join-Path $projectRoot 'CryptoPrototype\TCPTunnel.CryptoPrototype.csproj') -c $Configuration --no-launch-profile -- --self-test
    if ($LASTEXITCODE -ne 0) { throw 'Managed/native crypto integration checks failed.' }
}
finally {
    foreach ($name in $environmentBefore.Keys) { [Environment]::SetEnvironmentVariable($name, $environmentBefore[$name], 'Process') }
}
