using P15Printer;

// ---------------------------------------------------------------------------
//  P15 Mini label printer — command-line driver (Linux / BlueZ build)
//
//  Usage:
//    P15Printer text "Hello world"        print a line of text
//    P15Printer image picture.png         print an image file
//    P15Printer feed                      advance one label
//    P15Printer scan                      list BLE devices, flag the P15
//
//  Options:
//    --width <dots>     printable width (default 384)
//    --density <0-15>   burn darkness  (default 8)
//    --no-dither        disable Floyd–Steinberg dithering for images
//    --name <filter>    BLE name filter (default "P15")
// ---------------------------------------------------------------------------

if (args.Length == 0)
{
    Console.WriteLine("Usage: P15Printer <scan|text|image|feed> [value] [options]");
    Console.WriteLine("  --width <dots>  --density <0-15>  --no-dither  --name <filter>");
    return 1;
}

string command = args[0].ToLowerInvariant();

// Diagnostic: list BLE devices and flag which expose the P15 printer service.
if (command == "scan")
{
    Console.WriteLine("Scanning Bluetooth LE devices (~6 s)...\n");
    var devices = await P15Driver.ScanAsync();
    foreach (var d in devices.OrderByDescending(d => d.HasPrinterService))
        Console.WriteLine($"  {(d.HasPrinterService ? "[P15 ✓]" : "[     ]")}  {d.Name}  {d.Address}");
    if (!devices.Any(d => d.HasPrinterService))
        Console.WriteLine("\nNo device exposing the P15 service was found. " +
                          "Pair/allow it with `bluetoothctl` (scan on, then trust/pair), " +
                          "and make sure no other app (e.g. the phone) is connected to it.");
    return 0;
}

string? value = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;

int width = GetOptInt("--width", ImageEncoder.DefaultWidthDots);
byte density = (byte)GetOptInt("--density", 8);
bool dither = !HasFlag("--no-dither");
string nameFilter = GetOptStr("--name", "P15");

// Encode the page BEFORE touching Bluetooth, so a bad argument fails fast
// without occupying the printer.
ImageEncoder.Raster? raster = null;
switch (command)
{
    case "text":
        if (value is null) { Console.Error.WriteLine("Provide text to print."); return 1; }
        raster = ImageEncoder.FromText(value, width);
        break;

    case "image":
        if (value is null) { Console.Error.WriteLine("Provide an image path."); return 1; }
        if (!File.Exists(value))
        {
            Console.Error.WriteLine($"Image file not found: \"{value}\"");
            Console.Error.WriteLine($"  (looked in {Directory.GetCurrentDirectory()})");
            return 1;
        }
        try
        {
            raster = ImageEncoder.FromFile(value, width, dither);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not read image \"{value}\": {ex.Message}");
            Console.Error.WriteLine("  Supported formats: PNG, JPG, BMP, GIF.");
            return 1;
        }
        break;

    case "feed":
        break;

    default:
        Console.Error.WriteLine($"Unknown command: {command}");
        return 1;
}

Console.WriteLine($"Searching for P15 printer (name contains \"{nameFilter}\")...");

await using var printer = await P15Driver.ConnectAsync(nameFilter);
printer.StatusReported += s => Console.WriteLine($"  [printer] {s.Describe()}");
Console.WriteLine("Connected.");

if (command == "feed")
{
    await printer.FeedAsync();
    Console.WriteLine("Fed one label.");
}
else
{
    var r = raster!.Value;
    await printer.PrintRasterAsync(r.Data, r.WidthBytes, r.Height, density);
    Console.WriteLine(command == "text" ? "Printed text." : "Printed image.");
}

// Give the printer a moment to flush its final status notifications.
await Task.Delay(800);
return 0;

// --- tiny arg helpers ---
int GetOptInt(string name, int fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
}
string GetOptStr(string name, string fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}
bool HasFlag(string name) => Array.IndexOf(args, name) >= 0;
