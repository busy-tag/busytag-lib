# BusyTag.Lib

[![NuGet Version](https://img.shields.io/nuget/v/BusyTag.Lib.svg)](https://www.nuget.org/packages/BusyTag.Lib/)
[![Downloads](https://img.shields.io/nuget/dt/BusyTag.Lib.svg)](https://www.nuget.org/packages/BusyTag.Lib/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A powerful and intuitive .NET library for seamless BusyTag device management via serial communication. Control LED patterns, manage files, and configure devices across Windows, macOS, and Linux platforms with ease.

## 🚀 Key Features

- **🔍 Cross-platform device discovery** - Automatic detection on Windows, macOS, and Linux
- **📡 Robust serial communication** - Reliable connection management with BusyTag devices
- **📁 Complete file management** - Upload, download, and delete files with progress tracking
- **💡 Advanced LED control** - Solid colors, custom patterns, and predefined animations
- **⚙️ Device configuration** - Brightness, storage, and system settings management
- **📢 Real-time notifications** - Comprehensive event system for all device operations
- **🔄 Firmware update support** - Built-in firmware update capabilities

## 📦 Installation

### Package Manager Console
```powershell
Install-Package BusyTag.Lib
```

### .NET CLI
```bash
dotnet add package BusyTag.Lib
```

### PackageReference
```xml
<PackageReference Include="BusyTag.Lib" Version="0.2.2" />
```

## 🏃‍♂️ Quick Start

### Device Discovery and Connection

```csharp
using BusyTag.Lib;

// Create and configure the manager
using var manager = new BusyTagManager();

// Subscribe to device events
manager.DeviceConnected += (sender, port) => 
    Console.WriteLine($"✅ Device connected on {port}");
manager.DeviceDisconnected += (sender, port) => 
    Console.WriteLine($"❌ Device disconnected from {port}");

// Start automatic device scanning
manager.StartPeriodicDeviceSearch(intervalMs: 5000);

// Manual device discovery
var devices = await manager.FindBusyTagDevice();
if (devices?.Any() == true)
{
    Console.WriteLine($"🎯 Found devices: {string.Join(", ", devices)}");
    
    // Connect to first device
    var device = new BusyTagDevice(devices.First());
    await device.Connect();
    
    if (device.IsConnected)
    {
        Console.WriteLine($"🔗 Connected to {device.DeviceName}");
        Console.WriteLine($"📱 Firmware: {device.FirmwareVersion}");
        Console.WriteLine($"💾 Free space: {device.FreeStorageSize:N0} bytes");
    }
}
```

## 💡 LED Control

### Solid Colors
```csharp
// Simple color names
await device.SetSolidColorAsync("red", brightness: 80);
await device.SetSolidColorAsync("blue", brightness: 100);
await device.SetSolidColorAsync("green", brightness: 60);
await device.SetSolidColorAsync("off"); // Turn off LEDs

// Custom RGB values
await device.SendRgbColorAsync(
    red: 255, green: 128, blue: 0, 
    ledBits: 127); // Orange color on all LEDs

// Get current LED state
device.ReceivedSolidColor += (sender, ledArgs) =>
    Console.WriteLine($"💡 LED Status: {ledArgs.Color} on bits {ledArgs.LedBits}");
await device.GetSolidColorAsync();
```

### Custom LED Patterns
```csharp
using BusyTag.Lib.Util;

// Create a custom flashing pattern
var customPattern = new List<PatternLine>
{
    new PatternLine(127, "FF0000", 10, 100), // Red flash
    new PatternLine(127, "000000", 10, 100), // Off
    new PatternLine(127, "0000FF", 10, 100), // Blue flash  
    new PatternLine(127, "000000", 10, 100)  // Off
};

// Send and play the pattern
await device.SetNewCustomPattern(
    list: customPattern, 
    playAfterSending: true, 
    playPatternNonStop: false);

// Control pattern playback
await device.PlayPatternAsync(allow: true, repeatCount: 5);
await device.PlayPatternAsync(allow: false, repeatCount: 0); // Stop
```

### Predefined Patterns
```csharp
// Use built-in police pattern
var policePattern = PatternListCommands.PatternList[
    PatternListCommands.PatternName.GetPolice1];
    
if (policePattern != null)
{
    await device.SetNewCustomPattern(
        policePattern.PatternLines, 
        playAfterSending: true, 
        playPatternNonStop: false);
}

// Available predefined patterns:
// - Police1, Police2
// - Color flashes (Red, Green, Blue, Yellow, Cyan, Magenta, White)
// - Running LEDs in various colors
// - Pulse effects with breathing animation
```

## 📁 File Management

### Upload Files with Progress Tracking
```csharp
// Subscribe to upload events
device.FileUploadProgress += (sender, args) =>
    Console.WriteLine($"📤 Uploading {args.FileName}: {args.ProgressLevel:F1}%");

device.FileUploadFinished += (sender, args) =>
{
    if (args.Success)
        Console.WriteLine($"✅ Upload completed: {args.FileName}");
    else
        Console.WriteLine($"❌ Upload failed: {args.ErrorMessage}");
};

// Upload a file
await device.SendNewFile(@"C:\path\to\image.png");
```

### File Operations
```csharp
// List files on device
device.FileListUpdated += (sender, files) =>
{
    Console.WriteLine("📋 Files on device:");
    foreach (var file in files)
    {
        var sizeStr = file.Size > 1024 * 1024 
            ? $"{file.Size / (1024.0 * 1024):F1} MB"
            : $"{file.Size / 1024.0:F1} KB";
        Console.WriteLine($"  📄 {file.Name} ({sizeStr})");
    }
};

await device.GetFileListAsync();

// Display image on device
await device.ShowPictureAsync("image.png");

// Download file from device  
var localPath = await device.GetFileAsync("image.png");
if (!string.IsNullOrEmpty(localPath))
    Console.WriteLine($"📥 Downloaded to: {localPath}");

// Delete file
await device.DeleteFile("old_image.png");
```

## ⚙️ Device Configuration

### Display and System Settings
```csharp
// Control display brightness (0-100)
await device.SetDisplayBrightnessAsync(brightness: 75);
var currentBrightness = await device.GetDisplayBrightnessAsync();
Console.WriteLine($"🔆 Current brightness: {currentBrightness}%");

// Get device information
var deviceName = await device.GetDeviceNameAsync();
var manufacturer = await device.GetManufactureNameAsync();
var deviceId = await device.GetDeviceIdAsync();
var firmwareVersion = await device.GetFirmwareVersionAsync();

Console.WriteLine($"📱 Device: {deviceName} by {manufacturer}");
Console.WriteLine($"🆔 ID: {deviceId}");
Console.WriteLine($"📱 Firmware: {firmwareVersion}");
```

### Storage Management
```csharp
// Check storage space
var freeSpace = await device.GetFreeStorageSizeAsync();
var totalSpace = await device.GetTotalStorageSizeAsync();
var usedSpace = totalSpace - freeSpace;

Console.WriteLine($"💾 Storage: {usedSpace:N0} / {totalSpace:N0} bytes used");
Console.WriteLine($"📊 {(usedSpace * 100.0 / totalSpace):F1}% full");

// Format device storage (⚠️ Destructive operation!)
await device.FormatDiskAsync();

// Restart device
await device.RestartDeviceAsync();
```

## 📢 Event Handling

The library provides comprehensive event notifications for all operations:

```csharp
// Connection and device state
device.ConnectionStateChanged += (sender, connected) => 
    Console.WriteLine($"🔗 Connection: {(connected ? "Connected" : "Disconnected")}");

device.ReceivedDeviceBasicInformation += (sender, received) => 
    Console.WriteLine($"📱 Device info updated");

// File operations
device.FileListUpdated += (sender, files) => 
    Console.WriteLine($"📋 File list updated: {files.Count} files");

device.FileUploadProgress += (sender, progress) => 
    UpdateProgressBar(progress.ProgressLevel);

device.FileUploadFinished += (sender, result) => 
    HandleUploadResult(result);

// LED and pattern events
device.ReceivedSolidColor += (sender, ledArgs) => 
    Console.WriteLine($"💡 LED color: {ledArgs.Color}");

device.PlayPatternStatus += (sender, isPlaying) => 
    Console.WriteLine($"🎭 Pattern: {(isPlaying ? "Playing" : "Stopped")}");

// System events
device.FirmwareUpdateStatus += (sender, progress) => 
    Console.WriteLine($"🔄 Firmware update: {progress}%");

device.WritingInStorage += (sender, isWriting) => 
    Console.WriteLine($"💾 Storage operation: {(isWriting ? "Active" : "Idle")}");
```

## 🖥️ Platform Support

### Windows
- **Device Discovery**: WMI-based VID/PID detection (303A:81DF)
- **Port Format**: `COM1`, `COM2`, `COM3`, etc.
- **Requirements**: `System.Management` package
- **Permissions**: Standard user permissions sufficient

### macOS
- **Device Discovery**: `system_profiler` USB enumeration
- **Port Format**: `/dev/cu.usbmodem-xxx` or `/dev/tty.usbmodem-xxx`
- **Requirements**: Xcode command line tools
- **Permissions**: May require accessibility permissions for serial access

### Linux
- **Device Discovery**: `lsusb` command-line tool
- **Port Format**: `/dev/ttyUSB0`, `/dev/ttyACM0`, etc.
- **Requirements**: `usbutils` package
- **Permissions**: User must be in `dialout` group: `sudo usermod -a -G dialout $USER`

## 🔧 Advanced Usage

### Custom Device Configuration
```csharp
using BusyTag.Lib.Util;

// Create a complete device configuration
var config = new DeviceConfig
{
    Version = 1,
    Image = "startup_logo.png",
    ShowAfterDrop = true,
    AllowUsbMsc = true,
    AllowFileServer = false,
    DispBrightness = 80,
    solidColor = new DeviceConfig.SolidColor(127, "1291AF"),
    ActivatePattern = true,
    PatternRepeat = 3,
    CustomPatternArr = customPattern
};

// Apply configuration (implementation depends on your BusyTagDevice class)
```

### Error Handling Best Practices
```csharp
try
{
    await device.Connect();
    
    if (!device.IsConnected)
    {
        Console.WriteLine("❌ Failed to establish device connection");
        return;
    }
    
    // Perform device operations with timeout
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await device.SetSolidColorAsync("blue", brightness: 100);
    
    Console.WriteLine("✅ Operation completed successfully");
}
catch (InvalidOperationException ex)
{
    Console.WriteLine($"⚠️ Device operation failed: {ex.Message}");
}
catch (TimeoutException ex)
{
    Console.WriteLine($"⏱️ Operation timed out: {ex.Message}");
}
catch (Exception ex)
{
    Console.WriteLine($"💥 Unexpected error: {ex.Message}");
}
finally
{
    device.Disconnect();
    Console.WriteLine("🔌 Device disconnected");
}
```

## 🎨 Available LED Patterns

| Pattern Type | Variants | Description |
|--------------|----------|-------------|
| **Police** | Police1, Police2 | Emergency vehicle lighting patterns |
| **Color Flashes** | Red, Green, Blue, Yellow, Cyan, Magenta, White | Simple on/off flashing in various colors |
| **Running LEDs** | All colors | Moving light effect across LED strip |
| **Running with Off** | All colors | Running light with trailing off sections |
| **Pulse Effects** | All colors | Breathing/pulsing animation |

Access patterns via:
```csharp
// By enum
var pattern = PatternListCommands.PatternList[PatternListCommands.PatternName.GetPolice1];

// By name
var pattern = PatternListCommands.PatternListByName("Police 1");
```

## 📋 Requirements

- **.NET Runtime**: 8.0 or 9.0
- **Permissions**: Serial port access
- **Hardware**: BusyTag device with compatible firmware
- **Operating System**: Windows 10+, macOS 10.15+, or Linux with kernel 4.0+

## 📚 Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `System.IO.Ports` | 9.0.7 | Serial communication |
| `System.Text.Json` | 9.0.7 | JSON serialization |
| `System.Management` | 8.0.0 | Windows device discovery |

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

Please ensure your code follows our coding standards and includes appropriate tests.

## 📞 Support & Community

- **🐛 Bug Reports**: [GitHub Issues](https://github.com/Greynut-Development/busytag-lib/issues)
- **📖 Documentation**: [Project Wiki](https://github.com/Greynut-Development/busytag-lib/wiki)
- **💬 Discussions**: [GitHub Discussions](https://github.com/Greynut-Development/busytag-lib/discussions)
- **📧 Email Support**: [Contact BUSY TAG SIA](mailto:support@busytag.com)

## 🔄 Changelog

### v0.2.2 (Latest)
- ✨ Enhanced device discovery reliability
- 🐛 Fixed memory leaks in long-running applications
- 📱 Improved macOS serial port detection
- 🔧 Better error handling for connection failures

### v0.2.1
- 🚀 Initial public release
- 🔍 Cross-platform device discovery
- 📁 Complete file management system
- 💡 LED color and pattern control
- 📢 Comprehensive event system
- 📚 Predefined pattern library

---

<div align="center">

**Made with ❤️ by [BUSY TAG SIA](https://www.busytag.com)**

*Empowering developers to create amazing IoT experiences*

</div>