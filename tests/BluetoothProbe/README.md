# Windows Bluetooth capability diagnostics

Standalone diagnostic executable; it is not the TCPTunnel Bluetooth transport. It does not change radio state, device pairing, firewall rules, or system policy. The application retains its existing target and dependencies.

From the repository root (Windows 10 build 19041+ and .NET 10 SDK):

```powershell
dotnet run --project tests/BluetoothProbe -c Release
dotnet run --project tests/BluetoothProbe -c Release -- --advertise
dotnet run --project tests/BluetoothProbe -c Release -- --self-test
```

The default command queries the adapter. `--advertise` briefly attempts a test BLE advertisement and an RFCOMM `PlainSocket` listener, then stops both in `finally`. The RFCOMM call does not request radio discoverability. A 20-second cancellation deadline bounds asynchronous API calls; the BLE state wait is limited to 5 seconds. Exit codes: 0 requested local checks succeeded, 1 adapter/API setup failed, 2 either advertising check failed. Exit 0 never proves remote transfer.

The prototype uses experimental manufacturer data (`0xffff`) and RFCOMM UUID `f0829a7e-9731-4ebc-9479-3edb47aef8ab`. The capability smoke uses ASCII `TCPTunnelProbe`; the host/scan test uses an exact 19-byte record: `TCPB`, version 1, six little-endian bytes of the host's classic Bluetooth address and an eight-byte random nonzero instance ID. The advertised address is distinct from a potentially randomized LE address. Neither format is a finalized production discovery protocol or authenticated identity. Windows reserves service UUID advertising fields in this publisher; see [BluetoothLEAdvertisementPublisher](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothleadvertisementpublisher?view=winrt-26100).

## Two-computer procedure

Build once:

```powershell
dotnet publish tests/BluetoothProbe -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o tests/artifacts/BluetoothProbe
```

Copy `tests/artifacts/BluetoothProbe/BluetoothProbe.exe` to each Windows 10 19041+ / Windows 11 x64 computer with the .NET 10 runtime. Use devices that are not already paired with each other; do not unpair personal devices automatically.

On the host, in PowerShell:

```powershell
.\BluetoothProbe.exe --host 120
```

It prints its classic address and instance ID after both listeners start. On the second computer:

```powershell
.\BluetoothProbe.exe --scan 15
.\BluetoothProbe.exe --connect ADDRESS_FROM_THE_AGREED_HOST
```

Replace the final argument with the exact 12 hexadecimal digits from the scan result; compare address/instance with the host's READY line. Scan never connects automatically. Do not select unrelated test beacons. The host exits after one transfer or timeout (duration can be 1..300 seconds); the client has a 60-second overall deadline; Ctrl+C cancels.

The client uses uncached RFCOMM service discovery, explicitly requests PlainSocket, sends 16 random 16-KiB frames, checks every echo and the host's SHA256 digest. It rejects an already-paired device and queries fresh pairing state after exchange. `PASS UNPAIRED EXCHANGE` is evidence of that specific test only. Record whether any pairing prompt appears; cancel it and mark the no-pairing requirement failed. This test does not automatically call PairAsync or change discoverability. Scan RSSI describes the received beacon, not a proven bidirectional/root-hub measurement.

Exit codes for host/connect: 0 transfer completed, 2 validation/API/protocol failure, 3 timeout/cancel; argument usage errors may return 1 or 2. Scan exit 0 means the scan completed, even if its output explicitly says NO MATCHING BEACONS. This is a bounded synthetic transfer protocol, not the application wire format or full chat/media acceptance.

Sources: [Microsoft RFCOMM client sample](https://github.com/microsoft/Windows-universal-samples/blob/main/Samples/BluetoothRfcommChat/cs/Scenario1_ChatClient.xaml.cs), [StreamSocket](https://learn.microsoft.com/en-us/uwp/api/windows.networking.sockets.streamsocket).

## Several simultaneous clients

`--host SECONDS CLIENTS` (clients 1..7, default 1) accepts that many clients and starts serving only after all of them are connected, so the sessions provably overlap. Each client runs the usual `--connect` from a separate computer within its 60-second deadline. The host prints each client's remote address and `ALL N CLIENTS CONNECTED SIMULTANEOUSLY`; every client must still print its own `PASS UNPAIRED EXCHANGE`. Two clients on the same computer cannot share one RFCOMM service connection, so N clients need N separate computers.

## Next hardware checks

1. Use two Windows devices where the required BLE role is permitted. Do not delete existing device pairings automatically; use devices that are not already paired to each other.
2. Demonstrate beacon discovery with RSSI, connect to the advertised RFCOMM service, and exchange bounded bidirectional data without a pairing prompt. Record adapter, driver, OS, actual protection level, errors and timeouts on both sides.
3. Verify multiple clients, cancellation/disconnect, concurrent advertising and data, the full chat/media protocol and the packaged executable before selecting the production transport.
4. For Mesh, use at least three devices and prove a route whose final participant cannot reach the root directly. Simulation does not satisfy this radio test.

Capability flags alone must not enable the product's “working Bluetooth module” message without successfully starting the required APIs. Record hardware results separately from this reusable procedure.
