# BusyTag.Lib

[![NuGet Version](https://img.shields.io/nuget/v/BusyTag.Lib.svg)](https://www.nuget.org/packages/BusyTag.Lib/)
[![Downloads](https://img.shields.io/nuget/dt/BusyTag.Lib.svg)](https://www.nuget.org/packages/BusyTag.Lib/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0-blue)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey)](https://github.com/busy-tag/busytag-lib)
[![Build and Test](https://github.com/busy-tag/busytag-lib/actions/workflows/build-test.yml/badge.svg)](https://github.com/busy-tag/busytag-lib/actions/workflows/build-test.yml)

A powerful and intuitive .NET library for seamless BusyTag device management via USB serial and cloud communication. Control LED patterns, manage files, configure devices, and connect to the BusyTag cloud across Windows, macOS, and Linux platforms.

## 🚀 Key Features

- **🔍 Cross-platform device discovery** - Automatic detection on Windows, macOS, and Linux (experimental)
- **📡 Robust serial communication** - 1.5 Mbps connection with semaphore-based buffer handling and automatic reconnection
- **📁 Complete file management** - Upload, download, and delete files with chunked transfers and real-time progress tracking
- **💡 Advanced LED control** - Solid colors, custom patterns, and predefined animations
- **⚙️ Device configuration** - Brightness, storage, Wi-Fi scanning, and system settings management
- **📢 Real-time notifications** - Comprehensive event system for all device operations
- **☁️ Cloud connectivity** - WebSocket and cloud client for remote device control, heartbeat, and event tracking
- **📶 Wi-Fi management** - Wi-Fi network scanning with structured results and retry logic
- **🔧 ESP32-S3 firmware recovery** - Built-in esptool integration with embedded firmware for device recovery
- **🖥️ Mac Catalyst USB driver** - Native IOKit-based USB transport for macOS applications
- **🔄 Bootloader detection** - Automatic detection of devices in boot/recovery mode
- **🎨 Rich pattern library** - 30+ predefined LED patterns including police, running lights, and pulses

## 📦 Installation

### Package Manager Console
```powershell
Install-Package BusyTag.Lib -Version 0.6.2
```

### .NET CLI
```bash
dotnet add package BusyTag.Lib --version 0.6.2
```

### PackageReference
```xml
<PackageReference Include="BusyTag.Lib" Version="0.6.2" />
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

### Linux (Experimental)
- **Device Discovery**: `lsusb` command-line tool + AT command validation
- **Port Format**: `/dev/ttyUSB0`, `/dev/ttyACM0`, etc.
- **Requirements**: `usbutils` package
- **Permissions**: User must be in `dialout` group: `sudo usermod -a -G dialout $USER`
- **Status**: Experimental - enable via `EnableExperimentalLinuxSupport = true` on `BusyTagManager`

## 🏃‍♂️ Quick Start

### Basic Usage

```csharp
using BusyTag.Lib;

// Create and configure the manager
using var manager = new BusyTagManager();

// Optional: Enable experimental Linux support
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
  - Linux (experimental, enable via `EnableExperimentalLinuxSupport`)
- **Hardware**: BusyTag device with compatible firmware (v0.7+)
- **Permissions**: Serial port access rights
- **Dependencies**: See table below

## 🔄 Changelog

### v0.6.2 (Current)
- 🔧 **Native macOS USB driver packaging fix** - Pack `libBusyTagUSBDriver.dylib` as `runtimes/osx/native/` runtime asset for proper DllImport resolution in dotnet tool installs and self-contained publish scenarios
- 📦 Added `.targets` copy step for native USB driver during build and publish

### v0.6.1
- 🔧 **NuGet packaging fix** - Fixed esptool binaries not being copied to output when consumed as a NuGet package
- 📦 Added `.targets` file and transitive build support for proper esptool binary deployment

### v0.6.0
- ☁️ **Cloud connectivity** - New `BusyTagCloudClient` for remote device control, heartbeat, event tracking, and pending command handling
- 📡 **WebSocket support** - New `BusyTagWebSocketClient` for real-time device status monitoring and LED status tracking
- 📶 **Wi-Fi scanning** - New `BusyTagHttpClient` with async Wi-Fi network scanning, retry logic, and local server token management
- 🔧 **ESP32-S3 firmware recovery** - New `EspToolRunner` and `FirmwarePackage` for firmware flashing with bundled esptool executables and embedded firmware
- 🖥️ **Mac Catalyst USB driver** - Native IOKit-based `IOKitUsbTransport` for Mac Catalyst applications with `BusyTagUSBDriver.framework`
- 🌐 Expanded macOS platform detection to include Darwin identifiers
- 🔧 Refactored USB communication logic and build system for improved macOS compatibility
- 🏗️ Added native macOS `libBusyTagUSBDriver.dylib` for direct USB communication
- 🔍 **Bootloader detection** - New `BusyTagDeviceInfo` class with detailed device scanning and boot mode detection in `BusyTagManager`
- 🚀 **1.5 Mbps serial speed** - Increased serial port speed for faster file transfers with optimized chunking and throttled progress updates
- 🛡️ **Storage integrity validation** - Cache cleanup logic, storage auto-delete functionality, and reflection-based WMI fallback detection
- 📡 **Improved SendCommandAsync** - Semaphore-based buffer handling for better reliability and optimized storage drive detection
- 🐛 **File upload improvements** - Better response handling with `WaitForResponseAsync`, improved `+FSS:` response parsing, and file deletion error handling
- 📱 **iOS platform detection** - Prevents USB discovery errors on iOS
- 🏷️ **Rebranded** to "SIA BUSY TAG" across identifiers, metadata, and documentation
- 📦 Updated dependencies to v9.0.14 (System.IO.Ports, System.Text.Json, System.Management)
- ✨ Added `FirmwareUpdateError` event, `CurrentUploadFileName` property, and `IsWritingToStorage` flag
- 🔧 Refactored `SetStorageAutoDeleteAsync` to `SetShowAfterDropAsync`
- ✨ Code cleanup and streamlined event handlers in `BusyTagDevice`

### v0.5.4
- 🔧 Minor fixes after retesting on Windows
- 📚 Updated documentation and version synchronization

### v0.5.3
- 🔧 Library updates and improvements
- 🐛 Fixed issue for device finding on newest macOS version
- 📚 Enhanced macOS compatibility and device detection
- ✨ Performance improvements and stability enhancements

### v0.5.2
- 🔧 Linux support added as experimental via `EnableExperimentalLinuxSupport` flag
- 📚 Improved platform detection and stability for Windows and macOS

### v0.5.0
- ✨ Enhanced SendCommandAsync implementation
- 📡 Better serial communication stability
- 🔧 Improved response handling for AT commands
- 🚀 Performance optimizations for file transfers

### v0.4.0
- 🚀 Added support for .NET 9.0
- 📦 Updated dependencies to latest versions
- 🔧 Improved cross-platform compatibility

### v0.3.0
- 🔧 Project configuration updates
- 📱 Better support for Mac Catalyst builds

### v0.2.3
- 📚 Updated documentation and examples

### v0.2.2
- ✨ Enhanced device discovery reliability
- 🐛 Fixed memory leaks in long-running applications
- 📱 Improved macOS serial port detection

### v0.2.1
- 🚀 Initial public release
- 🔍 Cross-platform device discovery
- 📁 Complete file management system
- 💡 LED color and pattern control
- 📢 Comprehensive event system
- 📚 Predefined pattern library

---

<div align="center">

**Made with ❤️ by [SIA BUSY TAG](https://www.busy-tag.com)**

*Empowering developers to create amazing IoT experiences*

[![GitHub Stars](https://img.shields.io/github/stars/busy-tag/busytag-lib?style=social)](https://github.com/busy-tag/busytag-lib)
[![Follow on Twitter](https://img.shields.io/twitter/follow/busytag?style=social)](https://twitter.com/busytag)

</div>