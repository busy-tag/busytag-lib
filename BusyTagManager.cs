using System.Diagnostics;
using System.IO.Ports;
using System.Management;
using System.Runtime.InteropServices;
using BusyTag.Lib.Util;

namespace BusyTag.Lib;

public class BusyTagManager
{
    // public event EventHandler<Dictionary<string, bool>>? FoundSerialDevices;
    public event EventHandler<List<string>?>? FoundBusyTagSerialDevices;
    // private Dictionary<string, bool> _serialDeviceList = new();
    private List<string>? _busyTagSerialDevices = new();
    // private SerialPort? _serialPort;
    private static bool _isScanningForDevices = false;

    public string[] AllSerialPorts()
    {
        return SerialPort.GetPortNames();
    }

    public void FindBusyTagDevice()
    {
        // Trace.WriteLine($"FindBusyTagDevice(), _isScanningForDevices: {_isScanningForDevices}");
        if (_isScanningForDevices) return;
        _isScanningForDevices = true;
        
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _ = DiscoverByVidPidWindowsAsync();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                _ = DiscoverByVidPidMacOsAsync();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _ = DiscoverByVidPidLinuxAsync();
            }

//
//         Task.Run(async () =>
//         {
//             string[] ports = SerialPort.GetPortNames();
//             // Trace.WriteLine($"ports: {string.Join(", ", ports)}");
//             _serialDeviceList = new Dictionary<string, bool>();
//
//             foreach (var port in ports)
//             {
// #if MACCATALYST
//                 if (!_serialDeviceList.ContainsKey(port) &&
//                     port.StartsWith("/dev/tty.usbmodem", StringComparison.Ordinal))
// #else
//                 if (!_serialDeviceList.ContainsKey(port))
// #endif
//                 {
//                     _serialDeviceList[port] = false;
//                 }
//             }
//
//             foreach (var port in _serialDeviceList.Keys)
//             {
//                 // Trace.WriteLine($"Port: {port}");
//                 // _serialDeviceList.Add(port, false);
//                 if (_serialPort != null && _serialPort.IsOpen)
//                     _serialPort.Close();
//
//                 _serialPort = new SerialPort(port, 460800, Parity.None, 8, StopBits.One)
//                 {
//                     ReadTimeout = 500,
//                     WriteTimeout = 500,
//                     WriteBufferSize = 8192,
//                     ReadBufferSize = 8192
//                 };
//
//                 _serialPort.DataReceived += sp_DataReceived;
//                 _serialPort.ErrorReceived += sp_ErrorReceived;
//
//                 var cts = new CancellationTokenSource();
//                 cts.CancelAfter(TimeSpan.FromSeconds(1)); // Set timeout to 1 second
//
//                 try
//                 {
//                     var portOpened = await OpenSerialPortWithTimeoutAsync(_serialPort, cts.Token);
//                     if (portOpened && _serialPort.IsOpen)
//                     // _serialPort.Open();
//                     // if (_serialPort.IsOpen)
//                     {
//                         SendCommand(new SerialPortCommands().GetCommand(SerialPortCommands.Commands.GetDeviceName));
//                         await Task.Delay(150, cts.Token); // Wait for 150 ms to receive data
//                         _serialPort.Close();
//                     }
//                 }
//                 catch (OperationCanceledException)
//                 {
//                     Trace.WriteLine($"Timeout opening port: {port}");
//                 }
//                 catch (Exception e)
//                 {
//                     Trace.WriteLine($"Error: {e.Message}");
//                 }
//             }
//
//             var busyTagPortList = new List<string>();
//             foreach (var item in _serialDeviceList)
//             {
//                 if (item.Value)
//                 {
//                     busyTagPortList.Add(item.Key);
//                 }
//             }
//
//             _isScanningForDevices = false;
//
//             SafeInvokeFoundSerialDevices(_serialDeviceList);
//             SafeInvokeFoundBusyTagSerialDevices(busyTagPortList);
//         });
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
            return null;
        }
    }
    
    private List<string>? FindPortByVidPidWindows(string vid, string pid)
    {
        var deviceId = $"VID_{vid}&PID_{pid}";
        
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
                                if(_busyTagSerialDevices != null && !_busyTagSerialDevices.Contains(port))
                                    _busyTagSerialDevices?.Add(port);
                            }
                        }
                    }
                }

                _isScanningForDevices = false;
                FoundBusyTagSerialDevices?.Invoke(this, _busyTagSerialDevices);
                return _busyTagSerialDevices;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] Windows WMI query failed: {ex.Message}");
        }
        
        return null;
    }
#else
    private Task<List<string>?> DiscoverByVidPidWindowsAsync()
    {
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
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] macOS VID/PID discovery failed: {ex.Message}");
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
                    if(_busyTagSerialDevices != null && !_busyTagSerialDevices.Contains(port))
                        _busyTagSerialDevices?.Add(port);
                }
                catch
                {
                    continue;
                }
            }
            _isScanningForDevices = false;
            FoundBusyTagSerialDevices?.Invoke(this, _busyTagSerialDevices);
            return _busyTagSerialDevices;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] macOS serial port discovery failed: {ex.Message}");
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
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] Linux VID/PID discovery failed: {ex.Message}");
        }

        return null;
    }

    private async Task<List<string>?> FindLinuxSerialPortAsync()
    {
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
                    if(_busyTagSerialDevices != null && !_busyTagSerialDevices.Contains(port))
                        _busyTagSerialDevices?.Add(port);
                }
                catch
                {
                    continue;
                }
            }
            _isScanningForDevices = false;
            FoundBusyTagSerialDevices?.Invoke(this, _busyTagSerialDevices);
            return _busyTagSerialDevices;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] Linux serial port discovery failed: {ex.Message}");
        }

        return null;
    }

    #endregion


    //
    // private void SafeInvokeFoundSerialDevices(Dictionary<string, bool> devices)
    // {
    //     if (FoundSerialDevices == null) return;
    //
    //     try
    //     {
    //         FoundSerialDevices.Invoke(this, devices);
    //     }
    //     catch (Exception ex)
    //     {
    //         Trace.WriteLine($"Error in FoundSerialDevices event handler: {ex.Message}");
    //     }
    // }
    //
    // private void SafeInvokeFoundBusyTagSerialDevices(List<string> devices)
    // {
    //     if (FoundBusyTagSerialDevices == null) return;
    //
    //     try
    //     {
    //         FoundBusyTagSerialDevices.Invoke(this, devices);
    //     }
    //     catch (Exception ex)
    //     {
    //         Trace.WriteLine($"Error in FoundBusyTagSerialDevices event handler: {ex.Message}");
    //     }
    // }
    //
    // private static async Task<bool> OpenSerialPortWithTimeoutAsync(SerialPort serialPort, CancellationToken token)
    // {
    //     var tcs = new TaskCompletionSource<bool>();
    //
    //     var thread = new Thread(() =>
    //     {
    //         try
    //         {
    //             serialPort.Open();
    //             tcs.TrySetResult(true);
    //         }
    //         catch (Exception ex)
    //         {
    //             tcs.TrySetException(ex);
    //         }
    //     });
    //
    //     thread.Start();
    //
    //     await using (token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false))
    //     {
    //         try
    //         {
    //             return await tcs.Task.ConfigureAwait(false);
    //         }
    //         catch (OperationCanceledException operationCanceledException)
    //         {
    //             Trace.WriteLine($"Error: {operationCanceledException.Message}");
    //             // The thread will exit naturally since it checks the cancellation token
    //             throw;
    //         }
    //         catch
    //         {
    //             Trace.WriteLine($"Error");
    //             // The thread will handle exceptions and exit naturally
    //             throw;
    //         }
    //     }
    // }
    //
    // private void SendCommand(string data)
    // {
    //     if (_serialPort is not { IsOpen: true }) return;
    //
    //     var timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
    //     Trace.WriteLine($"[{UnixToDate(timestamp, "HH:mm:ss.fff")}]TX:{data}");
    //     _serialPort.WriteLine(data);
    // }
    //
    // private void sp_DataReceived(object sender, SerialDataReceivedEventArgs e)
    // {
    //     if (_serialPort is not { IsOpen: true }) return;
    //
    //     try
    //     {
    //         const int bufSize = 512;
    //         var buf = new byte[bufSize];
    //         var len = _serialPort.Read(buf, 0, bufSize);
    //         var data = System.Text.Encoding.UTF8.GetString(buf, 0, len);
    //         var timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
    //         Trace.WriteLine($"[{UnixToDate(timestamp, "HH:mm:ss.fff")}]RX:{data}");
    //
    //         if (data.Contains("+DN:busytag-") || data.Contains("+evn:"))
    //         {
    //             _serialDeviceList[_serialPort.PortName] = true;
    //         }
    //     }
    //     catch (InvalidOperationException ex) when (ex.Message.Contains("port is closed"))
    //     {
    //         Trace.WriteLine("Port closed during read operation");
    //     }
    //     catch (Exception ex)
    //     {
    //         Trace.WriteLine($"Error in DataReceived: {ex.Message}");
    //     }
    // }
    //
    // private static void sp_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    // {
    //     Trace.WriteLine(e.ToString());
    // }
    //
    // private static string UnixToDate(long timestamp, string convertFormat)
    // {
    //     var convertedUnixTime = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).DateTime;
    //     return convertedUnixTime.ToString(convertFormat);
    // }
}