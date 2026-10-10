param([string]$ProjectRoot = $PSScriptRoot)

$ErrorActionPreference = 'Stop'
$vendorRoot = Join-Path $ProjectRoot 'obj\CryptoVendor'
$archive = Join-Path $vendorRoot 'openmls_rust_crypto-0.6.0.crate'
$expected = '36bf3fe824ead6e81d22f49a9cd7feffca3494979fac14c1248e19516e3625f1'
New-Item -ItemType Directory -Path $vendorRoot -Force | Out-Null
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
    Invoke-WebRequest 'https://crates.io/api/v1/crates/openmls_rust_crypto/0.6.0/download' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) {
    throw 'OpenMLS provider archive does not match its pinned crates.io checksum.'
}
& tar -xf $archive -C $vendorRoot
if ($LASTEXITCODE -ne 0) { throw 'Could not extract the pinned OpenMLS provider.' }
$manifest = Join-Path $vendorRoot 'openmls_rust_crypto-0.6.0\Cargo.toml'
$original = [IO.File]::ReadAllText($manifest)
$pattern = '(?m)(\[dependencies\.(?:hpke|hpke-rs-crypto|hpke-rs-rust-crypto)\]\r?\nversion = )"0\.7"'
if ([regex]::Matches($original, $pattern).Count -ne 3) { throw 'Pinned provider manifest changed; review the HPKE update before building.' }
$updated = [regex]::Replace($original, $pattern, '${1}"=0.8.0"')
[IO.File]::WriteAllText($manifest, $updated, [Text.UTF8Encoding]::new($false))

$modelsArchive = Join-Path $vendorRoot 'core-models-0.0.7.crate'
$modelsExpected = 'ee3344d349528f449816cfa85e558c439bdca5a6d8a738cfb21e69f0b2b1952f'
if (-not (Test-Path -LiteralPath $modelsArchive -PathType Leaf)) {
    Invoke-WebRequest 'https://crates.io/api/v1/crates/core-models/0.0.7/download' -OutFile $modelsArchive
}
if ((Get-FileHash -LiteralPath $modelsArchive -Algorithm SHA256).Hash -ne $modelsExpected) {
    throw 'Core-models archive does not match its pinned crates.io checksum.'
}
& tar -xf $modelsArchive -C $vendorRoot
if ($LASTEXITCODE -ne 0) { throw 'Could not extract core-models.' }
$modelsManifest = Join-Path $vendorRoot 'core-models-0.0.7\Cargo.toml'
$modelsOriginal = [IO.File]::ReadAllText($modelsManifest)
$modelsPattern = '(?m)(\[dependencies\.hax-lib\]\r?\nversion = )"0\.3\.7"'
if ([regex]::Matches($modelsOriginal, $modelsPattern).Count -ne 1) { throw 'Pinned core-models manifest changed.' }
[IO.File]::WriteAllText($modelsManifest, [regex]::Replace($modelsOriginal, $modelsPattern, '${1}"=0.4.2"'), [Text.UTF8Encoding]::new($false))
