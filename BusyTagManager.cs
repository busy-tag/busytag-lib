using System.Diagnostics;
using System.IO.Ports;
using System.Reflection;
using System.Runtime.InteropServices;

#if WINDOWS || WINDOWS10_0_19041_0_OR_GREATER
using System.Management;
#endif

namespace BusyTag.Lib;

public class BusyTagManager : IDisposable
{
    public event EventHandler<List<string>?>? FoundBusyTagSerialDevices;
    public event EventHandler<string>? DeviceConnected;
    public event EventHandler<string>? DeviceDisconnected;
    
    private List<string>? _busyTagSerialDevices = new();
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
            const string vid = "303A";
            const string pid = "81DF";
            
            return await Task.Run(() => FindPortByVidPidWindows(vid, pid));
        }
        catch (Exception ex)
        {
            if (EnableVerboseLogging)
                Debug.WriteLine($"[DEBUG] Windows VID/PID discovery failed: {ex.Message}");
            _isScanningForDevices = false;
            return null;
        }
    }
    
    private List<string>? FindPortByVidPidWindows(string vid, string pid)
    {
        var deviceId = $"VID_{vid}&PID_{pid}";
        var foundDevices = new List<string>();

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

                    if (deviceIdStr != null && deviceIdStr.Contains(deviceId))
                    {
                        // Check if it's a COM port
                        var name = device["Name"]?.ToString();
                        if (name != null && name.Contains("COM"))
                        {
                            // Extract COM port number
                            var match = System.Text.RegularExpressions.Regex.Match(name, @"COM(\d+)");
                            if (match.Success)
                            {
                                var port = $"COM{match.Groups[1].Value}";
                                foundDevices.Add(port);
                            }
                        }
                    }
                }
            }
#else
            // Use runtime reflection to access System.Management when not available at compile-time
            foundDevices = FindPortByVidPidWindowsViaReflection(deviceId);
#endif

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
            var assembly = Assembly.Load("System.Management");
            if (assembly == null)
            {
                if (EnableVerboseLogging)
                    Debug.WriteLine("[DEBUG] System.Management assembly not found at runtime");
                return foundDevices;
            }

            // Get the ManagementObjectSearcher type
            var searcherType = assembly.GetType("System.Management.ManagementObjectSearcher");
            if (searcherType == null)
            {
                if (EnableVerboseLogging)
                    Debug.WriteLine("[DEBUG] ManagementObjectSearcher type not found");
                return foundDevices;
            }

            // Create instance with query
            const string query = "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE '%USB%'";
            var searcher = Activator.CreateInstance(searcherType, query);
            if (searcher == null)
            {
                if (EnableVerboseLogging)
                    Debug.WriteLine("[DEBUG] Failed to create ManagementObjectSearcher instance");
                return foundDevices;
            }

            try
            {
                // Call Get() method
                var getMethod = searcherType.GetMethod("Get", Type.EmptyTypes);
                if (getMethod == null)
                {
                    if (EnableVerboseLogging)
                        Debug.WriteLine("[DEBUG] Get method not found on ManagementObjectSearcher");
                    return foundDevices;
                }

                var collection = getMethod.Invoke(searcher, null);
                if (collection == null)
                {
                    if (EnableVerboseLogging)
                        Debug.WriteLine("[DEBUG] WMI query returned null");
                    return foundDevices;
                }

                // Iterate through the collection
                foreach (var device in (System.Collections.IEnumerable)collection)
                {
                    try
                    {
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

                        if (deviceIdValue != null && deviceIdValue.Contains(deviceId))
                        {
                            if (nameValue != null && nameValue.Contains("COM"))
                            {
                                // Extract COM port number
                                var match = System.Text.RegularExpressions.Regex.Match(nameValue, @"COM(\d+)");
                                if (match.Success)
                                {
                                    var port = $"COM{match.Groups[1].Value}";
                                    foundDevices.Add(port);
                                    if (EnableVerboseLogging)
                                        Debug.WriteLine($"[DEBUG] Found device via WMI reflection: {port}");
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
            }
            finally
            {
                // Dispose the searcher
                if (searcher is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            if (EnableVerboseLogging)
                Debug.WriteLine($"[DEBUG] WMI reflection found {foundDevices.Count} device(s)");
        }
        catch (Exception ex)
        {
            if (EnableVerboseLogging)
                Debug.WriteLine($"[DEBUG] WMI reflection failed: {ex.Message}");
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
            
            const string targetVid = "303A";
            const string targetPid = "81DF";

            const string key = $"{targetVid}:{targetPid}";
            if (usbDevices.ContainsKey(key))
            {
                // Found the device, now find associated serial port
                return await FindMacOsSerialPortAsync();
            }
            else
            {
                // Device not found, clear the list
                lock (_lockObject)
                {
                    _busyTagSerialDevices = new List<string>();
                    CheckForDeviceChanges();
                }
                _isScanningForDevices = false;
                FoundBusyTagSerialDevices?.Invoke(this, _busyTagSerialDevices);
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
            const string targetVid = "303A";
            const string targetPid = "81DF";  

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

            if (output.Contains($"{targetVid}:{targetPid}"))
            {
                // Device found, look for an associated tty device
                return await FindLinuxSerialPortAsync();
            }
            else
            {
                // Device not found, clear the list
                lock (_lockObject)
                {
                    _busyTagSerialDevices = new List<string>();
                    CheckForDeviceChanges();
                }
                _isScanningForDevices = false;
                FoundBusyTagSerialDevices?.Invoke(this, _busyTagSerialDevices);
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