# BusyTag.Lib

A comprehensive .NET library for managing BusyTag devices via serial communication. This library provides device discovery, file management, LED control, and pattern configuration across Windows, macOS, and Linux platforms.

## Features

- **Cross-platform device discovery** (Windows, macOS, Linux)
- **Serial communication** with BusyTag devices
- **File management** (upload, download, delete)
- **LED color and pattern control**
- **Device configuration management**
- **Real-time event notifications**
- **Firmware update support**

## Installation

### NuGet Package Manager
```bash
Install-Package BusyTag.Lib
```

### .NET CLI
```bash
dotnet add package BusyTag.Lib
```

### PackageReference
```xml
<PackageReference Include="BusyTag.Lib" Version="0.1.1" />
```

## Quick Start

### 1. Device Discovery

```csharp
using BusyTag.Lib;

// Create manager instance
using var manager = new BusyTagManager();

// Enable verbose logging (optional)
manager.EnableVerboseLogging = true;

// Subscribe to device events
manager.DeviceConnected += (sender, port) => 
    Console.WriteLine($"Device connected on {port}");
manager.DeviceDisconnected += (sender, port) => 
    Console.WriteLine($"Device disconnected from {port}");

// Start periodic device scanning
manager.StartPeriodicDeviceSearch(intervalMs: 5000);

// Or perform one-time discovery
var devices = await manager.FindBusyTagDevice();
if (devices?.Any() == true)
{
    Console.WriteLine($"Found devices: {string.Join(", ", devices)}");
}
```

### 2. Device Connection and Control

```csharp
// Connect to a specific device
var device = new BusyTagDevice("COM3"); // Windows
// var device = new BusyTagDevice("/dev/cu.usbserial-xxx"); // macOS
// var device = new BusyTagDevice("/dev/ttyUSB0"); // Linux

// Subscribe to events
device.ConnectionStateChanged += (sender, connected) => 
    Console.WriteLine($"Connection: {(connected ? "Connected" : "Disconnected")}");

device.ReceivedDeviceBasicInformation += (sender, received) =>
    Console.WriteLine($"Device info received: {device.DeviceName} v{device.FirmwareVersion}");

// Connect to device
await device.Connect();

if (device.IsConnected)
{
    Console.WriteLine($"Connected to {device.DeviceName}");
    Console.WriteLine($"Firmware: {device.FirmwareVersion}");
    Console.WriteLine($"Free space: {device.FreeStorageSize:N0} bytes");
}
```

## Core Functionality

### LED Control

```csharp
// Set solid colors
await device.SetSolidColorAsync("red", brightness: 80);
await device.SetSolidColorAsync("blue", brightness: 100);
await device.SetSolidColorAsync("off");

// Set custom RGB colors
await device.SendRgbColorAsync(red: 255, green: 128, blue: 0, ledBits: 127);

// Get current LED configuration
device.ReceivedSolidColor += (sender, ledArgs) =>
    Console.WriteLine($"LED: {ledArgs.Color} on bits {ledArgs.LedBits}");
await device.GetSolidColorAsync();
```

### Pattern Management

```csharp
using BusyTag.Lib.Util;

// Create custom pattern
var customPattern = new List<PatternLine>
{
    new PatternLine(ledBits: 127, color: "FF0000", speed: 10, delay: 50), // Red
    new PatternLine(ledBits: 127, color: "000000", speed: 10, delay: 50), // Off
    new PatternLine(ledBits: 127, color: "0000FF", speed: 10, delay: 50), // Blue
    new PatternLine(ledBits: 127, color: "000000", speed: 10, delay: 50)  // Off
};

// Send pattern to device
await device.SetNewCustomPattern(
    list: customPattern, 
    playAfterSending: true, 
    playPatternNonStop: false);

// Use predefined patterns
var policePattern = PatternListCommands.PatternList[PatternListCommands.PatternName.GetPolice1];
if (policePattern != null)
{
    await device.SetNewCustomPattern(policePattern.PatternLines, true, false);
}

// Control pattern playback
await device.PlayPatternAsync(allow: true, repeatCount: 5);
await device.PlayPatternAsync(allow: false, repeatCount: 0); // Stop
```

### File Management

```csharp
// Upload file to device
device.FileUploadProgress += (sender, args) =>
    Console.WriteLine($"Uploading {args.FileName}: {args.ProgressLevel:F1}%");

device.FileUploadFinished += (sender, success) =>
    Console.WriteLine($"Upload {(success ? "completed" : "failed")}");

await device.SendNewFile(@"C:\path\to\image.png");

// Get file list
device.FileListUpdated += (sender, files) =>
{
    Console.WriteLine("Files on device:");
    foreach (var file in files)
        Console.WriteLine($"  {file.Name} ({file.Size:N0} bytes)");
};

await device.GetFileListAsync();

// Show picture on device
await device.ShowPictureAsync("image.png");

// Download file from device
var localPath = await device.GetFileAsync("image.png");
if (!string.IsNullOrEmpty(localPath))
    Console.WriteLine($"File downloaded to: {localPath}");

// Delete file from device
await device.DeleteFile("old_image.png");
```

### Device Configuration

```csharp
// Display brightness control
await device.SetDisplayBrightnessAsync(brightness: 75); // 0-100
var currentBrightness = await device.GetDisplayBrightnessAsync();

// Device information
var deviceName = await device.GetDeviceNameAsync();
var manufacturer = await device.GetManufactureNameAsync();
var deviceId = await device.GetDeviceIdAsync();
var firmwareVersion = await device.GetFirmwareVersionAsync();

// Storage management
var freeSpace = await device.GetFreeStorageSizeAsync();
var totalSpace = await device.GetTotalStorageSizeAsync();

// Format device storage (use with caution!)
await device.FormatDiskAsync();

// Restart device
await device.RestartDeviceAsync();
```

## Event Handling

The library provides comprehensive event notifications:

```csharp
// Connection events
device.ConnectionStateChanged += (sender, connected) => { };

// Device information events
device.ReceivedDeviceBasicInformation += (sender, received) => { };
device.ReceivedSolidColor += (sender, ledArgs) => { };
device.ReceivedDisplayBrightness += (sender, brightness) => { };

// File operation events
device.FileListUpdated += (sender, files) => { };
device.FileUploadProgress += (sender, progress) => { };
device.FileUploadFinished += (sender, success) => { };
device.ReceivedShowingPicture += (sender, imageName) => { };

// Pattern and status events
device.ReceivedPattern += (sender, pattern) => { };
device.PlayPatternStatus += (sender, isPlaying) => { };
device.FirmwareUpdateStatus += (sender, progress) => { };
device.WritingInStorage += (sender, isWriting) => { };
```

## Platform-Specific Notes

### Windows
- Uses WMI for device discovery via VID/PID (303A:81DF)
- Requires `System.Management` package
- COM port format: `COM1`, `COM2`, etc.

### macOS
- Uses `system_profiler` for USB device enumeration
- Serial port format: `/dev/cu.usbserial-xxx` or `/dev/cu.usbmodem-xxx`
- May require permissions for serial port access

### Linux
- Uses `lsusb` for device discovery
- Serial port format: `/dev/ttyUSB0`, `/dev/ttyACM0`, etc.
- User may need to be in `dialout` group for serial access

## Error Handling

```csharp
try
{
    await device.Connect();
    
    if (!device.IsConnected)
    {
        Console.WriteLine("Failed to connect to device");
        return;
    }
    
    // Device operations...
}
catch (InvalidOperationException ex)
{
    Console.WriteLine($"Device operation failed: {ex.Message}");
}
catch (Exception ex)
{
    Console.WriteLine($"Unexpected error: {ex.Message}");
}
finally
{
    device.Disconnect();
}
```

## Predefined LED Patterns

The library includes several predefined patterns:

- **Police patterns**: Police 1, Police 2
- **Color flashes**: Red, Green, Blue, Yellow, Cyan, Magenta, White
- **Running LEDs**: Various colors with moving light effect
- **Pulse effects**: Breathing light patterns in different colors

Access them via `PatternListCommands.PatternList` or use `PatternListCommands.PatternListByName()`.

## Requirements

- .NET 8.0 or .NET 9.0
- Serial port access permissions
- BusyTag device with compatible firmware

## Dependencies

- `System.IO.Ports` - Serial communication
- `System.Text.Json` - JSON serialization
- `System.Management` - Windows device discovery (Windows only)

## License

MIT License - see LICENSE file for details

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Add tests if applicable
5. Submit a pull request

## Support

For issues and questions:
- GitHub Issues: [Repository Issues](https://github.com/Greynut-Development/busytag-lib/issues)
- Documentation: [Project Wiki](https://github.com/Greynut-Development/busytag-lib/wiki)

## Changelog

### v0.1.5
- Initial release
- Device discovery and connection
- File upload/download/delete functionality
- LED color and pattern control
- Cross-platform support (Windows, macOS, Linux)
- Comprehensive event system
- Predefined pattern library