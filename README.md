# BusyTag.Lib

[![NuGet Version](https://img.shields.io/nuget/v/BusyTag.Lib.svg)](https://www.nuget.org/packages/BusyTag.Lib/)
[![Downloads](https://img.shields.io/nuget/dt/BusyTag.Lib.svg)](https://www.nuget.org/packages/BusyTag.Lib/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0-blue)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey)](https://github.com/busy-tag/busytag-lib)

A powerful and intuitive .NET library for seamless BusyTag device management via serial communication. Control LED patterns, manage files, and configure devices across Windows, macOS, and Linux platforms with ease.

## 🚀 Key Features

- **🔍 Cross-platform device discovery** - Automatic detection on Windows, macOS, and Linux (not fully tested)
- **📡 Robust serial communication** - Reliable connection management with automatic reconnection
- **📁 Complete file management** - Upload, download, and delete files with real-time progress tracking
- **💡 Advanced LED control** - Solid colors, custom patterns, and predefined animations
- **⚙️ Device configuration** - Brightness, storage, Wi-Fi, and system settings management
- **📢 Real-time notifications** - Comprehensive event system for all device operations
- **🔄 Firmware update support** - Built-in firmware update capabilities with progress tracking
- **🎨 Rich pattern library** - 30+ predefined LED patterns including police, running lights, and pulses

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
<PackageReference Include="BusyTag.Lib" Version="0.3.0" />
```

## 🏃‍♂️ Quick Start

### 1. Device Discovery and Connection

```csharp
using BusyTag.Lib;

// Create and configure the manager
using var manager = new BusyTagManager();

// Subscribe to device events
manager.DeviceConnected += (sender, port) => 
    Console.WriteLine($"✅ Device connected on {port}");
manager.DeviceDisconnected += (sender, port) => 
    Console.WriteLine($"❌ Device disconnected from {port}");

// Start automatic device scanning every 5 seconds
manager.StartPeriodicDeviceSearch(intervalMs: 5000);

// Manual device discovery
var devices = await manager.FindBusyTagDevice();
if (devices?.Any() == true)
{
    Console.WriteLine($"🎯 Found devices: {string.Join(", ", devices)}");
    
    // Connect to first device
    var device = new BusyTagDevice(devices.First());
    
    // Subscribe to connection events
    device.ConnectionStateChanged += (sender, connected) => 
        Console.WriteLine($"🔗 Connection: {(connected ? "Connected" : "Disconnected")}");
    
    await device.Connect();
    
    if (device.IsConnected)
    {
        Console.WriteLine($"🔗 Connected to {device.DeviceName}");
        Console.WriteLine($"📱 Firmware: {device.FirmwareVersion}");
        Console.WriteLine($"💾 Free space: {device.FreeStorageSize:N0} bytes");
    }
}
```

### 2. Basic LED Control

```csharp
// Simple color control
await device.SetSolidColorAsync("red", brightness: 80);
await device.SetSolidColorAsync("blue", brightness: 100);
await device.SetSolidColorAsync("off"); // Turn off LEDs

// Custom RGB values
await device.SendRgbColorAsync(red: 255, green: 128, blue: 0, ledBits: 127);
```

### 3. File Management

```csharp
// Upload a file with progress tracking
device.FileUploadProgress += (sender, args) =>
    Console.WriteLine($"📤 Uploading {args.FileName}: {args.ProgressLevel:F1}%");

await device.SendNewFile(@"C:\path\to\image.png");

// Display image on device
await device.ShowPictureAsync("image.png");
```

## 💡 LED Control

### Solid Colors
```csharp
// Predefined colors with brightness control
await device.SetSolidColorAsync("red", brightness: 80);
await device.SetSolidColorAsync("blue", brightness: 100);
await device.SetSolidColorAsync("green", brightness: 60);
await device.SetSolidColorAsync("yellow", brightness: 90);
await device.SetSolidColorAsync("cyan", brightness: 70);
await device.SetSolidColorAsync("magenta", brightness: 85);
await device.SetSolidColorAsync("white", brightness: 50);
await device.SetSolidColorAsync("off"); // Turn off LEDs

// Custom RGB values with LED bit control
await device.SendRgbColorAsync(
    red: 255, green: 128, blue: 0, 
    ledBits: 127); // Orange color on all LEDs (bits 0-6)

// Control specific LED segments
await device.SendRgbColorAsync(
    red: 255, green: 0, blue: 0, 
    ledBits: 120); // Red on outer LEDs only

// Get current LED state
device.ReceivedSolidColor += (sender, ledArgs) =>
    Console.WriteLine($"💡 LED Status: #{ledArgs.Color} on bits {ledArgs.LedBits}");
await device.GetSolidColorAsync();
```

### Custom LED Patterns
```csharp
using BusyTag.Lib.Util;

// Create a custom emergency pattern
var emergencyPattern = new List<PatternLine>
{
    new PatternLine(127, "FF0000", 5, 100),  // Red flash - all LEDs, fast speed, 100ms delay
    new PatternLine(127, "000000", 5, 100),  // Off
    new PatternLine(127, "0000FF", 5, 100),  // Blue flash
    new PatternLine(127, "000000", 5, 100)   // Off
};

// Send and play the pattern
await device.SetNewCustomPattern(
    list: emergencyPattern, 
    playAfterSending: true, 
    playPatternNonStop: false);

// Control pattern playback
await device.PlayPatternAsync(allow: true, repeatCount: 5);
await device.PlayPatternAsync(allow: false, repeatCount: 0); // Stop pattern

// Monitor pattern status
device.PlayPatternStatus += (sender, isPlaying) =>
    Console.WriteLine($"🎭 Pattern: {(isPlaying ? "Playing" : "Stopped")}");
```

### Predefined Patterns
```csharp
using BusyTag.Lib.Util;

// Police pattern
var policePattern = PatternListCommands.PatternList[
    PatternListCommands.PatternName.GetPolice1];

if (policePattern != null)
{
    await device.SetNewCustomPattern(
        policePattern.PatternLines, 
        playAfterSending: true, 
        playPatternNonStop: true);
}

// Running lights
var runningRed = PatternListCommands.PatternList[
    PatternListCommands.PatternName.GetRedRunningLed];

// Pulse effects
var bluePulse = PatternListCommands.PatternList[
    PatternListCommands.PatternName.GetBluePulses];

// Get pattern by name
var pattern = PatternListCommands.PatternListByName("Police 2");
if (pattern != null)
{
    await device.SetNewCustomPattern(pattern.PatternLines, true, false);
}
```

## 📁 File Management

### File Upload with Progress Tracking
```csharp
// Subscribe to upload events for detailed feedback
device.FileUploadProgress += (sender, args) =>
{
    Console.WriteLine($"📤 Uploading {args.FileName}: {args.ProgressLevel:F1}%");
    
    // Update progress bar in your UI
    UpdateProgressBar(args.ProgressLevel);
};

device.FileUploadFinished += (sender, args) =>
{
    if (args.Success)
    {
        Console.WriteLine($"✅ Upload completed: {args.FileName}");
    }
    else
    {
        Console.WriteLine($"❌ Upload failed: {args.ErrorMessage}");
        Console.WriteLine($"🔍 Error type: {args.ErrorType}");
    }
};

// Upload files
await device.SendNewFile(@"C:\Images\logo.png");
await device.SendNewFile(@"C:\Images\animation.gif");

// Cancel ongoing upload if needed
device.CancelFileUpload(false, UploadErrorType.Cancelled, "User cancelled");
```

### File Operations
```csharp
// Monitor file list changes
device.FileListUpdated += (sender, files) =>
{
    Console.WriteLine($"📋 Files on device ({files.Count}):");
    foreach (var file in files)
    {
        var sizeStr = file.Size > 1024 * 1024 
            ? $"{file.Size / (1024.0 * 1024):F1} MB"
            : $"{file.Size / 1024.0:F1} KB";
        Console.WriteLine($"  📄 {file.Name} ({sizeStr})");
    }
};

// Refresh file list
await device.GetFileListAsync();

// Display image on device
await device.ShowPictureAsync("logo.png");

// Monitor current image changes
device.ReceivedShowingPicture += (sender, imageName) =>
    Console.WriteLine($"🖼️ Now showing: {imageName}");

// Download file from device to local cache
var localPath = await device.GetFileAsync("animation.gif");
if (!string.IsNullOrEmpty(localPath))
{
    Console.WriteLine($"📥 Downloaded to: {localPath}");
    
    // Check if file exists in cache
    if (device.FileExistsInCache("animation.gif"))
    {
        var cachedPath = device.GetFilePathFromCache("animation.gif");
        Console.WriteLine($"📂 Cached at: {cachedPath}");
    }
}

// Delete files
await device.DeleteFile("old_image.png");

// Check if file exists on device
if (device.FileExistsInDeviceStorage("test.png"))
{
    var fileInfo = device.GetFileInfo("test.png");
    Console.WriteLine($"📊 File info: {fileInfo?.Name} - {fileInfo?.Size} bytes");
}
```

## ⚙️ Device Configuration

### Display and System Settings
```csharp
// Control display brightness (0-100)
await device.SetDisplayBrightnessAsync(brightness: 75);

// Monitor brightness changes
device.ReceivedDisplayBrightness += (sender, brightness) =>
    Console.WriteLine($"🔆 Brightness set to: {brightness}%");

var currentBrightness = await device.GetDisplayBrightnessAsync();
Console.WriteLine($"🔆 Current brightness: {currentBrightness}%");

// Get comprehensive device information
await device.GetDeviceNameAsync();      // Updates device.DeviceName
await device.GetManufactureNameAsync(); // Updates device.ManufactureName  
await device.GetDeviceIdAsync();        // Updates device.Id
await device.GetFirmwareVersionAsync(); // Updates device.FirmwareVersion

Console.WriteLine($"📱 Device: {device.DeviceName} by {device.ManufactureName}");
Console.WriteLine($"🆔 ID: {device.Id}");
Console.WriteLine($"📱 Firmware: {device.FirmwareVersion} ({device.FirmwareVersionFloat})");
Console.WriteLine($"🌐 Local address: {device.LocalHostAddress}");
```

### Storage Management
```csharp
// Monitor storage operations
device.WritingInStorage += (sender, isWriting) =>
    Console.WriteLine($"💾 Storage: {(isWriting ? "Writing..." : "Idle")}");

// Check storage space
await device.GetFreeStorageSizeAsync();
await device.GetTotalStorageSizeAsync();

var freeSpace = device.FreeStorageSize;
var totalSpace = device.TotalStorageSize;
var usedSpace = totalSpace - freeSpace;

Console.WriteLine($"💾 Storage: {usedSpace:N0} / {totalSpace:N0} bytes used");
Console.WriteLine($"📊 {(usedSpace * 100.0 / totalSpace):F1}% full");

// Advanced storage operations
await device.SetUsbMassStorageActiveAsync(true);  // Enable USB mass storage
await device.SetAllowedAutoStorageScanAsync(false); // Disable auto scan
await device.ActivateFileStorageScanAsync(); // Manual storage scan

// ⚠️ Destructive operations
await device.FormatDiskAsync();  // Format device storage
await device.RestartDeviceAsync(); // Restart device
```

### WiFi Configuration (Firmware dependent)
```csharp
// Monitor WiFi configuration
device.ReceivedWifiConfig += (sender, config) =>
    Console.WriteLine($"📶 WiFi: {config.Ssid} (Password: {(string.IsNullOrEmpty(config.Password) ? "None" : "Set")})");

// Note: WiFi configuration methods depend on firmware version
// Check device.FirmwareVersionFloat for feature availability
```

## 📢 Comprehensive Event System

The library provides extensive event notifications for all operations:

```csharp
// Connection and device state
device.ConnectionStateChanged += (sender, connected) => 
    Console.WriteLine($"🔗 Connection: {(connected ? "Connected" : "Disconnected")}");

device.ReceivedDeviceBasicInformation += (sender, received) => 
{
    if (received)
    {
        Console.WriteLine($"📱 Device info received:");
        Console.WriteLine($"   Name: {device.DeviceName}");
        Console.WriteLine($"   Firmware: {device.FirmwareVersion}");
        Console.WriteLine($"   Storage: {device.FreeStorageSize:N0}/{device.TotalStorageSize:N0}");
    }
};

// File operations with detailed progress
device.FileListUpdated += (sender, files) => 
    Console.WriteLine($"📋 File list updated: {files.Count} files");

device.FileUploadProgress += (sender, progress) => 
{
    // Update progress bar or status
    var percentage = (int)progress.ProgressLevel;
    var progressBar = new string('█', percentage / 2) + new string('░', 50 - percentage / 2);
    Console.Write($"\r📤 {progress.FileName}: [{progressBar}] {percentage}%");
};

device.FileUploadFinished += (sender, result) => 
{
    Console.WriteLine(); // New line after progress
    if (result.Success)
    {
        Console.WriteLine($"✅ Upload successful: {result.FileName}");
    }
    else
    {
        Console.WriteLine($"❌ Upload failed: {result.FileName}");
        Console.WriteLine($"🔍 Reason: {result.ErrorMessage}");
        Console.WriteLine($"📋 Type: {result.ErrorType}");
    }
};

// LED and pattern events
device.ReceivedSolidColor += (sender, ledArgs) => 
    Console.WriteLine($"💡 LED color updated: #{ledArgs.Color} (LEDs: {Convert.ToString(ledArgs.LedBits, 2).PadLeft(8, '0')})");

device.PlayPatternStatus += (sender, isPlaying) => 
    Console.WriteLine($"🎭 Pattern playback: {(isPlaying ? "▶️ Playing" : "⏹️ Stopped")}");

// System events
device.FirmwareUpdateStatus += (sender, progress) => 
{
    Console.WriteLine($"🔄 Firmware update progress: {progress:F1}%");
    if (progress >= 100.0f)
        Console.WriteLine("✅ Firmware update completed!");
};

device.ReceivedDisplayBrightness += (sender, brightness) =>
    Console.WriteLine($"🔆 Display brightness: {brightness}%");

device.ReceivedShowingPicture += (sender, imageName) =>
    Console.WriteLine($"🖼️ Currently displaying: {imageName}");

device.WritingInStorage += (sender, isWriting) => 
{
    if (isWriting)
        Console.WriteLine("💾 Storage write operation started...");
    else
        Console.WriteLine("✅ Storage write operation completed");
};

device.ReceivedUsbMassStorageActive += (sender, isActive) =>
    Console.WriteLine($"🔌 USB Mass Storage: {(isActive ? "Enabled" : "Disabled")}");
```

## 🖥️ Platform Support

### Windows
- **Device Discovery**: WMI-based VID/PID detection (303A:81DF)
- **Port Format**: `COM1`, `COM2`, `COM3`, etc.
- **Requirements**: `System.Management` package
- **Permissions**: Standard user permissions sufficient
- **Tested on**: Windows 10/11

### macOS
- **Device Discovery**: `system_profiler` USB enumeration + AT command validation
- **Port Format**: `/dev/tty.usbmodem-xxx`
- **Requirements**: Xcode command line tools
- **Permissions**: May require accessibility permissions for serial access
- **Tested on**: macOS 10.15+ (Catalina and newer)

### Linux
- **Device Discovery**: `lsusb` command-line tool + AT command validation
- **Port Format**: `/dev/ttyUSB0`, `/dev/ttyACM0`, etc.
- **Requirements**: `usbutils` package
- **Permissions**: User must be in `dialout` group: `sudo usermod -a -G dialout $USER`
- **Not fully tested**

## 🎨 Available LED Patterns

| Category | Patterns | Description |
|----------|----------|-------------|
| **Emergency** | Police1, Police2 | Red/blue alternating patterns for emergency vehicles |
| **Color Flashes** | Red, Green, Blue, Yellow, Cyan, Magenta, White | Simple on/off flashing in various colors |
| **Running Lights** | All colors | Moving light effect across LED strip |
| **Pulse Effects** | All colors | Breathing/pulsing animation with fade in/out |

### Pattern Parameters
- **LedBits**: Controls which LEDs are active (0-255, binary mask)
- **Color**: 6-digit hex color code (e.g., "FF0000" for red)
- **Speed**: Animation speed (1-255, higher = faster)
- **Delay**: Delay between steps in milliseconds

### Pattern Access Examples
```csharp
// By enum
var pattern = PatternListCommands.PatternList[PatternListCommands.PatternName.GetPolice1];

// By name
var pattern = PatternListCommands.PatternListByName("Police 1");

// List all available patterns
foreach (var kvp in PatternListCommands.PatternList)
{
    Console.WriteLine($"🎨 {kvp.Value?.Name}: {kvp.Value?.PatternLines.Count} steps");
}
```

## 🔧 Advanced Usage

### Connection Management
```csharp
public class BusyTagService
{
    private BusyTagDevice? _device;
    private readonly Timer _healthCheckTimer;
    
    public BusyTagService()
    {
        // Setup periodic health check
        _healthCheckTimer = new Timer(CheckConnection, null, 
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }
    
    private async void CheckConnection(object? state)
    {
        if (_device?.IsConnected != true)
        {
            Console.WriteLine("🔄 Attempting to reconnect...");
            await ReconnectAsync();
        }
    }
    
    private async Task ReconnectAsync()
    {
        try
        {
            using var manager = new BusyTagManager();
            var devices = await manager.FindBusyTagDevice();
            
            if (devices?.Any() == true)
            {
                _device = new BusyTagDevice(devices.First());
                await _device.Connect();
                
                if (_device.IsConnected)
                {
                    Console.WriteLine("✅ Reconnected successfully");
                    await InitializeDevice();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Reconnection failed: {ex.Message}");
        }
    }
    
    private async Task InitializeDevice()
    {
        // Set default brightness
        await _device.SetDisplayBrightnessAsync(80);
        
        // Show welcome pattern
        var welcomePattern = PatternListCommands.PatternList[
            PatternListCommands.PatternName.GetGreenPulses];
        await _device.SetNewCustomPattern(welcomePattern.PatternLines, true, false);
    }
}
```

### Error Handling Best Practices
```csharp
public async Task<bool> SafeDeviceOperation(BusyTagDevice device)
{
    if (!device.IsConnected)
    {
        Console.WriteLine("❌ Device not connected");
        return false;
    }
    
    try
    {
        // Use timeout for all operations
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        // Check firmware compatibility
        if (device.FirmwareVersionFloat < 2.0)
        {
            Console.WriteLine("⚠️ Old firmware detected, enabling compatibility mode");
            await device.SetUsbMassStorageActiveAsync(true);
        }
        
        // Perform operations with proper error handling
        var success = await device.SetSolidColorAsync("blue", brightness: 100);
        if (!success)
        {
            Console.WriteLine("⚠️ LED control failed");
            return false;
        }
        
        // Wait for confirmation
        await Task.Delay(1000, cts.Token);
        
        Console.WriteLine("✅ Operation completed successfully");
        return true;
    }
    catch (InvalidOperationException ex)
    {
        Console.WriteLine($"⚠️ Device operation failed: {ex.Message}");
        
        // Attempt reconnection
        if (!device.IsConnected)
        {
            device.Disconnect();
        }
        
        return false;
    }
    catch (TimeoutException)
    {
        Console.WriteLine("⏱️ Operation timed out");
        return false;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"💥 Unexpected error: {ex.Message}");
        return false;
    }
}
```

### Firmware Version Handling
```csharp
public async Task HandleFirmwareVersions(BusyTagDevice device)
{
    var version = device.FirmwareVersionFloat;
    Console.WriteLine($"📱 Firmware version: {version}");
    
    if (version >= 2.0)
    {
        Console.WriteLine("✨ Using new serial-based file transfer");
        // Modern firmware features
        await device.SetAllowedAutoStorageScanAsync(false);
        // Serial-based file operations available
    }
    else
    {
        Console.WriteLine("⚠️ Legacy firmware - limited features");
        // Basic LED control only
    }
    
    // Version-specific features
    if (version > 0.8)
    {
        // Advanced pattern support
        var pattern = PatternListCommands.PatternList[
            PatternListCommands.PatternName.GetPolice1];
        await device.SetNewCustomPattern(pattern.PatternLines, true, false);
    }
    
    if (version > 0.7)
    {
        // Auto storage scan control
        await device.SetAllowedAutoStorageScanAsync(false);
    }
}
```

## 📋 System Requirements

- **.NET Runtime**: 8.0 or 9.0
- **Operating Systems**:
    - Windows 10 version 1903 or later
    - macOS 10.15 (Catalina) or later
    - Linux with kernel 4.0 or later
- **Hardware**: BusyTag device with compatible firmware (v0.7+)
- **Permissions**: Serial port access rights
- **Dependencies**: See table below

## 📚 Dependencies

| Package | Version | Purpose | Platform |
|---------|---------|---------|----------|
| `System.IO.Ports` | 9.0.7 | Serial communication | All |
| `System.Text.Json` | 9.0.7 | JSON serialization | All |
| `System.Management` | 8.0.0 | Windows device discovery | Windows only |

## 🔍 Troubleshooting

### Common Issues

**Device not found**
```csharp
// Enable verbose logging
manager.EnableVerboseLogging = true;

// Check all serial ports
var allPorts = manager.AllSerialPorts();
Console.WriteLine($"Available ports: {string.Join(", ", allPorts)}");

// Manual port testing
foreach (var port in allPorts)
{
    try
    {
        var testDevice = new BusyTagDevice(port);
        await testDevice.Connect();
        if (testDevice.IsConnected)
        {
            Console.WriteLine($"✅ BusyTag found on {port}");
            testDevice.Disconnect();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Port {port} failed: {ex.Message}");
    }
}
```

**Connection timeout**
```csharp
// Increase timeout and add retry logic
for (int attempt = 1; attempt <= 3; attempt++)
{
    try
    {
        Console.WriteLine($"Connection attempt {attempt}/3");
        await device.Connect();
        
        if (device.IsConnected)
        {
            Console.WriteLine("✅ Connected successfully");
            break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Attempt {attempt} failed: {ex.Message}");
        if (attempt < 3)
        {
            await Task.Delay(2000); // Wait before retry
        }
    }
}
```

**File upload failures**
```csharp
device.FileUploadFinished += (sender, result) =>
{
    if (!result.Success)
    {
        switch (result.ErrorType)
        {
            case UploadErrorType.FilenameToolong:
                Console.WriteLine("💡 Try renaming file to be shorter");
                break;
            case UploadErrorType.InsufficientStorage:
                Console.WriteLine("💡 Delete some files or use device.FreeUpStorage()");
                break;
            case UploadErrorType.ConnectionLost:
                Console.WriteLine("💡 Check USB cable and try reconnecting");
                break;
            default:
                Console.WriteLine($"💡 Error: {result.ErrorMessage}");
                break;
        }
    }
};
```

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

### Development Setup
```bash
# Clone the repository
git clone https://github.com/busy-tag/busytag-lib.git

# Restore packages
dotnet restore

# Build the project
dotnet build

# Run tests
dotnet test
```

## 📞 Support & Community

- **🐛 Bug Reports**: [GitHub Issues](https://github.com/busy-tag/busytag-lib/issues)
- **💬 Discussions**: [GitHub Discussions](https://github.com/busy-tag/busytag-lib/discussions)
- **📧 Email Support**: [support@busy-tag.com](mailto:support@busy-tag.com)
- **🌐 Website**: [www.busy-tag.com](https://www.busy-tag.com)

## 🔄 Changelog

### v0.3.0 (Current)
- 🔧 Little project configuration update

### v0.2.3
- 📚 Updated documentation and examples

### v0.2.2
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

**Made with ❤️ by [BUSY TAG SIA](https://www.busy-tag.com)**

*Empowering developers to create amazing IoT experiences*

[![GitHub Stars](https://img.shields.io/github/stars/busy-tag/busytag-lib?style=social)](https://github.com/busy-tag/busytag-lib)
[![Follow on Twitter](https://img.shields.io/twitter/follow/busytag?style=social)](https://twitter.com/busytag)

</div>