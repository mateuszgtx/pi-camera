# PiCameraFramebuffer

Lekki program C# bez desktopu: live preview na /dev/fb0, zdjęcie z ekranu, GPIO17 i Enter/Spacja.

## Uruchomienie

```bash
dotnet restore
dotnet run -- --fb=/dev/fb0 --width=320 --height=240
```

Z dotykiem, np. event2:

```bash
dotnet run -- --fb=/dev/fb0 --width=320 --height=240 --touch=/dev/input/event2
```

Klawisze: Enter/Spacja = zdjęcie, P = preview on/off, Q/Esc = wyjście.

## Znalezienie dotyku

```bash
cat /proc/bus/input/devices
sudo evtest
```

## Publikacja

```bash
dotnet publish -c Release -r linux-arm64 --self-contained true -o publish
sudo ./publish/PiCameraFramebuffer --fb=/dev/fb0 --width=320 --height=240
```
