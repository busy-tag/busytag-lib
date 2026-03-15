using System.Diagnostics;
using System.IO.Ports;
using System.Reflection;
using System.Runtime.InteropServices;
using BusyTag.Lib.Util;

#if WINDOWS || WINDOWS10_0_19041_0_OR_GREATER
using System.Management;
#endif

namespace BusyTag.Lib;

public class BusyTagManager : IDisposable
{
    // Normal operating mode: TinyUSB CDC with custom PID
    private const string NormalModeVid = "303A";
    private const string NormalModePid = "81DF";

    // ESP32-S3 native USB boot/download mode
    private const string BootModeVid = "303A";
    private const string BootModePid = "1001";

    public event EventHandler<List<string>?>? FoundBusyTagSerialDevices;
    public event EventHandler<List<BusyTagDeviceInfo>>? FoundBusyTagDevicesDetailed;
    public event EventHandler<string>? DeviceConnected;
    public event EventHandler<string>? DeviceDisconnected;

    private List<string>? _busyTagSerialDevices = new();
    private List<BusyTagDeviceInfo> _busyTagDevicesDetailed = new();
    private List<string>? _previousBusyTagSerialDevices = new();
    private static bool _isScanningForDevices = false;
    private Timer? _periodicSearchTimer;
    private bool _isPeriodicSearchEnabled = false;
    private readonly object _lockObject = new object();
    private bool _disposed = false;

    // Add this property to control logging verbosity
    public bool EnableVerboseLogging { get; set; } = false;
    
    // Add this property to control Linux support (disabled by default)
    public bool EnableExperimentalLinuxSupport { get; set; } = false;

    public string[] AllSerialPorts()
    {
        return SerialPort.GetPortNames();
    }

    public void StartPeriodicDeviceSearch(int intervalMs = 5000)
    {
        lock (_lockObject)
        {
            if (_isPeriodicSearchEnabled)
                return;

            _isPeriodicSearchEnabled = true;
            _periodicSearchTimer = new Timer(PeriodicSearchCallback, null, 0, intervalMs);
        }
    }

    public void StopPeriodicDeviceSearch()
    {
        lock (_lockObject)
        {
            _isPeriodicSearchEnabled = false;
            _periodicSearchTimer?.Dispose();
            _periodicSearchTimer = null;
        }
    }

    private async void PeriodicSearchCallback(object? state)
    {
        try
        {
            if (!_isPeriodicSearchEnabled)
                return;

            _ = await FindBusyTagDevice();
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
        }
    }

    /// <summary>
    /// Resets the scanning flag. Use when the flag gets stuck due to async race conditions.
    /// </summary>
    public void ResetScanState()
    {
        _isScanningForDevices = false;
    }

    public async Task<List<string>?> FindBusyTagDevice()
    {
        if (_isScanningForDevices) return null;
        _isScanningForDevices = true;

        // Use runtime platform detection instead of compile-time
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return await DiscoverByVidPidWindowsAsync();
        }
        else if (IsRunningOnMacOS())
        {
            return await DiscoverByVidPidMacOsAsync();
        }
        else if (IsRunningOnIOS())
        {
            // iOS doesn't support USB device discovery - devices connect via Bluetooth or network
            _isScanningForDevices = false;
            return null;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && EnableExperimentalLinuxSupport)
        {
            if (EnableVerboseLogging)
                Debug.WriteLine("[WARNING] Linux support is experimental and not fully tested");
            return await DiscoverByVidPidLinuxAsync();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (EnableVerboseLogging)
                Debug.WriteLine("[INFO] Linux platform detected but support is disabled. Set EnableExperimentalLinuxSupport = true to enable experimental support.");
        }

        _isScanningForDevices = false;
        return null;
    }

    /// <summary>
    /// Returns detailed device info including boot-mode devices.
    /// Call after FindBusyTagDevice() or use independently.
    /// </summary>
    public List<BusyTagDeviceInfo> GetLastDetailedDevices() => _busyTagDevicesDetailed;

    private static bool IsRunningOnIOS()
    {
        // Use compile-time check for iOS target framework
#if IOS || __IOS__
        return true;
#else
        // Runtime fallback for non-compile-time detection
        // Check for iOS-specific indicators
        var osDesc = RuntimeInformation.OSDescription;

        if (osDesc.Contains("Darwin"))
        {
            try
            {
                // On iOS, /var/mobile exists; on macOS it doesn't
                if (Directory.Exists("/var/mobile"))
                    return true;

                // Check for iOS simulator or device paths
                var processPath = Environment.ProcessPath ?? "";
                if (processPath.Contains("/CoreSimulator/") ||
                    processPath.Contains("/iPhone") ||
                    processPath.Contains("/iPad"))
                    return true;
            }
            catch
            {
                // If we can't check paths, we're likely sandboxed (iOS)
            }
        }

        return false;
#endif
    }

    private static bool IsRunningOnMacOS()
    {
        // Use compile-time check - MacCatalyst is macOS, not iOS
#if MACCATALYST || __MACCATALYST__
        return true;
#elif IOS || __IOS__
        return false;
#else
        // Runtime fallback
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
            !RuntimeInformation.OSDescription.Contains("Darwin"))
            return false;

        // If iOS is detected, return false
        if (IsRunningOnIOS())
            return false;

        return true;
#endif
    }

    private void CheckForDeviceChanges()
    {
        lock (_lockObject)
        {
            if (_busyTagSerialDevices == null || _previousBusyTagSerialDevices == null)
            {
                _previousBusyTagSerialDevices = _busyTagSerialDevices?.ToList() ?? new List<string>();
                return;
            }

            // Check for newly connected devices
            var newDevices = _busyTagSerialDevices.Except(_previousBusyTagSerialDevices).ToList();
            foreach (var device in newDevices)
            {
                DeviceConnected?.Invoke(this, device);
            }

            // Check for disconnected devices
            var disconnectedDevices = _previousBusyTagSerialDevices.Except(_busyTagSerialDevices).ToList();
            foreach (var device in disconnectedDevices)
            {
                DeviceDisconnected?.Invoke(this, device);
            }

            _previousBusyTagSerialDevices = _busyTagSerialDevices.ToList();
        }
    }
    
    #region Windows VID/PID Discovery

    private async Task<List<string>?> DiscoverByVidPidWindowsAsync()
    {
        try
        {
            return await Task.Run(FindPortByVidPidWindows);
        }
        catch (Exception ex)
        {
            if (EnableVerboseLogging)
                Debug.WriteLine($"[DEBUG] Windows VID/PID discovery failed: {ex.Message}");
            _isScanningForDevices = false;
            return null;
        }
    }
    
    private List<string>? FindPortByVidPidWindows()
    {
        var normalDeviceId = $"VID_{NormalModeVid}&PID_{NormalModePid}";
        var bootDeviceId = $"VID_{BootModeVid}&PID_{BootModePid}";
        var foundDevices = new List<string>();
        var detailedDevices = new List<BusyTagDeviceInfo>();

        try
        {
#if WINDOWS || WINDOWS10_0_19041_0_OR_GREATER
            // Query WMI for USB devices using direct reference
            const string query = "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE '%USB%'";

            using (var searcher = new ManagementObjectSearcher(query))
            {
                foreach (ManagementObject device in searcher.Get())
                {
                    var deviceIdStr = device["DeviceID"]?.ToString();
                    if (deviceIdStr == null) continue;

                    BusyTagDeviceMode? mode = null;
                    if (deviceIdStr.Contains(normalDeviceId, StringComparison.OrdinalIgnoreCase))
                        mode = BusyTagDeviceMode.Normal;
                    else if (deviceIdStr.Contains(bootDeviceId, StringComparison.OrdinalIgnoreCase))
                        mode = BusyTagDeviceMode.BootLoader;

                    if (mode == null) continue;

                    // Check if it's a COM port
                    var name = device["Name"]?.ToString();
                    if (name != null && name.Contains("COM"))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(name, @"COM(\d+)");
                        if (match.Success)
                        {
                            var port = $"COM{match.Groups[1].Value}";
                            var vid = mode == BusyTagDeviceMode.Normal ? NormalModeVid : BootModeVid;
                            var pid = mode == BusyTagDeviceMode.Normal ? NormalModePid : BootModePid;

                            detailedDevices.Add(new BusyTagDeviceInfo
                            {
                                PortName = port, Mode = mode.Value, Vid = vid, Pid = pid
                            });

                            // Only add normal-mode devices to the backward-compatible list
                            if (mode == BusyTagDeviceMode.Normal)
                                foundDevices.Add(port);
                        }
                    }
                }
            }
#else
            // Use runtime reflection to access System.Management when not available at compile-time
            var normalPorts = FindPortByVidPidWindowsViaReflection(normalDeviceId);
            var bootPorts = FindPortByVidPidWindowsViaReflection(bootDeviceId);

            if (EnableVerboseLogging)
            {
                Debug.WriteLine($"[DEBUG] Reflection WMI: normal={normalPorts.Count}, boot={bootPorts.Count}");
                Console.WriteLine($"  [DEBUG] WMI scan: normal={normalPorts.Count}, boot={bootPorts.Count}");
            }

            foreach (var port in normalPorts)
            {
                foundDevices.Add(port);
                detailedDevices.Add(new BusyTagDeviceInfo
                {
                    PortName = port, Mode = BusyTagDeviceMode.Normal, Vid = NormalModeVid, Pid = NormalModePid
                });
            }
            foreach (var port in bootPorts)
            {
                detailedDevices.Add(new BusyTagDeviceInfo
                {
                    PortName = port, Mode = BusyTagDeviceMode.BootLoader, Vid = BootModeVid, Pid = BootModePid
                });
            }
#endif

            lock (_lockObject)
            {
                _busyTagSerialDevices = foundDevices;
                _busyTagDevicesDetailed = detailedDevices;
                CheckForDeviceChanges();
            }

            _isScanningForDevices = false;
            FoundBusyTagSerialDevices?.Invoke(this, _busyTagSerialDevices);
            FoundBusyTagDevicesDetailed?.Invoke(this, _busyTagDevicesDetailed);
            return _busyTagSerialDevices;
        }
        catch (Exception ex)
        {
            if (EnableVerboseLogging)
                Debug.WriteLine($"[DEBUG] Windows WMI query failed: {ex.Message}");
            _isScanningForDevices = false;
        }

        return null;
    }

    /// <summary>
    /// Uses reflection to access System.Management for WMI queries at runtime.
    /// This allows the library to work on Windows even when compiled without Windows-specific TFM.
    /// </summary>
    private List<string> FindPortByVidPidWindowsViaReflection(string deviceId)
    {
        var foundDevices = new List<string>();

        try
        {
            // Try to load System.Management assembly at runtime
            Assembly? assembly;
            try
            {
                assembly = Assembly.Load("System.Management");
            }
            catch (Exception ex)
            {
                if (EnableVerboseLogging)
                    Console.WriteLine($"  [DEBUG] System.Management assembly load failed: {ex.Message}");
                return foundDevices;
            }

            if (assembly == null)
            {
                if (EnableVerboseLogging)
                    Console.WriteLine("  [DEBUG] System.Management assembly not found at runtime");
                return foundDevices;
            }

            if (EnableVerboseLogging)
                Console.WriteLine($"  [DEBUG] System.Management loaded from: {assembly.Location}");

            // Get the ManagementObjectSearcher type
            var searcherType = assembly.GetType("System.Management.ManagementObjectSearcher");
            if (searcherType == null)
            {
                if (EnableVerboseLogging)
                    Console.WriteLine("  [DEBUG] ManagementObjectSearcher type not found");
                return foundDevices;
            }

            // Create instance with query
            const string query = "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE '%USB%'";
            var searcher = Activator.CreateInstance(searcherType, query);
            if (searcher == null)
            {
                if (EnableVerboseLogging)
                    Console.WriteLine("  [DEBUG] Failed to create ManagementObjectSearcher instance");
                return foundDevices;
            }

            try
            {
                // Call Get() method
                var getMethod = searcherType.GetMethod("Get", Type.EmptyTypes);
                if (getMethod == null)
                {
                    if (EnableVerboseLogging)
                        Console.WriteLine("  [DEBUG] Get method not found on ManagementObjectSearcher");
                    return foundDevices;
                }

                var collection = getMethod.Invoke(searcher, null);
                if (collection == null)
                {
                    if (EnableVerboseLogging)
                        Console.WriteLine("  [DEBUG] WMI query returned null");
                    return foundDevices;
                }

                int totalDevices = 0;
                // Iterate through the collection
                foreach (var device in (System.Collections.IEnumerable)collection)
                {
                    try
                    {
                        totalDevices++;
                        // ManagementObject uses indexer with string parameter - access via GetValue method
                        var deviceType = device.GetType();

                        // Try to get the indexer (Item property with string parameter)
                        var indexerProperty = deviceType.GetProperty("Item", new[] { typeof(string) });

                        string? deviceIdValue = null;
                        string? nameValue = null;

                        if (indexerProperty != null)
                        {
                            deviceIdValue = indexerProperty.GetValue(device, new object[] { "DeviceID" })?.ToString();
                            nameValue = indexerProperty.GetValue(device, new object[] { "Name" })?.ToString();
                        }
                        else
                        {
                            // Fallback: try accessing via GetPropertyValue method
                            var getPropertyValueMethod = deviceType.GetMethod("GetPropertyValue", new[] { typeof(string) });
                            if (getPropertyValueMethod != null)
                            {
                                deviceIdValue = getPropertyValueMethod.Invoke(device, new object[] { "DeviceID" })?.ToString();
                                nameValue = getPropertyValueMethod.Invoke(device, new object[] { "Name" })?.ToString();
                            }
                        }

                        if (deviceIdValue != null && deviceIdValue.Contains(deviceId, StringComparison.OrdinalIgnoreCase))
                        {
                            if (EnableVerboseLogging)
                                Console.WriteLine($"  [DEBUG] VID/PID match: {deviceIdValue} | Name: {nameValue}");

                            if (nameValue != null && nameValue.Contains("COM"))
                            {
                                // Extract COM port number
                                var match = System.Text.RegularExpressions.Regex.Match(nameValue, @"COM(\d+)");
                                if (match.Success)
                                {
                                    var port = $"COM{match.Groups[1].Value}";
                                    foundDevices.Add(port);
                                }
                            }
                        }
                    }
                    finally
                    {
                        // Dispose the ManagementObject
                        if (device is IDisposable disposableDevice)
                        {
                            disposableDevice.Dispose();
                        }
                    }
                }

                if (EnableVerboseLogging)
                    Console.WriteLine($"  [DEBUG] WMI scanned {totalDevices} USB entities, matched {foundDevices.Count} for {deviceId}");
            }
            finally
            {
                // Dispose the searcher
                if (searcher is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            if (EnableVerboseLogging)
                Console.WriteLine($"  [DEBUG] WMI reflection failed: {ex.GetType().Name}: {ex.Message}");
        }

        return foundDevices;
    }

    #endregion
    
    #region macOS VID/PID Discovery

    private async Task<List<string>?> DiscoverByVidPidMacOsAsync()
    {
        try
        {
            // Get USB device information
            var usbDevices = await GetMacOsUsbDevicesAsync();

            var normalKey = $"{NormalModeVid}:{NormalModePid}";
            var bootKey = $"{BootModeVid}:{BootModePid}";
            bool hasNormal = usbDevices.ContainsKey(normalKey);
            bool hasBoot = usbDevices.ContainsKey(bootKey);

            if (hasNormal || hasBoot)
            {
                // Found a device, now find associated serial ports
                var result = await FindMacOsSerialPortAsync();

                // Add boot-mode entries to detailed list
                if (hasBoot)
                {
                    var ports = SerialPort.GetPortNames();
                    var usbPorts = ports.Where(p => p.StartsWith("/dev/cu.usbmodem")).ToArray();
                    foreach (var port in usbPorts)
                    {
                        if (!_busyTagDevicesDetailed.Any(d => d.PortName == port))
                        {
                            _busyTagDevicesDetailed.Add(new BusyTagDeviceInfo
                            {
                                PortName = port,
                                Mode = hasNormal ? BusyTagDeviceMode.Normal : BusyTagDeviceMode.BootLoader,
                                Vid = BootModeVid,
                                Pid = BootModePid
                            });
                        }
                    }
                    FoundBusyTagDevicesDetailed?.Invoke(this, _busyTagDevicesDetailed);
                }

                return result;
            }
            else
            {
                // Device wasn't found, clear the list
                lock (_lockObject)
                {
                    _busyTagSerialDevices = new List<string>();
                    _busyTagDevicesDetailed = new List<BusyTagDeviceInfo>();
                    CheckForDeviceChanges();
                }
                _isScanningForDevices = false;
                FoundBusyTagSerialDevices?.Invoke(this, _busyTagSerialDevices);
                FoundBusyTagDevicesDetailed?.Invoke(this, _busyTagDevicesDetailed);
            }
        }
        catch (Exception ex)
        {
            if (EnableVerboseLogging)
                Debug.WriteLine($"[DEBUG] macOS VID/PID discovery failed: {ex.Message}");
            _isScanningForDevices = false;
        }

        return null;
    }

    private static async Task<Dictionary<string, string>> GetMacOsUsbDevicesAsync()
    {
        var devices = new Dictionary<string, string>();

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ioreg",
                    Arguments = "-c IOUSBHostDevice -r",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            // Parse ioreg output to extract VID:PID pairs
            var lines = output.Split('\n');
            var deviceBlocks = new List<Dictionary<string, string>>();
            var currentDevice = new Dictionary<string, string>();

            foreach (var line in lines)
            {
                // Start of a new device block
                if (line.Contains("class IOUSBHostDevice"))
                {
                    if (currentDevice.Count > 0)
                    {
                        deviceBlocks.Add(currentDevice);
                    }
                    currentDevice = new Dictionary<string, string>();
                }
                else if (line.Contains("\"idVendor\""))
                {
                    var parts = line.Split('=');
                    if (parts.Length > 1)
                    {
                        var vendorIdStr = parts[1].Trim();
                        if (int.TryParse(vendorIdStr, out int vendorId))
                        {
                            currentDevice["vid"] = vendorId.ToString("X4");
                        }
                    }
                }
                else if (line.Contains("\"idProduct\""))
                {
                    var parts = line.Split('=');
                    if (parts.Length > 1)
                    {
                        var productIdStr = parts[1].Trim();
                        if (int.TryParse(productIdStr, out int productId))
                        {
                            currentDevice["pid"] = productId.ToString("X4");
                        }
                    }
                }
            }

            // Don't forget the last device
            if (currentDevice.Count > 0)
            {
                deviceBlocks.Add(currentDevice);
            }

            // Convert device blocks to VID:PID pairs
            foreach (var device in deviceBlocks)
            {
                if (device.ContainsKey("vid") && device.ContainsKey("pid"))
                {
                    var key = $"{device["vid"]}:{device["pid"]}";
                    devices[key] = key;
                }
            }
        }
        catch (Exception ex) when (ex.Message.Contains("not supported on this platform"))
        {
            // Expected on MacCatalyst - process execution is not supported in sandbox
            // Fall through silently and return empty devices
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DEBUG] Failed to get macOS USB devices: {ex.Message}");
        }

        return devices;
    }

    private async Task<List<string>?> FindMacOsSerialPortAsync()
    {
        var foundDevices = new List<string>();

        try
        {
            // Since we've already confirmed the USB device exists via VID/PID,
            // just return the USB modem ports that match our device pattern
            var ports = SerialPort.GetPortNames();

            var usbPorts = ports.Where(p =>
                p.StartsWith("/dev/cu.usbmodem")).ToArray();

            // Add all USB modem ports since we already confirmed the USB device exists
            // Don't try to communicate with them as they might already be in use
            foundDevices.AddRange(usbPorts);

            lock (_lockObject)
            {
                _busyTagSerialDevices = foundDevices;
                CheckForDeviceChanges();
            }

            _isScanningForDevices = false;
            FoundBusyTagSerialDevices?.Invoke(this, _busyTagSerialDevices);
            return _busyTagSerialDevices;
        }
        catch (Exception ex)
        {
            if (EnableVerboseLogging)
                Debug.WriteLine($"[DEBUG] macOS serial port discovery failed: {ex.Message}");
            _isScanningForDevices = false;
        }

        return null;
    }

    #endregion

    #region Linux VID/PID Discovery - EXPERIMENTAL (Disabled by default)

    // Linux support is experimental and not fully tested
    // Enable by setting EnableExperimentalLinuxSupport = true

    private async Task<List<string>?> DiscoverByVidPidLinuxAsync()
    {
        try
        {
            // Use lsusb to find the device
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "lsusb",
                    Arguments = "",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            var normalId = $"{NormalModeVid}:{NormalModePid}";
            var bootId = $"{BootModeVid}:{BootModePid}";
            bool hasNormal = output.Contains(normalId, StringComparison.OrdinalIgnoreCase);
            bool hasBoot = output.Contains(bootId, StringComparison.OrdinalIgnoreCase);

            if (hasNormal || hasBoot)
            {
                var result = await FindLinuxSerialPortAsync();

                if (hasBoot && !hasNormal)
                {
                    // All found ports are boot-mode devices
                    lock (_lockObject)
                    {
                        _busyTagDevicesDetailed = _busyTagSerialDevices?
                            .Select(p => new BusyTagDeviceInfo
                            {
                                PortName = p, Mode = BusyTagDeviceMode.BootLoader,
                                Vid = BootModeVid, Pid = BootModePid
                            }).ToList() ?? new List<BusyTagDeviceInfo>();
                    }
                    FoundBusyTagDevicesDetailed?.Invoke(this, _busyTagDevicesDetailed);
                }

                return result;
            }
            else
            {
                // Device not found, clear the list
                lock (_lockObject)
                {
                    _busyTagSerialDevices = new List<string>();
                    _busyTagDevicesDetailed = new List<BusyTagDeviceInfo>();
                    CheckForDeviceChanges();
                }
                _isScanningForDevices = false;
                FoundBusyTagSerialDevices?.Invoke(this, _busyTagSerialDevices);
                FoundBusyTagDevicesDetailed?.Invoke(this, _busyTagDevicesDetailed);
            }
        }
        catch (Exception ex)
        {
            if (EnableVerboseLogging)
                Debug.WriteLine($"[DEBUG] Linux VID/PID discovery failed: {ex.Message}");
            _isScanningForDevices = false;
        }

        return null;
    }

    private async Task<List<string>?> FindLinuxSerialPortAsync()
    {
        var foundDevices = new List<string>();
        
        try
        {
            var ports = SerialPort.GetPortNames();
            
            // Filter for USB serial devices
            var usbPorts = ports.Where(p => 
                p.StartsWith("/dev/ttyUSB") || 
                p.StartsWith("/dev/ttyACM")).ToArray();

            foreach (var port in usbPorts)
            {
                try
                {
                    using var testPort = new SerialPort(port, 1500000);
                    testPort.ReadTimeout = 2000;
                    testPort.WriteTimeout = 2000;

                    testPort.Open();
                    testPort.WriteLine("AT+GDN");
                    await Task.Delay(100);

                    if (testPort.BytesToRead <= 0) continue;
                    var response = testPort.ReadExisting();
                    if (!response.Contains("+DN:busytag-")) continue;

                    if (EnableVerboseLogging)
                        Debug.WriteLine($"[INFO] Found BusyTag device on {port} via Linux discovery");
                    foundDevices.Add(port);
                }
                catch
                {
                    continue;
                }
            }

            lock (_lockObject)
            {
                _busyTagSerialDevices = foundDevices;
                CheckForDeviceChanges();
            }

            _isScanningForDevices = false;
            FoundBusyTagSerialDevices?.Invoke(this, _busyTagSerialDevices);
            return _busyTagSerialDevices;
        }
        catch (Exception ex)
        {
            if (EnableVerboseLogging)
                Debug.WriteLine($"[DEBUG] Linux serial port discovery failed: {ex.Message}");
            _isScanningForDevices = false;
        }

        return null;
    }

    #endregion

    #region Fallback AT Command Discovery

    private async Task<List<string>?> FindDeviceByAtCommandAsync()
    {
        var foundDevices = new List<string>();
        
        try
        {
            var ports = SerialPort.GetPortNames();
            
            foreach (var port in ports)
            {
                try
                {
                    using var testPort = new SerialPort(port, 1500000);
                    testPort.ReadTimeout = 2000;
                    testPort.WriteTimeout = 2000;

                    testPort.Open();
                    testPort.WriteLine("AT+GDN");
                    await Task.Delay(100);

                    if (testPort.BytesToRead <= 0) continue;
                    var response = testPort.ReadExisting();
                    if (!response.Contains("+DN:busytag-")) continue;

                    if (EnableVerboseLogging)
                        Debug.WriteLine($"[INFO] Found BusyTag device on {port} via AT command");
                    foundDevices.Add(port);
                }
                catch
                {
                    continue;
                }
            }

            lock (_lockObject)
            {
                _busyTagSerialDevices = foundDevices;
                CheckForDeviceChanges();
            }

            _isScanningForDevices = false;
            FoundBusyTagSerialDevices?.Invoke(this, _busyTagSerialDevices);
            return _busyTagSerialDevices;
        }
        catch (Exception ex)
        {
            if (EnableVerboseLogging)
                Debug.WriteLine($"[DEBUG] AT command discovery failed: {ex.Message}");
            _isScanningForDevices = false;
        }

        return null;
    }

    #endregion

    #region IDisposable Implementation

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            StopPeriodicDeviceSearch();
        }

        _disposed = true;
    }

    ~BusyTagManager()
    {
        Dispose(false);
    }

    #endregion
}