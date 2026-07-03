## CEGŁA

`pi-camera` is a C#/.NET application for Raspberry Pi that turns a Pi, a camera module and a small framebuffer/TFT screen into a compact digital camera. It provides a live camera preview, photo and video capture modes, a local gallery, touchscreen support, GPIO buttons, Wi-Fi/hotspot controls, and an HTTP web panel with a REST-style API.

The application uses Raspberry Pi Camera tools (`rpicam-vid` and `rpicam-still`) for image capture, stores media locally, and renders its on-device UI directly to a Linux framebuffer such as `/dev/fb0`.

## Features

* Live camera preview rendered to an RGB565 framebuffer.
* Photo capture in `jpg`, `png`, `bmp`, `raw`/`dng`, and `rawjpg` modes.
* Photo sources: `Full HQ` through `rpicam-still`, or `Preview` from the current preview frame.
* Video recording from preview frames to `AVI/MJPEG` or `MP4` through `ffmpeg`.
* `RandomFrame` video mode, where each segment is recorded with a random FPS.
* `Stream` capture mode for sending the processed or raw preview to an external streaming URL.
* `GlitchPhoto` and `GlitchVideo` modes with randomized palette, pixelation and RGB channel scaling.
* Visual presets: `NORMAL`, `LOW32`, `LOW16`, `RETRO8`, `MONO4`.
* Color palettes: `GREEN565`, `BALANCED`, `GREEN`, `YELLOW`, `BLUE`, `RED`, `CYAN`, `MAGENTA`, `AMBER`, `GRAY`, `WARM`, `COLD`.
* Keyboard, touchscreen and GPIO button control.
* On-device media gallery.
* Static web UI from `wwwroot/index.html` with one context-aware main action button.
* HTTP API endpoints for status, preview, capture action, media gallery, settings, stream, audio, Wi-Fi and hotspot controls.
* Persistent settings saved to a JSON file and restored after restart.
* Reset-to-defaults action from the web panel and HTTP API.
* Optional web password protection for the HTTP API and browser panel; disabled by default.
* Wi-Fi and hotspot management through `NetworkManager`/`nmcli`.
* Optional battery status reading from a file, `/sys/class/power_supply`, or a custom command.

## Project structure

```text
pi-camera-master/
├── pi-camera.slnx
├── pi-camera/
│   ├── Program.cs                    # application startup, argument parsing, main loop
│   ├── Program.Api.cs                # web panel and HTTP API endpoints
│   ├── Program.Auth.cs               # optional web password login/session handling
│   ├── Program.Audio.cs              # audio input and Bluetooth audio helpers
│   ├── Program.Capture.cs            # photo capture through rpicam-still
│   ├── Program.Display.cs            # screen UI, tabs, gallery, status messages
│   ├── Program.Glitch.cs             # glitch effects for photos and videos
│   ├── Program.HardwareReset.cs      # GPIO hold combo for emergency password reset
│   ├── Program.ImageProcessing.cs    # pixelation, palettes and visual processing
│   ├── Program.Input.cs              # keyboard, touch and GPIO input
│   ├── Program.Network.cs            # Wi-Fi, hotspot, IP and battery status
│   ├── Program.PersistentSettings.cs # JSON settings load/save/reset
│   ├── Program.Settings.cs           # presets, argument helpers and setting logic
│   ├── Program.Stream.cs             # external video streaming
│   ├── Program.Video.cs              # MJPEG/MP4 recording and RandomFrame video
│   ├── Program.WebUi.cs              # static web UI helper
│   ├── Services/
│   │   ├── CameraPreviewService.cs   # rpicam-vid -> MJPEG -> RGB frames
│   │   ├── CameraService.cs          # camera service helpers
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

* Raspberry Pi with a camera supported by `rpicam-apps`.
* A framebuffer display, usually `/dev/fb0`.
* Optional touchscreen exposed as `/dev/input/eventX`.
* Optional GPIO buttons connected as `InputPullUp` inputs, active-low (`LOW`).

### System software

* Linux/Raspberry Pi OS.
* .NET SDK/runtime compatible with the project `TargetFramework`: `net10.0`.
* `rpicam-apps`, which provides `rpicam-vid` and `rpicam-still`.
* `ffmpeg` for converting raw MJPEG recordings to `AVI`/`MP4`.
* `NetworkManager` and `nmcli` for Wi-Fi and hotspot functions.

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
  --settings-file=/home/admin/.config/pi-camera/settings.json \
  --api=true \
  --api-url=http://0.0.0.0:5000
```

After startup:

* the device screen shows the `CEGŁA` camera interface,
* photos and videos are saved to the directory passed with `--out`,
* the web panel is available at `http://<RASPBERRY_PI_IP>:5000`.

## Persistent settings

Settings changed from the web panel or from the device controls are saved automatically to a JSON file. On the next start, the application first applies command-line defaults and then overlays the saved settings from disk.

Default settings path:

```text
~/.config/pi-camera/settings.json
```

You can choose a different path with:

```bash
--settings-file=/var/lib/pi-camera/settings.json
```

Use **Reset defaults** in the web panel, or call `POST /api/settings/reset`, to restore the built-in defaults and overwrite the saved JSON file. This settings reset does not create a web password; password protection remains optional.

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
  --settings-file=/home/admin/.config/pi-camera/settings.json \
  --api=true \
  --api-url=http://0.0.0.0:5000
```

## Run as a systemd service

For a Raspberry Pi camera device, it is recommended to run `pi-camera` as a `systemd` service. This allows the application to start automatically after boot, restart after crashes, and be managed with standard Linux service commands.

### 1. Publish the application

From the project directory:

```bash
dotnet publish -c Release -r linux-arm64 --self-contained true -o ./publish
```

For 32-bit Raspberry Pi OS, use:

```bash
dotnet publish -c Release -r linux-arm --self-contained true -o ./publish
```

### 2. Copy the application to `/opt`

```bash
sudo mkdir -p /opt/pi-camera
sudo cp -r ./publish/* /opt/pi-camera/
sudo chmod +x /opt/pi-camera/pi-camera
```

Create the media output directory and a directory for persistent settings:

```bash
sudo mkdir -p /home/admin/Pictures/PiCamera
sudo chown -R admin:admin /home/admin/Pictures/PiCamera
sudo mkdir -p /var/lib/pi-camera
```

### 3. Create the systemd service file

Create a new service file:

```bash
sudo nano /etc/systemd/system/pi-camera.service
```

Paste the following configuration:

```ini
[Unit]
Description=CEGŁA Pi Camera
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=root
WorkingDirectory=/opt/pi-camera
ExecStart=/opt/pi-camera/pi-camera \
  --fb=/dev/fb0 \
  --touch=/dev/input/event0 \
  --width=480 \
  --height=320 \
  --out=/home/admin/Pictures/PiCamera \
  --settings-file=/var/lib/pi-camera/settings.json \
  --api=true \
  --api-url=http://0.0.0.0:5000 \
  --gpio-pin=17 \
  --button-tab-pin=27 \
  --button-mode-pin=22 \
  --button-prev-pin=5 \
  --button-next-pin=6 \
  --button-gallery-pin=13 \
  --button-video-pin=19

Restart=always
RestartSec=3

[Install]
WantedBy=multi-user.target
```

Adjust the framebuffer path, touch device, screen size, GPIO pins and output directory if your setup is different.

The service runs as `root` because the application may need direct access to `/dev/fb0`, `/dev/input/eventX`, GPIO and camera devices.

### 4. Enable and start the service

Reload systemd:

```bash
sudo systemctl daemon-reload
```

Enable automatic startup on boot:

```bash
sudo systemctl enable pi-camera.service
```

Start the application:

```bash
sudo systemctl start pi-camera.service
```

Check service status:

```bash
sudo systemctl status pi-camera.service
```

### 5. View logs

Live logs:

```bash
sudo journalctl -u pi-camera.service -f
```

Logs from the current boot:

```bash
sudo journalctl -u pi-camera.service -b
```

Last 100 log lines:

```bash
sudo journalctl -u pi-camera.service -n 100
```

### 6. Restart, stop or disable the service

Restart the camera application:

```bash
sudo systemctl restart pi-camera.service
```

Stop the application:

```bash
sudo systemctl stop pi-camera.service
```

Disable autostart:

```bash
sudo systemctl disable pi-camera.service
```

### 7. Update the installed application

Build a new release:

```bash
dotnet publish -c Release -r linux-arm64 --self-contained true -o ./publish
```

Stop the running service:

```bash
sudo systemctl stop pi-camera.service
```

Replace the installed files:

```bash
sudo cp -r ./publish/* /opt/pi-camera/
sudo chmod +x /opt/pi-camera/pi-camera
```

Start the service again:

```bash
sudo systemctl start pi-camera.service
```

Check logs after the update:

```bash
sudo journalctl -u pi-camera.service -n 100
```

### Troubleshooting systemd startup

If the service does not start, check:

```bash
sudo systemctl status pi-camera.service
sudo journalctl -u pi-camera.service -b
```

Common issues:

| Problem                  | Possible cause                                                                  |
| ------------------------ | ------------------------------------------------------------------------------- |
| No image on display      | Wrong framebuffer path, for example `/dev/fb0` vs `/dev/fb1`                    |
| Touch does not work      | Wrong `/dev/input/eventX` device                                                |
| Camera does not start    | `rpicam-apps` missing or camera not detected                                    |
| GPIO buttons do not work | Wrong GPIO pin numbers or missing pull-up wiring                                |
| Web panel is unavailable | API disabled, wrong port, firewall, or wrong Raspberry Pi IP address            |
| Permission errors        | Service user does not have access to framebuffer, input, GPIO or camera devices |

Useful checks:

```bash
ls /dev/fb*
ls /dev/input/
rpicam-hello --list-cameras
ip addr
```

If you changed the service file, always reload systemd before restarting:

```bash
sudo systemctl daemon-reload
sudo systemctl restart pi-camera.service
```

## Controls

### Web panel

The web panel uses one main action button instead of separate Photo/Video/Stream buttons. Select the desired `Capture mode` in **Settings → Mode**, then press the main button:

| Capture mode | Main button action |
| ------------- | ------------------ |
| `Photo` | Queues a photo |
| `Video` | Starts or stops normal video recording |
| `RandomFrame` | Starts or stops RandomFrame video recording |
| `GlitchPhoto` | Queues one glitch photo or a glitch burst |
| `GlitchVideo` | Starts or stops glitch video recording |
| `Stream` | Starts or stops external streaming |

The button label changes automatically, for example `Photo`, `Video`, `Stop Video`, `Stream`, or `Stop Stream`.

The **Reset defaults** button in **Settings → Mode** restores the built-in defaults and saves them to the persistent settings file. It does not force password protection on.

Password protection is configured in **Settings → Security**. It is disabled by default. After a password is set, API calls, preview streams, gallery files and settings require login. Use **Remove password** in the same tab to turn it off.

Emergency password reset from the device: hold all configured function GPIO buttons except the shutter button for 10 seconds. Function buttons are the buttons assigned with `--button-tab-pin`, `--button-mode-pin`, `--button-prev-pin`, `--button-next-pin`, `--button-gallery-pin` and `--button-video-pin`. At least two function buttons must be configured for this emergency reset loop to run.

### Keyboard

| Key               | Action                                                |
| ----------------- | ----------------------------------------------------- |
| `Enter` / `Space` | Capture a photo or trigger the current capture action |
| `1`               | Preview tab                                           |
| `2`               | Mode/settings tab                                     |
| `3`               | Gallery tab                                           |
| `4`               | Network tab                                           |
| `Left` / `Right`  | Previous/next item in the gallery                     |
| `Q` / `Esc`       | Exit the application                                  |

### Touchscreen

The bottom navigation bar has four tabs:

* `POD` — preview,
* `TRYB` — mode/settings,
* `GAL` — gallery,
* `SIEC` — network.

In the mode/settings tab, touching the left half decreases the selected value and touching the right half increases it. The button above the bottom navigation changes the settings page.

When the application is started with `--touch-capture=true`, touching the live preview area can trigger a capture.

### GPIO

The main shutter button is configured with `--gpio-pin`. Additional buttons can be assigned with:

* `--button-tab-pin`,
* `--button-mode-pin`,
* `--button-prev-pin`,
* `--button-next-pin`,
* `--button-gallery-pin`,
* `--button-video-pin`.

Buttons are read as `InputPullUp`, so the simplest wiring is a button between GPIO and GND.

## Capture modes

| Mode          | Description                                                    |
| ------------- | -------------------------------------------------------------- |
| `Photo`       | Standard still photo capture                                   |
| `Video`       | Start/stop video recording from preview frames                 |
| `RandomFrame` | Segmented recording with random FPS per segment                |
| `GlitchPhoto` | One photo or a burst of photos with randomized glitch settings |
| `GlitchVideo` | Video recording with changing glitch effects                   |
| `Stream`      | External stream using the configured stream URL and format      |

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

| Argument              |                         Default | Description                                              |
| --------------------- | ------------------------------: | -------------------------------------------------------- |
| `--fb=`               |                      `/dev/fb0` | Framebuffer device path                                  |
| `--touch=`            |                           empty | Touch input device path, for example `/dev/input/event0` |
| `--out=`              | `/home/admin/Pictures/PiCamera` | Output directory for photos and videos                   |
| `--settings-file=`    | `~/.config/pi-camera/settings.json` | JSON file used to persist UI/device settings       |
| `--width=`            |                           `480` | Framebuffer UI width                                     |
| `--height=`           |                           `320` | Framebuffer UI height                                    |
| `--rotate=`           |                             `0` | Display rotation passed to the framebuffer renderer      |
| `--swap-rb=`          |                         `false` | Swap red and blue channels on the display                |
| `--fps=`              |                            `20` | Live preview FPS                                         |
| `--api=`              |                          `true` | Enable HTTP API and web UI                               |
| `--api-url=`          |           `http://0.0.0.0:5000` | HTTP listen URL                                          |
| `--gpio-pin=`         |                            `-1` | Main shutter GPIO pin; `-1` disables it                  |
| `--touch-capture=`    |                         `false` | Capture by touching the preview area                     |
| `--look=`             |                         `LOW32` | Visual preset                                            |
| `--palette=`          |                      `green565` | Color palette                                            |
| `--colors=`           |                            `32` | Number of color levels                                   |
| `--preview-pixel=`    |                preset-dependent | Preview pixelation/detail level                          |
| `--photo-source=`     |                          `full` | `full`/`FullHq` or `preview`                             |
| `--photo-width=`      |                          `4056` | Full HQ photo width                                      |
| `--photo-height=`     |                          `3040` | Full HQ photo height                                     |
| `--random-min-fps=`   |                             `1` | Minimum FPS for `RandomFrame` segments                   |
| `--random-max-fps=`   |                            `12` | Maximum FPS for `RandomFrame` segments                   |
| `--random-seconds=`   |                            `10` | RandomFrame segment length in seconds                    |
| `--glitch-strength=`  |                             `5` | Glitch intensity, `1–10`                                 |
| `--glitch-change-ms=` |                           `700` | GlitchVideo setting change interval in milliseconds      |
| `--stream-url=`       |                           empty | External stream target URL                               |
| `--stream-format=`    |                         `auto` | Stream output format: `auto`, `flv`, `mpegts`, `rtsp`    |
| `--stream-fps=`       |                            `15` | Stream FPS                                               |
| `--stream-bitrate=`   |                          `2500` | Stream bitrate in kbps                                   |
| `--stream-jpeg-quality=` |                        `75` | JPEG quality used by stream input frames                 |
| `--stream-raw=`       |                         `false` | Stream raw/normal preview instead of filtered output     |
| `--audio=`            |                          `true` | Enable audio where supported                             |
| `--audio-mode=`       |                          `Auto` | Audio input mode                                         |
| `--audio-device=`     |                          empty | Optional audio device override                           |
| `--hotspot-ssid=`     |                      `PiCamera` | Hotspot SSID                                             |
| `--hotspot-pass=`     |                   `picamera123` | Hotspot password                                         |

More setup details are available in [`docs/RASPBERRY_PI_SETUP.md`](docs/RASPBERRY_PI_SETUP.md).

## HTTP API

The API is enabled by default with `--api=true`. The default listen URL is:

```text
http://0.0.0.0:5000
```

Main endpoints include:

* `GET /api/auth/status`
* `POST /api/auth/login`
* `POST /api/auth/logout`
* `POST /api/auth/password`
* `POST /api/auth/password/clear`
* `GET /api/status`
* `GET /api/preview.jpg`
* `GET /api/stream.mjpg`
* `POST /api/action`
* `POST /api/capture`
* `POST /api/video/toggle`
* `POST /api/stream/toggle`
* `POST /api/stream/start`
* `POST /api/stream/stop`
* `GET /api/photos`
* `GET /api/photos/{file}`
* `DELETE /api/photos/{file}`
* `GET /api/settings`
* `POST /api/settings`
* `POST /api/settings/reset`
* `GET /api/settings/options`
* `GET /api/network`
* `POST /api/network/wifi`
* `POST /api/network/wifi/connect`

Full endpoint documentation is in [`docs/API.md`](docs/API.md).

## Security notes

The HTTP API has optional cookie-based password protection. It is disabled by default so existing local/offline setups keep working. Set a password in **Settings → Security** to protect API actions, preview streams, gallery files and settings. The password is stored in the persistent settings JSON as a salted PBKDF2-SHA256 hash, not as plain text.

Emergency reset is available from the camera body: hold all configured function GPIO buttons except the shutter for 10 seconds to clear the web password and save that state.

Do not expose the camera web panel to public or untrusted networks without additional access control, firewall rules, a VPN, or a reverse proxy with authentication.

The systemd example runs the service as `root` because the application may need direct hardware access. For a production deployment, you can create a dedicated Linux user and grant it only the required permissions for framebuffer, input, camera and GPIO access.

## Notes for maintainers

* The project currently targets `net10.0`.
* `pi-camera.csproj` contains two `System.Device.Gpio` references with different versions. Keep only one version before publishing the project publicly.
* Web password protection is optional and off by default. Do not expose the panel/API to an untrusted network without enabling it and adding network-level protection.
* Consider adding a `LICENSE` file before publishing.
