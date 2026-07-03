# Architecture

`pi-camera` is implemented as a static `Program` class split across several `partial` files. This keeps one shared application state while separating the code by responsibility.

## Runtime flow

1. `Program.Main` parses command-line arguments.
2. The output directory for photos and videos is created.
3. The application initializes:
   - `FramebufferDisplay` for `/dev/fb0` or another framebuffer path,
   - `CameraPreviewService` for live preview through `rpicam-vid`,
   - optional `TouchInputService`,
   - optional `GpioShutterService` instances,
   - optional HTTP API/web UI.
4. `CameraPreviewService` starts `rpicam-vid --codec mjpeg -o -` and reads the MJPEG stream from stdout.
5. Each JPEG frame is decoded to RGB and emitted as a `PreviewFrame` through `FrameReady`.
6. `Program` stores the latest frame in memory, draws it to the framebuffer, and overlays status/tab UI.
7. During video recording, preview frames are encoded as JPEG and appended to a temporary `*.rawmjpeg` file.
8. For `Full HQ` photo capture, the preview is stopped temporarily and `rpicam-still` captures the image.
9. After recording stops, `ffmpeg` converts the temporary MJPEG stream to `AVI/MJPEG` or `MP4`.

## Main modules

### `Program.cs`

Application startup and main loop. It contains default settings, argument parsing, service initialization, capture request handling, preview frame handling and shutdown logic.

### `Program.Api.cs`

Configures the ASP.NET Core HTTP server:

- static web UI from `wwwroot`,
- optional cookie-based web authentication,
- application status,
- JPEG preview and MJPEG stream,
- context-aware capture action,
- photo capture,
- video and stream toggles,
- gallery/media endpoints,
- settings read/update/reset endpoints,
- Wi-Fi endpoints.

### `Program.Auth.cs`

Handles optional web password protection, login/logout, session cookies and salted password hashing. Password protection is disabled by default.

### `Program.PersistentSettings.cs`

Loads, saves and resets user-configurable settings from a JSON file. The default location is `~/.config/pi-camera/settings.json`, unless overridden with `--settings-file`.

### `Program.HardwareReset.cs`

Contains the local physical recovery reset loop used when the web password needs to be cleared from the camera body.

### `Program.Stream.cs`

Handles external streaming through `ffmpeg`, using processed or raw preview frames as stream input.

### `Program.Capture.cs`

Handles still photo capture:

- saving a frame from the current preview,
- full-resolution still capture,
- RAW/DNG capture,
- JPG/PNG/BMP output,
- `rpicam-still` invocation,
- temporary file cleanup.

### `Program.Video.cs`

Handles video recording from preview frames:

- temporary `*.rawmjpeg` writing,
- conversion to `AVI` or `MP4`,
- `RandomFrame` segmented capture,
- concatenating RandomFrame segments with `ffmpeg concat`,
- background conversion and source-file cleanup.

### `Program.ImageProcessing.cs`

Contains visual processing logic:

- pixelation,
- color quantization,
- mono and RGB palette mapping,
- black/dark tone correction,
- RGB channel scaling,
- applying the selected look to captured stills.

`previewPixelSize` works as a detail/quality value: higher values produce smaller blocks and better detail; lower values produce stronger pixel-art blocks.

### `Program.Display.cs`

Draws the on-device UI:

- live preview screen,
- status bar,
- tabs,
- settings/mode screens,
- gallery screen,
- network screen,
- transient messages such as busy/saved/error states.

### `Program.Input.cs`

Handles user input from:

- terminal keyboard,
- touchscreen events,
- GPIO buttons.

It maps physical actions to tab switching, mode selection, gallery navigation, video toggle and capture requests.

### `Program.Network.cs`

Integrates with system networking:

- saved Wi-Fi connection listing,
- connecting to saved Wi-Fi profiles,
- adding/updating Wi-Fi connections,
- Wi-Fi radio toggling,
- hotspot control through `nmcli device wifi hotspot`,
- IP address display,
- battery status display.

### `Program.Glitch.cs`

Implements glitch modes:

- saves current preview settings,
- randomizes palette, pixelation and RGB scaling,
- restores previous settings after GlitchPhoto,
- changes glitch settings periodically during GlitchVideo.

## Services

### `CameraPreviewService`

Starts `rpicam-vid`, reads MJPEG data from stdout, detects JPEG start/end markers (`FF D8` and `FF D9`), decodes frames with ImageSharp, and emits `PreviewFrame` objects.

### `FramebufferDisplay`

Writes directly to the framebuffer in RGB565 format. It provides drawing methods for rectangles, text, RGB frames and scaled gallery images.

### `GpioShutterService`

Reads a GPIO input as an `InputPullUp` button. It applies a stable-press filter and cooldown to reduce contact bounce, and exposes the current pressed state for local hardware recovery logic.

### `TouchInputService`

Reads Linux input events from `/dev/input/eventX`, scales ABS X/Y coordinates from `0–4095` to the configured screen size, and emits touch points.

### `ImageLoader`

Loads image files into RGB buffers for the on-device gallery.

## Shared state and locking

The program uses static fields for the current tab, capture mode, preview settings, latest frame and recording state. Critical sections are protected by locks:

| Lock | Purpose |
|---|---|
| `_lastPreviewLock` | Protects the latest preview frame |
| `_settingsLock` | Protects settings used by the API and preview pipeline |
| `_displayLock` | Prevents concurrent framebuffer drawing |
| `_previewRecordLock` | Protects video recording file/state |

## External dependencies

| Dependency | Purpose |
|---|---|
| `rpicam-vid` | Continuous MJPEG live preview |
| `rpicam-still` | Full HQ and RAW still capture |
| `ffmpeg` | MJPEG-to-AVI/MP4 conversion and RandomFrame segment concatenation |
| `nmcli` | Wi-Fi and hotspot management |
| `hostname -I` | IP address lookup |
| `/sys/class/power_supply` | Automatic battery percentage lookup |
| `/dev/fb0` | Framebuffer output |
| `/dev/input/eventX` | Touchscreen input |
| GPIO | Hardware buttons |

## 3D model files

Optional 3D-printable enclosure files live in `stl/`. Print settings and assembly notes live in `stl/README.md`.

## Extension ideas

- Add unit tests for argument parsing, palette logic and settings validation.
- Add packaging scripts for a repeatable Raspberry Pi deployment.
- Add editable CAD source files such as STEP or FreeCAD files next to the ready-to-print STL files.
