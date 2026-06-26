# API HTTP

Aplikacja uruchamia serwer HTTP, jeśli startuje z `--api=true`. Domyślny adres nasłuchiwania to:

```text
http://0.0.0.0:5000
```

Panel webowy znajduje się w `wwwroot/index.html`, a główna strona `/` przekierowuje do `/index.html`.

## Status aplikacji

```http
GET /api/status
```

Zwraca podstawowy stan programu, m.in. informację, czy aplikacja działa, czy kamera jest zajęta, czy trwa nagrywanie, jaki tryb przechwytywania jest aktywny i jaki był ostatnio zapisany plik.

Przykładowa odpowiedź:

```json
{
  "ok": true,
  "running": true,
  "busy": false,
  "recording": false,
  "randomRecording": false,
  "previewReady": true,
  "tab": "Preview",
  "captureKind": "Photo",
  "photoSource": "FullHq",
  "photoFormat": "jpg",
  "videoFormat": "mjpeg",
  "paletteMode": "Green565",
  "lastCaptured": "IMG_20260609_113000000.jpg",
  "uptimeSeconds": 123
}
```

## Podgląd obrazu

### Pojedyncza klatka JPG

```http
GET /api/preview.jpg?raw=false&q=55
```

Parametry:

| Parametr | Opis |
|---|---|
| `raw` | `true` zwraca obraz bez efektu/palety, `false` zwraca obraz po aktualnych ustawieniach |
| `q` | jakość JPG |

### Strumień MJPEG

```http
GET /api/stream.mjpg?raw=false&q=50&fps=15
```

Parametry:

| Parametr | Zakres | Opis |
|---|---:|---|
| `raw` | `true`/`false` | obraz surowy albo przetworzony |
| `q` | `35–80` | jakość JPG w strumieniu |
| `fps` | `1–30` | docelowy FPS strumienia |

## Zdjęcia i wideo

### Wykonanie zdjęcia

```http
POST /api/capture
```

Działa tylko w trybach zdjęciowych. Jeśli aktywny jest tryb `Video`, `RandomFrame` albo `GlitchVideo`, API zwróci błąd i należy użyć `/api/video/toggle`.

Przykład:

```bash
curl -X POST http://<IP_RASPBERRY_PI>:5000/api/capture
```

### Start/stop wideo

```http
POST /api/video/toggle
```

Endpoint ustawia tryb `Video` i przełącza nagrywanie.

```bash
curl -X POST http://<IP_RASPBERRY_PI>:5000/api/video/toggle
```

## Pliki mediów

### Lista plików

```http
GET /api/photos
```

Zwraca listę zdjęć i nagrań z katalogu `--out`. Obsługiwane rozszerzenia: `.jpg`, `.jpeg`, `.png`, `.bmp`, `.dng`, `.avi`, `.mp4`, `.mjpeg`, `.rawmjpeg`.

### Pobranie pliku

```http
GET /api/photos/{file}
```

Przykład:

```bash
curl -O http://<IP_RASPBERRY_PI>:5000/api/photos/IMG_20260609_113000000.jpg
```

### Usunięcie pliku

```http
DELETE /api/photos/{file}
```

Przykład:

```bash
curl -X DELETE http://<IP_RASPBERRY_PI>:5000/api/photos/IMG_20260609_113000000.jpg
```

## Ustawienia

### Pobranie ustawień

```http
GET /api/settings
```

Zwraca aktualne parametry zdjęć, wideo, palety, pikselizacji, presetów i podglądu.

### Zmiana ustawień

```http
POST /api/settings
Content-Type: application/json
```

Przykład:

```bash
curl -X POST http://<IP_RASPBERRY_PI>:5000/api/settings \
  -H 'Content-Type: application/json' \
  -d '{
    "captureKind": "GlitchPhoto",
    "photoFormat": "jpg",
    "photoSource": "FullHq",
    "paletteMode": "Amber",
    "selectedColorAmount": 16,
    "pixelSize": 512,
    "glitchStrength": 7,
    "glitchPhotoCount": 4,
    "preview": {
      "ev": -1.2,
      "contrast": 0.8,
      "saturation": 0.85,
      "blackLevel": 25,
      "darkLevel": 0.95,
      "denoise": "cdn_off"
    }
  }'
```

Najważniejsze pola:

| Pole | Przykłady | Opis |
|---|---|---|
| `captureKind` | `Photo`, `Video`, `RandomFrame`, `GlitchPhoto`, `GlitchVideo` | aktywny tryb przechwytywania |
| `lookPreset` | `NORMAL`, `LOW32`, `LOW16`, `RETRO8`, `MONO4` | szybki preset wyglądu |
| `photoFormat` | `jpg`, `png`, `bmp`, `raw`, `rawjpg` | format zdjęcia |
| `photoSource` | `FullHq`, `Preview` | źródło zdjęcia |
| `photoWidth` | `4056` | szerokość zdjęcia HQ |
| `photoHeight` | `3040` | wysokość zdjęcia HQ |
| `jpgQuality` | `70–100` | jakość JPG |
| `photoEv` | `-8.0–8.0` | kompensacja ekspozycji zdjęcia |
| `videoFormat` | `mjpeg`, `mp4` | format docelowy wideo |
| `previewFps` | `1–30` | FPS podglądu/nagrywania |
| `randomFrameMinFps` | `1–30` | minimalny FPS segmentu random |
| `randomFrameMaxFps` | `1–30` | maksymalny FPS segmentu random |
| `randomFrameSeconds` | `1–15` | długość segmentu random |
| `glitchStrength` | `1–10` | siła glitch |
| `glitchChangeMs` | `100–5000` | interwał zmian glitch video |
| `glitchPaletteEnabled` | `true`/`false` | losowanie palety w glitch |
| `glitchPixelsEnabled` | `true`/`false` | losowanie pikselizacji w glitch |
| `glitchRgbEnabled` | `true`/`false` | losowanie skali RGB w glitch |
| `glitchPhotoCount` | `1–12` | liczba zdjęć w serii glitch |
| `sensorMode` | `full`, `bin`, `fast` | tryb sensora |
| `selectedColorAmount` | `2–256` | liczba kolorów/poziomów |
| `pixelSize` / `previewPixelSize` | `1–2048` | jakość/pikselizacja |
| `paletteMode` | `Green565`, `Balanced`, `Amber`, itd. | paleta kolorów |
| `redScale`, `greenScale`, `blueScale` | `0.0–2.0` | skala kanałów RGB |
| `lowSaveGamma` | `0.35–2.5` | korekcja zapisu dla presetów low |
| `lowGrayYellowFix` | `0–80` | korekcja szaro-żółtego zafarbu |

Pola w obiekcie `preview`:

| Pole | Zakres | Opis |
|---|---:|---|
| `ev` | `-8.0–8.0` | ekspozycja podglądu |
| `sharpness` | `0.0–16.0` | ostrość |
| `contrast` | `0.0–32.0` | kontrast |
| `saturation` | `0.0–32.0` | nasycenie |
| `brightness` | `-1.0–1.0` | jasność |
| `blackLevel` | `0–240` | odcięcie czerni |
| `darkLevel` | `0.25–2.0` | wzmocnienie/ściemnienie tonów |
| `previewPixelSize` | zależnie od źródła | poziom szczegółowości |
| `previewColorLevels` | `2–256` | liczba kolorów |
| `denoise` | `cdn_off`, `cdn_fast`, `cdn_hq` | odszumianie `rpicam` |

### Dostępne opcje

```http
GET /api/settings/options
```

Zwraca listy do budowania UI, m.in. dostępne tryby, formaty, palety, wartości kolorów i pikselizacji.

## Sieć

### Status sieci

```http
GET /api/network
```

Zwraca status radia Wi‑Fi, hotspotu, aktywnego połączenia, IP, zapisanych sieci i ostatni komunikat sieciowy.

### Zapis lub połączenie z Wi‑Fi

```http
POST /api/network/wifi
Content-Type: application/json
```

Przykład:

```bash
curl -X POST http://<IP_RASPBERRY_PI>:5000/api/network/wifi \
  -H 'Content-Type: application/json' \
  -d '{"ssid":"MojaSiec","password":"tajnehaslo","connectNow":true}'
```

### Połączenie z zapisaną siecią

```http
POST /api/network/wifi/connect
Content-Type: application/json
```

Przykład:

```bash
curl -X POST http://<IP_RASPBERRY_PI>:5000/api/network/wifi/connect \
  -H 'Content-Type: application/json' \
  -d '{"name":"MojaSiec"}'
```

## Kody błędów

- `404 Not Found` — brak pliku albo brak gotowego podglądu.
- `409 Conflict` — kamera jest zajęta.
- `400 Bad Request` — niepoprawne dane lub próba wykonania zdjęcia w trybie wideo.
