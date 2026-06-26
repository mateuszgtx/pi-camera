C E G Ł A
                 
`pi-camera` is a C#/.NET application for Raspberry Pi that turns a Pi, a camera module and a small framebuffer/TFT screen into a compact digital camera. It provides a live camera preview, photo and video capture modes, a local gallery, touchscreen support, GPIO buttons, Wi-Fi/hotspot controls, and an HTTP web panel with a REST-style API.

The application uses Raspberry Pi Camera tools (`rpicam-vid` and `rpicam-still`) for image capture, stores media locally, and renders its on-device UI directly to a Linux framebuffer such as `/dev/fb0`.

## Features

- Live camera preview rendered to an RGB565 framebuffer.
- Photo capture in `jpg`, `png`, `bmp`, `raw`/`dng`, and `rawjpg` modes.
- Photo sources: `Full HQ` through `rpicam-still`, or `Preview` from the current preview frame.
- Video recording from preview frames to `AVI/MJPEG` or `MP4` through `ffmpeg`.
- `RandomFrame` video mode, where each segment is recorded with a random FPS.
- `GlitchPhoto` and `GlitchVideo` modes with randomized palette, pixelation and RGB channel scaling.
- Visual presets: `NORMAL`, `LOW32`, `LOW16`, `RETRO8`, `MONO4`.
- Color palettes: `GREEN565`, `BALANCED`, `GREEN`, `YELLOW`, `BLUE`, `RED`, `CYAN`, `MAGENTA`, `AMBER`, `GRAY`, `WARM`, `COLD`.
- Keyboard, touchscreen and GPIO button control.
- On-device media gallery.
- Static web UI from `wwwroot/index.html` and HTTP API endpoints.
- Wi-Fi and hotspot management through `NetworkManager`/`nmcli`.
- Optional battery status reading from a file, `/sys/class/power_supply`, or a custom command.

## Project structure

```text
pi-camera-master/
├── pi-camera.slnx
├── pi-camera/
│   ├── Program.cs                    # application startup, argument parsing, main loop
│   ├── Program.Api.cs                # web panel and HTTP API endpoints
│   ├── Program.Capture.cs            # photo capture through rpicam-still
│   ├── Program.Display.cs            # screen UI, tabs, gallery, status messages
│   ├── Program.Glitch.cs             # glitch effects for photos and videos
│   ├── Program.ImageProcessing.cs    # pixelation, palettes and visual processing
│   ├── Program.Input.cs              # keyboard, touch and GPIO input
│   ├── Program.Network.cs            # Wi-Fi, hotspot, IP and battery status
│   ├── Program.Settings.cs           # presets, argument helpers and setting logic
│   ├── Program.Video.cs              # MJPEG/MP4 recording and RandomFrame video
│   ├── Services/
│   │   ├── CameraPreviewService.cs   # rpicam-vid -> MJPEG -> RGB frames
│   │   ├── FramebufferDisplay.cs     # direct drawing to /dev/fb0
│   │   ├── GpioShutterService.cs     # GPIO button handling
│   │   ├── TouchInputService.cs      # /dev/input/eventX reader
│   │   ├── ImageLoader.cs            # gallery image loading
│   │   ├── CameraSettings.cs
│   │   └── PreviewSettings.cs
│   ├── wwwroot/index.html            # browser-based control panel
│   └── pi-camera.csproj
└── docs/
    ├── API.md
    ├── ARCHITECTURE.md
    └── RASPBERRY_PI_SETUP.md
```

## Requirements

### Hardware

- Raspberry Pi with a camera supported by `rpicam-apps`.
- A framebuffer display, usually `/dev/fb0`.
- Optional touchscreen exposed as `/dev/input/eventX`.
- Optional GPIO buttons connected as `InputPullUp` inputs, active-low (`LOW`).

### System software

- Linux/Raspberry Pi OS.
- .NET SDK/runtime compatible with the project `TargetFramework`: `net10.0`.
- `rpicam-apps`, which provides `rpicam-vid` and `rpicam-still`.
- `ffmpeg` for converting raw MJPEG recordings to `AVI`/`MP4`.
- `NetworkManager` and `nmcli` for Wi-Fi and hotspot functions.

Example system package installation:

```bash
sudo apt update
sudo apt install -y rpicam-apps ffmpeg network-manager
```

## Build

```bash
git clone <REPOSITORY_URL>
cd pi-camera-master/pi-camera

dotnet restore
dotnet build -c Release
```

Publish as a self-contained application for 64-bit Raspberry Pi OS:

```bash
dotnet publish -c Release -r linux-arm64 --self-contained true -o ./publish
```

For 32-bit Raspberry Pi OS use `linux-arm`:

```bash
dotnet publish -c Release -r linux-arm --self-contained true -o ./publish
```

## Quick start

```bash
sudo dotnet run --project pi-camera/pi-camera.csproj -- \
  --fb=/dev/fb0 \
  --width=480 \
  --height=320 \
  --out=/home/admin/Pictures/PiCamera \
  --api=true \
  --api-url=http://0.0.0.0:5000
```

After startup:

- the device screen shows the `CEGŁA` camera interface,
- photos and videos are saved to the directory passed with `--out`,
- the web panel is available at `http://<RASPBERRY_PI_IP>:5000`.

## Example with touchscreen and GPIO buttons

```bash
sudo ./publish/pi-camera \
  --fb=/dev/fb0 \
  --touch=/dev/input/event0 \
  --width=480 \
  --height=320 \
  --invert-y=true \
  --gpio-pin=17 \
  --button-tab-pin=27 \
  --button-mode-pin=22 \
  --button-prev-pin=5 \
  --button-next-pin=6 \
  --button-gallery-pin=13 \
  --button-video-pin=19 \
  --out=/home/admin/Pictures/PiCamera \
  --api=true \
  --api-url=http://0.0.0.0:5000
```

## Controls

### Keyboard

| Key | Action |
|---|---|
| `Enter` / `Space` | Capture a photo or trigger the current capture action |
| `1` | Preview tab |
| `2` | Mode/settings tab |
| `3` | Gallery tab |
| `4` | Network tab |
| `Left` / `Right` | Previous/next item in the gallery |
| `Q` / `Esc` | Exit the application |

### Touchscreen

The bottom navigation bar has four tabs:

- `POD` — preview,
- `TRYB` — mode/settings,
- `GAL` — gallery,
- `SIEC` — network.

In the mode/settings tab, touching the left half decreases the selected value and touching the right half increases it. The button above the bottom navigation changes the settings page.

When the application is started with `--touch-capture=true`, touching the live preview area can trigger a capture.

### GPIO

The main shutter button is configured with `--gpio-pin`. Additional buttons can be assigned with:

- `--button-tab-pin`,
- `--button-mode-pin`,
- `--button-prev-pin`,
- `--button-next-pin`,
- `--button-gallery-pin`,
- `--button-video-pin`.

Buttons are read as `InputPullUp`, so the simplest wiring is a button between GPIO and GND.

## Capture modes

| Mode | Description |
|---|---|
| `Photo` | Standard still photo capture |
| `Video` | Start/stop video recording from preview frames |
| `RandomFrame` | Segmented recording with random FPS per segment |
| `GlitchPhoto` | One photo or a burst of photos with randomized glitch settings |
| `GlitchVideo` | Video recording with changing glitch effects |

## Output files

Photos:

```text
IMG_yyyyMMdd_HHmmssfff.jpg
IMG_yyyyMMdd_HHmmssfff.png
IMG_yyyyMMdd_HHmmssfff.bmp
IMG_yyyyMMdd_HHmmssfff.dng
```

Videos:

```text
VID_yyyyMMdd_HHmmss.avi
VID_yyyyMMdd_HHmmss.mp4
RANDOM_yyyyMMdd_HHmmss.avi
RANDOM_yyyyMMdd_HHmmss.mp4
```

Temporary `*.rawmjpeg` files and RandomFrame segment files are removed after successful conversion.

## Main command-line arguments

| Argument | Default | Description |
|---|---:|---|
| `--fb=` | `/dev/fb0` | Framebuffer device path |
| `--touch=` | empty | Touch input device path, for example `/dev/input/event0` |
| `--out=` | `/home/admin/Pictures/PiCamera` | Output directory for photos and videos |
| `--width=` | `480` | Framebuffer UI width |
| `--height=` | `320` | Framebuffer UI height |
| `--rotate=` | `0` | Display rotation passed to the framebuffer renderer |
| `--swap-rb=` | `false` | Swap red and blue channels on the display |
| `--fps=` | `20` | Live preview FPS |
| `--api=` | `true` | Enable HTTP API and web UI |
| `--api-url=` | `http://0.0.0.0:5000` | HTTP listen URL |
| `--gpio-pin=` | `-1` | Main shutter GPIO pin; `-1` disables it |
| `--touch-capture=` | `false` | Capture by touching the preview area |
| `--look=` | `LOW32` | Visual preset |
| `--palette=` | `green565` | Color palette |
| `--colors=` | `32` | Number of color levels |
| `--preview-pixel=` | preset-dependent | Preview pixelation/detail level |
| `--photo-source=` | `full` | `full`/`FullHq` or `preview` |
| `--photo-width=` | `4056` | Full HQ photo width |
| `--photo-height=` | `3040` | Full HQ photo height |
| `--random-min-fps=` | `1` | Minimum FPS for `RandomFrame` segments |
| `--random-max-fps=` | `12` | Maximum FPS for `RandomFrame` segments |
| `--random-seconds=` | `10` | RandomFrame segment length in seconds |
| `--glitch-strength=` | `5` | Glitch intensity, `1–10` |
| `--glitch-change-ms=` | `700` | GlitchVideo setting change interval in milliseconds |
| `--hotspot-ssid=` | `PiCamera` | Hotspot SSID |
| `--hotspot-pass=` | `picamera123` | Hotspot password |

More setup details are available in [`docs/RASPBERRY_PI_SETUP.md`](docs/RASPBERRY_PI_SETUP.md).

## HTTP API

The API is enabled by default with `--api=true`. The default listen URL is:

```text
http://0.0.0.0:5000
```

Main endpoints include:

- `GET /api/status`
- `GET /api/preview.jpg`
- `GET /api/stream.mjpg`
- `POST /api/capture`
- `POST /api/video/toggle`
- `GET /api/photos`
- `GET /api/photos/{file}`
- `DELETE /api/photos/{file}`
- `GET /api/settings`
- `POST /api/settings`
- `GET /api/settings/options`
- `GET /api/network`
- `POST /api/network/wifi`
- `POST /api/network/wifi/connect`

Full endpoint documentation is in [`docs/API.md`](docs/API.md).

## Notes for maintainers

- The project currently targets `net10.0`.
- `pi-camera.csproj` contains two `System.Device.Gpio` references with different versions. Keep only one version before publishing the project publicly.
- The API has no authentication. Do not expose it to an untrusted network without adding access control.
- Consider adding a `LICENSE` file before publishing.
