using System.Diagnostics;
using System.IO.Ports;
using System.Management;
using System.Runtime.InteropServices;

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
        if (!_isPeriodicSearchEnabled)
            return;

        _ = await FindBusyTagDevice();
    }

    public async Task<List<string>?> FindBusyTagDevice()
    {
        // Trace.WriteLine($"FindBusyTagDevice(), _isScanningForDevices: {_isScanningForDevices}");
        if (_isScanningForDevices) return null;
        _isScanningForDevices = true;
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return await DiscoverByVidPidWindowsAsync();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return await DiscoverByVidPidMacOsAsync();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return await DiscoverByVidPidLinuxAsync();
        }
        
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
#if WINDOWS
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
                                Console.WriteLine($"[INFO] Found BusyTag device on {port} via VID/PID");
                                foundDevices.Add(port);
                            }
                        }
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
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] Windows WMI query failed: {ex.Message}");
            _isScanningForDevices = false;
        }
        
        return null;
    }
#else
    private Task<List<string>?> DiscoverByVidPidWindowsAsync()
    {
        _isScanningForDevices = false;
        return Task.FromResult<List<string>?>(null);
    }
#endif
    
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
            
            if (usbDevices.ContainsKey($"{targetVid}:{targetPid}"))
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
                    FileName = "system_profiler",
                    Arguments = "SPUSBDataType -json",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            // Parse JSON output to extract VID:PID pairs
            // This is a simplified version - you might want to use System.Text.Json
            var lines = output.Split('\n');
            string? currentVid = null;
            string? currentPid = null;

            foreach (var line in lines)
            {
                if (line.Contains("\"vendor_id\""))
                {
                    var parts = line.Split(':');
                    if (parts.Length > 1)
                    {
                        currentVid = parts[1].Trim().Trim('"').Trim(',');
                        if (currentVid.StartsWith("0x"))
                            currentVid = currentVid.Substring(2);
                    }
                }
                else if (line.Contains("\"product_id\""))
                {
                    var parts = line.Split(':');
                    if (parts.Length > 1)
                    {
                        currentPid = parts[1].Trim().Trim('"').Trim(',');
                        if (currentPid.StartsWith("0x"))
                            currentPid = currentPid.Substring(2);
                    }
                }

                if (currentVid == null || currentPid == null) continue;
                devices[$"{currentVid}:{currentPid}"] = $"{currentVid}:{currentPid}";
                currentVid = null;
                currentPid = null;
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
            // Look for serial devices that might be our BusyTag
            var ports = SerialPort.GetPortNames();
            
            // Filter for USB serial devices (usually start with /dev/cu.usbserial or /dev/cu.usbmodem)
            var usbPorts = ports.Where(p => 
                p.StartsWith("/dev/cu.usbserial") || 
                p.StartsWith("/dev/cu.usbmodem") ||
                p.StartsWith("/dev/tty.usbserial") ||
                p.StartsWith("/dev/tty.usbmodem")).ToArray();

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
                    Console.WriteLine($"[INFO] Found BusyTag device on {port} via macOS discovery");
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
            Console.WriteLine($"[DEBUG] macOS serial port discovery failed: {ex.Message}");
            _isScanningForDevices = false;
        }

        return null;
    }

    #endregion

    #region Linux VID/PID Discovery

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
            Console.WriteLine($"[DEBUG] Linux serial port discovery failed: {ex.Message}");
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