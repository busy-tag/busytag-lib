using System.Diagnostics;
using System.IO.Ports;
using System.Runtime.InteropServices;

#if WINDOWS
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
            Trace.WriteLine(e.Message);
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
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || RuntimeInformation.OSDescription.Contains("Darwin"))
        {
            return await DiscoverByVidPidMacOsAsync();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && EnableExperimentalLinuxSupport)
        {
            if (EnableVerboseLogging)
                Console.WriteLine("[WARNING] Linux support is experimental and not fully tested");
            return await DiscoverByVidPidLinuxAsync();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (EnableVerboseLogging)
                Console.WriteLine("[INFO] Linux platform detected but support is disabled. Set EnableExperimentalLinuxSupport = true to enable experimental support.");
        }
        
        _isScanningForDevices = false;
        return null;
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
                Console.WriteLine($"[DEBUG] Windows VID/PID discovery failed: {ex.Message}");
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
#if WINDOWS
            // Query WMI for USB devices
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
            // If System.Management is not available, fall back to AT command testing
            if (EnableVerboseLogging)
                Console.WriteLine("[DEBUG] System.Management not available, falling back to AT command testing");
            
            // return await FindDeviceByAtCommandAsync();
            return null;
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
                Console.WriteLine($"[DEBUG] Windows WMI query failed: {ex.Message}");
            _isScanningForDevices = false;
        }
        
        return null;
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
                Console.WriteLine($"[DEBUG] macOS VID/PID discovery failed: {ex.Message}");
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
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] Failed to get macOS USB devices: {ex.Message}");
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
                Console.WriteLine($"[DEBUG] macOS serial port discovery failed: {ex.Message}");
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
                Console.WriteLine($"[DEBUG] Linux VID/PID discovery failed: {ex.Message}");
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
                    using var testPort = new SerialPort(port, 460800);
                    testPort.ReadTimeout = 2000;
                    testPort.WriteTimeout = 2000;

                    testPort.Open();
                    testPort.WriteLine("AT+GDN");
                    await Task.Delay(100);

                    if (testPort.BytesToRead <= 0) continue;
                    var response = testPort.ReadExisting();
                    if (!response.Contains("+DN:busytag-")) continue;
                    
                    if (EnableVerboseLogging)
                        Console.WriteLine($"[INFO] Found BusyTag device on {port} via Linux discovery");
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
                Console.WriteLine($"[DEBUG] Linux serial port discovery failed: {ex.Message}");
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
                    using var testPort = new SerialPort(port, 460800);
                    testPort.ReadTimeout = 2000;
                    testPort.WriteTimeout = 2000;

                    testPort.Open();
                    testPort.WriteLine("AT+GDN");
                    await Task.Delay(100);

                    if (testPort.BytesToRead <= 0) continue;
                    var response = testPort.ReadExisting();
                    if (!response.Contains("+DN:busytag-")) continue;
                    
                    if (EnableVerboseLogging)
                        Console.WriteLine($"[INFO] Found BusyTag device on {port} via AT command");
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
                Console.WriteLine($"[DEBUG] AT command discovery failed: {ex.Message}");
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