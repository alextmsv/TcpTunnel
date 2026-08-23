# TcpTunnel repository instructions

## Mission

Add lightweight image sharing to this .NET 8 Windows console chat without regressing the existing TCP chat, command system, console redraw behavior, or lite distribution size.

This repository is intentionally small and latency-sensitive. Prefer small, explicit code over general frameworks. Correctness and compatibility come before clever micro-optimizations; once behavior is correct, optimize measured hot paths and allocations.

## Project facts that must remain true

- Target framework: `net8.0-windows`.
- Distribution remains framework-dependent (`SelfContained=false`).
- `build-lite.ps1` embeds the managed payload into the native bootstrapper.
- Existing transport is ordered, length-prefixed, strict UTF-8 with `MessageProtocol.MaxFrameBytes == 16 * 1024`.
- Ordinary chat text remains capped by `MessageProtocol.MaxMessageCharacters == 2000`.
- Slash commands are local command parsing and must not be confused with image input.
- `UserInterface` has an interactive `Console.ReadKey` editor and preserves typed input while incoming messages redraw the chat.
- Console layout can change while connected; current resize logic and `consoleLock` are part of the stability contract.

Read the current implementations before editing: `TCPTunnel.csproj`, `build-lite.ps1`, `MessageProtocol.cs`, `NetWorker.cs`, `Broadcaster.cs`, `Client.cs`, `Commands.cs`, `UserInterface.cs`, `ConsoleGraphic.cs`, `LegacyEventProtocol.cs`, `SystemMessageProtocol.cs`, `SnakeProtocol.cs`, and `StabilityTests.cs`.

## Hard acceptance criteria

1. **Frozen receive-time sizing**
   - An image is never shown as a full-screen takeover.
   - Each receiving client sizes the image exactly once using that client's current chat viewport when the packet arrives.
   - Preserve aspect ratio and compensate for tall console character cells.
   - Cap image height to roughly 50-60% of currently usable chat rows; tune the exact constant by testing.
   - Store the resulting fixed width/height with the history entry.
   - Future window enlargement must not upscale or recompute an old image.
   - If the future viewport becomes narrower, clip the frozen image for display; do not mutate its stored dimensions. When width returns, the original frozen image is visible again.

2. **Drag-and-drop input**
   - Primary UX: drag one image from Explorer into the chat input.
   - Required formats: `.jpg`, `.jpeg`, `.png`.
   - `.webp` is supported only when the machine's WIC installation can decode it. Do not bundle a WebP decoder solely for this feature.
   - Windows Terminal/conhost expose a dropped file to a console program as inserted path text, not a portable app-level drop event. Integrate with the existing input editor rather than adding a GUI/OLE drop surface.
   - Exactly one normalized existing image path in the input may become an image action. Quoted paths and spaces must work.
   - A slash command containing a path remains a slash command. A path embedded in normal chat remains text.
   - Keep Enter-triggered path recognition as the deterministic fallback. Automatic recognition after a drop/paste burst is allowed only if it is tested and does not create false sends.

3. **No chat regressions**
   - Normal messages retain ordering and current rate limits.
   - `/help`, `/status`, `/ping`, `/clear`, `/stop`, `/kick`, `/exit` and future slash commands keep their current semantics.
   - Mentions, taskbar attention, snake protocol, system messages, authentication, disconnect handling, and local input preservation keep working.
   - Image processing must not hold `consoleLock` during file I/O, WIC decode, quantization, Base64 conversion, or resampling.
   - Incoming messages while an image is prepared must remain responsive.

4. **Maximum optimization / minimum distribution cost**
   - Final lite `TCPTunnel.exe` hard limit: **20 MiB (20 * 1024 * 1024 bytes)**.
   - Add a hard size gate to `build-lite.ps1`; fail the build if the final executable exceeds the limit.
   - Also print the byte/MiB size and, if easy, delta from a recorded baseline.
   - Prefer **zero additional runtime DLLs**.
   - Default image backend is direct Windows Imaging Component (WIC) interop. JPEG and PNG are native WIC codecs. WebP is opportunistic through an installed WIC codec.
   - Do not add ImageSharp, SkiaSharp, WPF, WinForms, Avalonia, OpenCV, or another image stack unless the WIC implementation is proven infeasible and the size impact is measured first.

## Preferred design

### 1. Keep original image files local

Never transmit original JPEG/PNG/WebP bytes. Do not transmit filenames, source paths, EXIF, metadata, or thumbnails from the source container.

Sender pipeline:

`file -> WIC first frame -> tiny grayscale raster -> 4-bit quantization -> packed payload -> image control frame`

A practical canonical wire cap is approximately 160 columns by 60-80 raster rows. Constants must be selected so the maximum encoded image frame, including Base64 and hub-added sender information, remains comfortably under the existing 16 KiB transport ceiling.

Use two pixels per payload byte:

- high nibble: first luminance level 0..15;
- low nibble: second luminance level 0..15.

The recipient does not need JPEG/PNG decoding. It only receives the small packed raster.

### 2. Use WIC directly

Create a narrow WIC wrapper such as `ImageCodec.cs`.

Requirements:

- decode only frame 0;
- query source dimensions before allocating output;
- reject pathological dimensions/pixel counts;
- compute a canonical thumbnail preserving aspect ratio;
- use WIC scaling and convert directly to `GUID_WICPixelFormat8bppGray`;
- copy only the small target grayscale buffer into managed memory;
- deterministic COM release/disposal;
- no `System.Drawing.Common` dependency unless a benchmark/size comparison proves it is superior;
- `.webp` must be capability-detected: if no WIC codec is installed, return a localized local error and keep the chat session alive.

Do not use per-pixel COM calls. Work with contiguous buffers.

### 3. Add a versioned image control protocol

Add `ImageProtocol.cs`. Follow the existing control-message convention (record-separator/control prefix) and keep ordinary text frames untouched.

Preferred first version remains strict-UTF8-compatible to minimize risk to `MessageProtocol`, e.g. a small ASCII header plus Base64 packed payload. Base64 overhead is acceptable for a ~3-8 KiB bounded image if it avoids destabilizing the core framing code.

The image packet must contain only bounded protocol data such as:

- marker and protocol version;
- raster width and height;
- packed 4-bpp payload;
- sender nickname only in the server/broadcast form.

The hub must:

- detect image control frames **before** applying the ordinary 2,000-character chat limit;
- validate the protocol version, dimensions, exact packed payload length, Base64 syntax, and total frame size;
- use a separate stricter image rate limiter;
- add/derive the authenticated sender identity itself;
- broadcast through the existing ordered broadcaster;
- never decode source image formats.

Malformed image control input must be rejected predictably without desynchronizing the stream.

### 4. Freeze image size at receive time

Add a dedicated image history representation. Do not convert images into ordinary wrapping chat strings.

A `ChatHistoryEntry` should become a small discriminated model (or equivalent) with text and image forms. For an image form keep at least:

- frozen display width;
- frozen display height;
- packed 4-bpp raster at those frozen dimensions;
- optional lightweight caption/sender state if needed by rendering.

Receive flow:

1. Parse/validate image packet.
2. Under `consoleLock`, snapshot the current usable content width and chat rows; do not perform pixel work yet.
3. Release the lock.
4. Downscale the canonical wire raster to a size bounded by the snapshot. Never upscale above the wire raster.
5. Repack/freeze that local raster.
6. Re-enter the normal history/redraw path under `consoleLock`.

Redraw flow:

- text entries keep current dynamic wrapping;
- image entries keep fixed dimensions and never resample due to resize;
- map luminance nibbles using a static ASCII lookup table;
- clip each frozen image row if current width is too small;
- history visibility calculations must count image rows correctly;
- `/clear` clears both text and image entries.

Do not permanently allocate one managed `string` per image row unless profiling shows it is cheaper overall. Prefer packed bytes in history and bounded temporary row buffers while rendering.

### 5. Input integration must be surgical

Do not replace `ReadChatMessage`, command parsing, or editing controls wholesale.

Create a helper such as `ImageInput.cs` with pure/testable functions:

- strip one matching pair of surrounding quotes;
- reject empty input;
- reject input starting with `/`;
- require exactly one path, not arbitrary text plus a path;
- require `File.Exists` before image classification;
- extension allowlist is case-insensitive;
- separate `Supported` from `CodecUnavailable` so WebP can fail cleanly.

Because drop is delivered as inserted characters, an automatic-send heuristic may watch a completed path after a short idle period. It must never perform file decode while inside the input lock. If there is any ambiguity, leave the path visible and send only on Enter.

## Performance rules

Use the official .NET performance skill if available (`dotnet/skills`, `analyzing-dotnet-performance`) during final optimization.

For image-related code:

- no LINQ in hot paths;
- no `GetPixel`-style APIs;
- no per-pixel delegates;
- no `Substring`/`Split` loops when span parsing is straightforward;
- no repeated Base64 conversions;
- use static readonly LUTs for 4-bit -> ASCII mapping;
- use integer arithmetic for resize coordinate mapping when practical;
- use `Span<T>`/`ReadOnlySpan<T>` for parsing and bounded buffers;
- use `stackalloc` only for small proven bounds; otherwise `ArrayPool<byte>` with `try/finally` return;
- avoid large-object-heap allocations entirely for normal image packets;
- do not block async network receive/broadcast with file decoding;
- do not parallelize tiny raster loops; task/thread overhead will cost more than the work;
- minimize time inside `consoleLock` and client send locks;
- preserve `ConfigureAwait(false)` conventions in networking code.

Do not introduce `unsafe` merely for style. Use it only if measurements show a meaningful gain or WIC `CopyPixels` interop is materially cleaner with a pinned/span-backed buffer.

## Resource and security limits

Define explicit constants and validate before allocation/work. Suggested categories:

- max source file bytes;
- max source width/height and/or max source pixels;
- max wire width/height;
- max packed payload bytes;
- max image frame bytes below `MessageProtocol.MaxFrameBytes`;
- max frozen local image width/height;
- image rate limit per client;
- max image-history memory or equivalent total history budget.

Do not trust client-provided dimensions, Base64 length, nickname, or payload shape. For `width * height`, use checked arithmetic before computing packed length.

## Files likely to change

Prefer adding focused files rather than making `UserInterface.cs` larger:

- new: `ImageCodec.cs`
- new: `ImageProtocol.cs`
- new: `ImageRenderer.cs`
- new: `ImageInput.cs`
- modify: `UserInterface.cs`
- modify: `NetWorker.cs`
- modify: `Client.cs` if a separate image limiter belongs there
- modify: `StabilityTests.cs`
- modify: `Localization.cs` for user-facing errors/help
- modify: `README.md` for drag/drop behavior and supported formats
- modify: `build-lite.ps1` for the 20 MiB hard gate

Avoid changing `MessageProtocol.cs` beyond reusable bounded helpers unless the existing framing genuinely prevents the implementation.

## Testing requirements

Do not stop at compilation. Run the existing repository tests and add focused regression coverage.

### Protocol tests

- smallest and largest legal image frame round-trip;
- odd pixel count packing/unpacking;
- invalid version;
- zero/negative/overflowing dimensions rejected;
- dimension/product overflow rejected;
- truncated/extra packed data rejected;
- malformed Base64 rejected;
- maximum packet remains below 16 KiB;
- image control parsing happens before ordinary 2,000-char text rejection.

### Input tests

- `C:\x\a.jpg` recognized if it exists;
- quoted path with spaces recognized;
- `.JPG`, `.JPEG`, `.PNG` recognized case-insensitively;
- WebP capability branch tested;
- nonexistent `.png` is normal text, not a file operation;
- `/ping C:\x\a.jpg` remains a command;
- `look C:\x\a.jpg` remains normal chat;
- multiple paths are not auto-sent as one image.

### Layout tests

Model geometry without requiring an interactive terminal where possible.

- same image received at 60-column viewport freezes smaller than at 140 columns;
- neither result exceeds the configured fraction of available rows;
- later enlargement leaves frozen dimensions unchanged;
- later narrowing does not change frozen dimensions and rendering clips safely;
- text wrapping before/after image entries remains correct;
- visible-history start logic handles mixed text and image entries;
- input rendering remains preserved when an image arrives during typing.

### Integration/stress tests

- interleave text and images from multiple clients and assert consistent order;
- image rate limiting does not change text rate-limit semantics;
- malformed image from one client does not corrupt broadcasts for others;
- sender local echo uses the sender's current viewport, while each receiver freezes to its own viewport;
- `/clear`, disconnect/reconnect, and shutdown work with image history present.

### Build-size test

Run the lite build. It must produce a single executable <= 20 MiB. The script itself must enforce this limit. If adding any package changes the embedded payload list, explicitly account for every new file and report the size delta.

## Definition of done

The task is complete only when all acceptance criteria pass, existing self/stress tests still pass, new tests cover protocol/input/layout regressions, JPG/PNG work by drag/drop, optional WebP degrades cleanly, received images remain frozen after resize, the chat remains responsive under interleaved image/text traffic, and the lite executable is below the enforced 20 MiB ceiling.

When reporting completion, include:

- files changed;
- protocol constants and maximum frame calculation;
- before/after lite EXE size;
- representative JPEG/PNG preparation time and allocation observations if measured;
- tests run and results;
- whether WebP was available on the test machine.
