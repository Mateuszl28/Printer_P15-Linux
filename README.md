# P15 Printer — sterownik .NET (Linux)

Wersja **Linux** sterownika i aplikacji CLI dla drukarki etykiet
**Marklife / Pristar „P15 Mini"** (Bluetooth LE). Funkcjonalnie identyczna z
wersją Windows — ten sam protokół i ta sama sekwencja druku — ale komunikacja
BLE idzie przez **BlueZ po D-Bus** (pakiet `Linux.Bluetooth`) zamiast WinRT, a
obraz/tekst renderuje **SixLabors.ImageSharp** zamiast `System.Drawing`.

## Parametry BLE

| Element | UUID |
|---|---|
| Serwis | `0000ff00-0000-1000-8000-00805f9b34fb` |
| RX (notify, status) | `0000ff01-…` |
| TX (write, dane) | `0000ff02-…` |
| CX (kredyty przepływu) | `0000ff03-…` |

Pakiety on-air max **95 B**. Drukarka przydziela kredyty zapisu przez CX
(`[0x01, n]`), a status raportuje przez RX (`[0xFF, kod]` = błąd,
`0xAA/0x4F/0x4B` = OK).

## Sekwencja druku

```
wakeup     00×15
enable     10 FF F1 02
density    1F 70 02 <0-15>
raster     1D 76 30 00 xL xH yL yH <dane 1bpp MSB-first>
feed       1B 4A 64
stop       10 FF F1 45
```

## Wymagania

- Linux z **BlueZ** (`bluetoothd`) i adapterem Bluetooth LE.
- .NET 8 SDK do budowy.
- Czcionka TrueType do druku tekstu (np. `fonts-dejavu-core`).

```bash
# usługa Bluetooth
sudo systemctl enable --now bluetooth
sudo rfkill unblock bluetooth

# czcionki (druk tekstu)
sudo apt install fonts-dejavu-core      # Debian/Ubuntu
# sudo dnf install dejavu-sans-fonts    # Fedora
# sudo pacman -S ttf-dejavu             # Arch
```

Uprawnienia D-Bus/BlueZ: uruchamiaj jako użytkownik z dostępem do BlueZ
(zwykle wystarczy zwykły użytkownik; przy „Access denied" dodaj się do grupy
`bluetooth` albo uruchom przez `sudo`).

## Budowanie

```bash
dotnet build -c Release P15Printer.Linux.csproj

# albo samodzielny plik wykonywalny (bez zainstalowanego .NET u odbiorcy):
dotnet publish -c Release -r linux-x64 --self-contained \
    -p:PublishSingleFile=true -o publish P15Printer.Linux.csproj
# -> publish/P15Printer
```

## Użycie

```bash
# najpierw sparuj/zezwól drukarkę
bluetoothctl
#   scan on   (poczekaj aż pojawi się P15…)   scan off
#   pair <MAC>   trust <MAC>   exit

dotnet run --project P15Printer.Linux.csproj -- scan
dotnet run --project P15Printer.Linux.csproj -- text "Etykieta nr 42"
dotnet run --project P15Printer.Linux.csproj -- image logo.png
dotnet run --project P15Printer.Linux.csproj -- feed
```

Opcje: `--width <dots>` (domyślnie 384), `--density <0-15>` (domyślnie 8),
`--no-dither`, `--name <filtr_nazwy_BLE>` (domyślnie `P15`).

> **Wskazówka:** drukarka musi być włączona, wybudzona i **niepołączona z
> telefonem** (BLE = jedno połączenie na raz). Pierwsze połączenie po
> wybudzeniu może potrwać kilkanaście sekund.

## Struktura

| Plik | Rola |
|---|---|
| `P15Driver.cs` | sterownik BLE przez BlueZ/D-Bus (połączenie, kredyty, komendy ESC/POS) |
| `ImageEncoder.cs` | obraz/tekst → raster 1-bit (ImageSharp; próg + dithering) |
| `Program.cs` | aplikacja CLI |

## Użycie jako biblioteka

```csharp
await using var printer = await P15Driver.ConnectAsync("P15");
printer.StatusReported += s => Console.WriteLine(s.Describe());

var r = ImageEncoder.FromFile("etykieta.png", widthDots: 384);
await printer.PrintRasterAsync(r.Data, r.WidthBytes, r.Height, density: 8);
```
