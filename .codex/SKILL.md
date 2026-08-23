---
name: tcptunnel-image-chat
description: Add or optimize lightweight image transport and fixed-size ASCII image rendering in the .NET 8 Windows TcpTunnel console chat. Use when implementing drag-and-drop JPG/PNG/WebP images, image protocol frames, viewport-aware rendering, WIC decoding, chat-history integration, or performance/size work for this feature. Do not redesign unrelated chat behavior or add heavyweight UI/image frameworks.
---

# TcpTunnel Image Chat

## Purpose

Implement image sharing in TcpTunnel while preserving the existing text chat, slash-command behavior, asynchronous message ordering, console redraw logic, and tiny framework-dependent distribution. The feature must be bounded, versioned, hostile-input-safe, and cheap in CPU, memory, network traffic, and executable size.

## Hard constraints

- Target: `net8.0-windows` and the existing framework-dependent lite bootstrapper.
- Final lite `TCPTunnel.exe` must be <= 20 MiB. Treat this as a hard build failure, not a documentation goal.
- Prefer zero new runtime DLLs. Use Windows Imaging Component (WIC) directly for image decode/scale/format conversion.
- Required source formats: JPEG/JPG and PNG.
- WebP is optional only when WIC can decode it through an installed system codec. Do not bundle libwebp just to satisfy WebP.
- Do not add ImageSharp, SkiaSharp, WPF, WinForms, Avalonia, or another GUI/image framework unless measurements prove WIC is impossible.
- Keep the existing text framing and slash-command semantics unless a benchmark and compatibility analysis justify changing them.
- No LINQ, `GetPixel`, per-pixel heap allocation, reflection, or unbounded buffers in image hot paths.

## Required behavior

1. A user drags one supported image file from Explorer into an otherwise normal chat input. Windows Terminal/conhost inserts the local path; TcpTunnel recognizes it as an image submission instead of a chat message. Preserve Enter as a reliable fallback if automatic drop recognition is ambiguous.
2. The sender decodes once, downsamples to a small canonical wire raster, converts to grayscale, quantizes to 4 bits per pixel, and sends that raster through a versioned image control frame.
3. Each receiver independently snapshots its current chat viewport when the image arrives, downsamples the wire raster once to fit that viewport, and stores the resulting fixed-size image entry.
4. Later console resizes must never upscale or resample an already-received image. A larger future window leaves the image at the frozen receive-time dimensions. If a future window is narrower than the frozen image, clip for display rather than mutating the stored raster.
5. Images are embedded chat entries, not full-screen views. Limit image height to a fraction of the currently usable chat area and preserve aspect ratio with console-cell aspect compensation.
6. Text messages, mentions, incoming-message redraw, `/` commands, `/clear`, `/exit`, `/kick`, snake control messages, and connection/disconnection behavior must continue to work as before.

## Architecture

Use four small responsibilities instead of placing image logic in `UserInterface.cs`:

- `ImageCodec.cs`: WIC interop; decode first frame only; inspect dimensions; resize; convert directly to 8-bit grayscale; optionally detect WebP codec availability.
- `ImageProtocol.cs`: versioned control-frame creation, strict parsing, dimensions/payload validation, 4-bpp pack/unpack, and server/client envelope handling.
- `ImageRenderer.cs`: receive-time viewport fitting, 4-bpp resampling, ASCII LUT rendering, and fixed image row handling.
- `ImageInput.cs`: normalize quoted drag/drop paths and identify exactly one supported existing file without interfering with ordinary input or commands.

Keep `MessageProtocol` as the bounded transport. Image control frames must remain safely below `MaxFrameBytes` after Base64/header overhead. Parse image control frames before applying `MaxMessageCharacters` to ordinary chat text.

## Wire format

Prefer an ASCII control frame compatible with the current strict UTF-8 transport, for example:

`RS + TCPTUNNEL|IMAGE|1|<width>|<height>|<base64-4bpp>`

The exact names may change to match existing protocol conventions, but the following are mandatory:

- explicit protocol/version marker;
- bounded width and height;
- payload length derivable from dimensions: `ceil(width * height / 2)` bytes;
- strict Base64 decoding;
- no metadata, filename, source path, EXIF, or original file bytes on the wire;
- sender identity added/validated by the hub, never trusted from the client packet.

Recommended canonical maxima are around 160 columns and 60-80 raster rows. Choose constants so the largest server-broadcast frame remains comfortably below 16 KiB.

## Image pipeline

1. Validate extension and source file size before opening.
2. Let WIC identify/decode the format; do not trust the extension alone.
3. Decode frame 0 only.
4. Query source dimensions and reject unreasonable dimensions/pixel counts before expensive work.
5. Compute canonical wire dimensions while preserving aspect ratio.
6. Use WIC scaler + format converter to obtain only the small grayscale output buffer. Prefer `GUID_WICPixelFormat8bppGray`.
7. Quantize `gray >> 4` and pack two 4-bit pixels per byte.
8. Release COM/native resources deterministically.
9. On receive, compute frozen dimensions from a snapshot of the current chat content width and usable rows. Do not hold `consoleLock` while resampling.
10. Store the frozen packed raster plus fixed width/height in history. Map nibbles through a static ASCII LUT only while drawing.

## Viewport rules

Snapshot geometry under `consoleLock`, then release the lock before CPU work. The target should be bounded by:

- current `GetContentWidth()`;
- current usable chat rows after borders/input/server card;
- a maximum image-height fraction (roughly one half of usable chat rows, tune after visual testing);
- canonical wire dimensions;
- minimum sane dimensions so tiny terminals fail gracefully.

Never use future window size to improve an old image. The receive-time frozen width/height are part of the `ChatHistoryEntry` state.

## Input integration

Do not replace the existing `Console.ReadKey` editor. Drag/drop becomes path text in the input stream, so integrate with it.

- Only classify an input as an image when the entire normalized input is exactly one existing supported file path.
- Quoted paths and spaces must work.
- A path embedded in ordinary text or after a slash command is text, not an image.
- Multiple dropped paths must not be silently merged or partially sent.
- Prefer a short input-idle/burst heuristic for automatic send only if it is stable in both conhost and Windows Terminal. Enter-triggered recognition is the mandatory fallback.
- Never block the input loop while decoding. Move decode/prepare work outside `consoleLock` and provide a clean local error on failure.

## Chat-history integration

Do not flatten an image into ordinary wrapping text lines. Extend the history model so a logical entry can be text or a fixed image.

- Text entries keep current wrapping/mention behavior.
- Image entries have fixed `Width`, `Height`, and packed pixels.
- History row counting must account for image rows without resampling on redraw.
- Rendering may clip a frozen row if the current window is narrower.
- `/clear` removes image entries exactly like text entries.
- Preserve history bounds; do not let 200 large image entries create unbounded memory usage. Add a separate image-memory or total-history-row bound if needed.

## Server behavior and abuse limits

The hub must validate image frames before broadcast:

- version supported;
- dimensions within constants;
- exact payload size;
- valid Base64;
- image frame under transport limit;
- separate image rate limit, stricter than normal text messages.

Do not decode JPEG/PNG on the hub. The hub only validates the tiny grayscale raster envelope and broadcasts it in ordering-compatible fashion.

## Performance workflow

Before and after implementation:

1. Record lite EXE size.
2. Record allocations and elapsed time for preparing a representative 12 MP JPEG and PNG.
3. Test receive/render with narrow and wide terminals.
4. Inspect hot paths for transient strings/arrays, repeated conversions, and lock duration.
5. Only optimize measured hot paths. Prefer `Span<T>`, `stackalloc` for small bounded buffers, and `ArrayPool<T>` for larger temporary buffers.
6. Avoid unsafe code unless it measurably simplifies WIC buffer transfer or eliminates meaningful overhead.

## Validation

Run the existing self/stress tests plus new image-specific coverage. At minimum verify:

- JPEG and PNG drag/drop send successfully with paths containing spaces.
- WebP succeeds when a WIC WebP codec exists and fails cleanly when it does not.
- ordinary text equal to a nonexistent `.jpg` path stays text;
- slash commands behave identically before and after the feature;
- interleaved text/image traffic preserves broadcast ordering;
- malformed/oversized image control frames are rejected without corrupting subsequent frames;
- a receiver at a small viewport stores a small frozen image; enlarging the window does not enlarge it;
- receiving at a larger viewport creates a larger image than receiving the same packet at a smaller viewport, within caps;
- resizing narrower clips/redraws safely without changing stored dimensions;
- incoming messages while an image is being prepared do not destroy the local input buffer;
- `build-lite.ps1` fails if the final EXE is over 20 MiB;
- no new runtime payload DLL appears unless explicitly justified and added to the bootstrapper resource list.

## Stop conditions

Do not declare the task complete if any of these are true:

- image handling bypasses or weakens the 16 KiB frame bound;
- image packets are subject to the ordinary 2,000-character text rejection before image parsing;
- resizing an old image recomputes/upscales it;
- decode/render occurs while holding the main console lock;
- a package adds megabytes without a measured need;
- drag/drop breaks normal typed input or slash commands;
- malformed image data can throw out of the receive loop and disconnect healthy clients unnecessarily;
- the lite executable exceeds 20 MiB.
