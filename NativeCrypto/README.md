# Windows MLS module

The module is integrated into the TCP/Bluetooth chat through Cryptography/SecureHubService.cs and SecureClientBridge.cs. CryptoPrototype is a separate test and benchmark harness that links the same managed core. See ../ENCRYPTION_DESIGN.md for the implemented boundaries and radio-test limitation.

The native module uses OpenMLS 0.9.1 with the standard
`MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519` suite. Credentials are bound to
domain-separated SHA-256 fingerprints of Ed25519 public keys. The module supports ephemeral identities and restored profile identities. `ProfileIdentityStore`
keeps private signing material in a separate current-user DPAPI file, bound to the
profile GUID, and applies a private ACL when creating the file. Public references
contain only the GUID and fingerprint. Application profiles migrate on first E2E use; tests use separate data directories.

## Build and verification

Requirements: Windows x64, Rust 1.99.0 MSVC, Visual C++ x64 tools, Windows SDK,
.NET 10 SDK and cargo-audit. The project-local tools under `obj/e2e-tools` are
supported; ordinary installations on PATH are supported too. Rust is only a build
dependency. The DLL statically links its C runtime.

From the repository root:

```powershell
.\build-crypto.ps1 -Configuration Release
```

This restores checksum-verified provider sources, checks the committed Cargo.lock,
runs native tests, builds the DLL, requires a clean RustSec audit including
warnings, and runs the C# integration tests. `-UpdateLock` is only for intentional
dependency updates; review Cargo.lock changes and rerun the audit afterwards.

Native output: `obj/NativeCrypto/x86_64-pc-windows-msvc/release/tcptunnel_crypto.dll`.
Managed output: `CryptoPrototype/bin/Release/net10.0-windows10.0.19041.0/`.

```powershell
dotnet run --project CryptoPrototype/TCPTunnel.CryptoPrototype.csproj -c Release -- --self-test
dotnet run --project CryptoPrototype/TCPTunnel.CryptoPrototype.csproj -c Release -- --benchmark
```

The integration tests include 64 members, replay/tamper rejection, exclusion,
late joins, pending-commit barriers, concurrent calls, handle limits and a receiver
in a separate process. Binary process pipes are test infrastructure, not a production
network protocol. Benchmarks measure encryption plus decryption by **one** recipient;
they exclude transport, Hub fan-out, TLS, GIF decoding and console rendering.

## Dependency overlays

`prepare-crypto-provider.ps1` downloads these exact upstream crates and verifies
their published SHA-256 checksums before extracting them into `obj/CryptoVendor`:

- `openmls_rust_crypto` 0.6.0: `36bf3fe824ead6e81d22f49a9cd7feffca3494979fac14c1248e19516e3625f1`.
- `core-models` 0.0.7: `ee3344d349528f449816cfa85e558c439bdca5a6d8a738cfb21e69f0b2b1952f`.

Only Cargo manifests are changed. The provider's HPKE dependencies move together
from 0.7 to 0.8.0, eliminating vulnerable optional libcrux-kem 0.0.9 entries.
The core-models Hax dependency moves from 0.3.7 to 0.4.2, removing the unmaintained
proc-macro-error2 dependency. No cryptographic Rust source is patched.
Hax/formal-verification builds are not supported or claimed by this prototype.
These overlays need review when upstream providers release the corresponding updates.

OpenMLS's invalid extension-context regression is checked explicitly. The committed
lockfile is also scanned against the complete advisory database without exclusions.
Third-party inventory and license texts are in `THIRD_PARTY_NOTICES.md` and
`third-party-licenses/`; retain them when redistributing the native component.

## ABI and state boundaries

ABI version 5 uses opaque monotonically assigned integer handles; stale handles
cannot address another client's state. A registry allows at most 128 states.
Each state has its own signer, provider and MLS group. Operations are synchronized
in Rust and across execution/output-copy in C#. SafeHandle releases native state.

Operations: public key, one-use key package, group creation, member addition,
Welcome processing, pending-commit merge, encrypt, process, member removal and inspection of authenticated group/owner/member identities.
The owner coordinates membership changes; duplicate fingerprints are rejected.
Sender fingerprints come from authenticated MLS credentials, not nicknames.
No past epoch keys are retained in the MLS configuration. The within-epoch
out-of-order window retains at most eight generations; forward skipping is bounded at 128.

Wire input is bounded at 8 MiB, plaintext at 8 MiB minus 256 KiB to leave room for
encryption overhead, handshakes at 256 KiB and membership at 64.
Serialization consumes the entire input. Membership changes must be merged before
application send/receive continues. Recoverable native panics are converted into
errors; panic hooks do not print diagnostic content. Invalid foreign pointers,
out-of-memory aborts and stack overflow are not recoverable through catch_unwind.
The C# caller must provide valid pinned buffers; network packets never supply pointers.

After each operation the result is copied once and the native output is wiped.
Operations are never repeated for output sizing, avoiding repeated ratchet advancement.
Native internal/poisoned-state failures close the managed handle. Passing tests and advisory scans is not a complete security assessment or an independent audit.

ABI 5 distinguishes key-package validation failures from failures after OpenMLS
membership mutation begins. Invalid packages release the nickname reservation
and reject only that connection. Library-stage failures remain fatal to the room;
continuing after an uncertain partial mutation could desynchronize its epochs.

## Identity and admission

Restored signing keys are checked by signing and verifying a domain-separated
validation message. Invalid private/public pairs and unsupported schemes are rejected.
The private-key serialization is versioned independently from the native ABI.
Missing/corrupt keys fail closed; they do not trigger automatic identity replacement.

`RoomIdentityGate` retains nickname-to-fingerprint reservations while users are
offline. `RoomTrustStore` saves approvals and revocations in an owner/room-bound
current-user DPAPI file. Atomic saves must succeed before an approval takes effect.
This protects the local file against modification without valid DPAPI protection;
it does not claim rollback resistance against a compromised local Windows account.

Admission uses one-use 30-second challenges tied to the room, verified owner,
connection binding, nickname and exact validated MLS key package. The client
checks that context against trusted connection information before signing. The
connection binding is a fresh 32-byte client nonce sent inside the owner-pinned TLS session; the challenge adds a fresh server nonce. The client checks the exact expected context before signing.
Proof signatures use a separate domain from MLS messages. An accepted admission
returns the stored key package, not a replacement supplied in the proof response.
The coordinator must use that exact package when creating the membership commit.

Tests cover the same-nickname impostor, unapproved free nicknames, key-package
substitution, another connection, expired/replayed proofs, revoked users, failed
trust-file persistence, owner restart and key restoration in a separate process.
The test stores use isolated temporary directories; history storage is not implemented.

## Verified checkpoint: 2026-10-10

- Native module: 9 checks passed; C# integration: 16 checks passed.
- The self-contained single-file prototype also passes all 16 integration checks.
- Existing chat: 86 checks and `-stress-test` pass with the prototype excluded from its compilation.
- The strict dependency audit reports no known vulnerabilities or maintenance warnings.
- Native DLL: 2.36 MiB; standalone prototype EXE: 40.47 MiB. These are prototype
  measurements, not the final encrypted chat's size.
- The DLL imports Windows system libraries only; neither Rust Runtime nor a separate
  Visual C++ Runtime is required by the native module.

Local measurements on this machine, encryption plus one recipient's decryption:

| Group size | Plaintext | Ciphertext | Median | Sample p95 |
|---:|---:|---:|---:|---:|
| 2 | 128 B | 290 B | 0.145 ms | 0.151 ms |
| 16 | 128 B | 290 B | 0.149 ms | 0.151 ms |
| 64 | 128 B | 290 B | 0.156 ms | 0.161 ms |
| 64 | 5,760 B | 5,922 B | 0.248 ms | 0.263 ms |
| 64 | 2,880,000 B | 2,880,166 B | 53.981 ms | 54.694 ms |

The small message uses 300 samples, raster 40 and large payload 20, after warm-up.
These samples exclude transport, delivery to all recipients, media decoding and UI.
The earlier provider and the updated provider were also checked with a peer in a
separate process using the original HPKE 0.7 build. This is compatibility evidence,
not a claim of a complete interoperability or security audit.
