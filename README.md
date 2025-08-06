# BusyTag.Lib

[![NuGet Version](https://img.shields.io/nuget/v/BusyTag.Lib.svg)](https://www.nuget.org/packages/BusyTag.Lib/)
[![Downloads](https://img.shields.io/nuget/dt/BusyTag.Lib.svg)](https://www.nuget.org/packages/BusyTag.Lib/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0-blue)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20macOS-lightgrey)](https://github.com/busy-tag/busytag-lib)

A powerful and intuitive .NET library for seamless BusyTag device management via serial communication. Control LED patterns, manage files, and configure devices across Windows and macOS platforms with ease.

## 🚀 Key Features

- **🔍 Cross-platform device discovery** - Automatic detection on Windows and macOS
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
Install-Package BusyTag.Lib -Version 0.5.2
```

### .NET CLI
```bash
dotnet add package BusyTag.Lib --version 0.5.2
```

### PackageReference
```xml
<PackageReference Include="BusyTag.Lib" Version="0.5.2" />
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

### Linux (Coming Soon)
- **Status**: Support temporarily disabled - not fully tested
- **Note**: Linux support is planned for a future release
- **Experimental**: Can be enabled via `EnableExperimentalLinuxSupport` flag (not recommended for production)

<!-- When Linux support is fully enabled:
- **Device Discovery**: `lsusb` command-line tool + AT command validation
- **Port Format**: `/dev/ttyUSB0`, `/dev/ttyACM0`, etc.
- **Requirements**: `usbutils` package
- **Permissions**: User must be in `dialout` group: `sudo usermod -a -G dialout $USER`
-->

## 🏃‍♂️ Quick Start

### Basic Usage

```csharp
using BusyTag.Lib;

// Create and configure the manager
using var manager = new BusyTagManager();

// Optional: Enable experimental Linux support (not recommended)
// manager.EnableExperimentalLinuxSupport = true;

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
    await device.Connect();
    
    if (device.IsConnected)
    {
        Console.WriteLine($"🔗 Connected to {device.DeviceName}");
        Console.WriteLine($"📱 Firmware: {device.FirmwareVersion}");
        Console.WriteLine($"💾 Free space: {device.FreeStorageSize:N0} bytes");
    }
}
```

[Rest of README content remains the same...]

## 📋 System Requirements

- **.NET Runtime**: 8.0 or 9.0
- **Operating Systems**:
  - Windows 10 version 1903 or later
  - macOS 10.15 (Catalina) or later
  - Linux support coming soon (experimental flag available)
- **Hardware**: BusyTag device with compatible firmware (v0.7+)
- **Permissions**: Serial port access rights
- **Dependencies**: See table below

## 🔄 Changelog

### v0.5.2 (Current)
- 🔧 Linux support temporarily disabled pending further testing
- ✨ Added `EnableExperimentalLinuxSupport` flag for future Linux testing
- 📚 Improved platform detection and stability for Windows and macOS
- 📝 Updated documentation to reflect current platform support

### v0.5.0
- ✨ Enhanced SendCommandAsync implementation
- 📡 Better serial communication stability
- 🔧 Improved response handling for AT commands
- 🚀 Performance optimizations for file transfers

### v0.4.0
- 🚀 Added support for .NET 9.0
- 📦 Updated dependencies to latest versions (System.IO.Ports 9.0.7, System.Text.Json 9.0.7)
- 🔧 Improved cross-platform compatibility

### v0.3.0
- 🔧 Project configuration updates
- 📱 Better support for Mac Catalyst builds
- 🏗️ Improved build system for multi-platform targets

### v0.2.3
- 📚 Updated documentation and examples
- 🔍 Added more detailed usage examples
- 📋 Improved API documentation

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