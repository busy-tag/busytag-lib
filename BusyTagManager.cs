using System.Diagnostics;
using System.IO.Ports;
using BusyTagLib.Util;

namespace BusyTagLib;

public class BusyTagManager
{
    public event EventHandler<Dictionary<string, bool>> FoundSerialDevices = null!;
    public event EventHandler<List<string>> FoundBusyTagSerialDevices = null!; 
    private Dictionary<string, bool> _serialDeviceList = new Dictionary<string, bool>();
    private SerialPort? _serialPort;

    public string[] AllSerialPorts()
    {
        return SerialPort.GetPortNames();
    }

    public void FindBusyTagDevice()
    {
        CancellationTokenSource ctsForConnection = new CancellationTokenSource();
        Task.Run(async () =>
        {
            string[] ports = SerialPort.GetPortNames();
            _serialDeviceList = new Dictionary<string, bool>();
            // Display each port name to the console.
            foreach (string port in ports)
            {
                Trace.WriteLine(port);
                _serialDeviceList.Add(port, false);
                if (_serialPort != null && _serialPort.IsOpen) _serialPort.Close();
                _serialPort = new SerialPort(port, 460800, Parity.None, 8, StopBits.One);
                _serialPort.ReadTimeout = 500;
                _serialPort.WriteTimeout = 500;
                _serialPort.WriteBufferSize = 8192;
                _serialPort.ReadBufferSize = 8192;

                _serialPort.DataReceived += new SerialDataReceivedEventHandler(sp_DataReceived);
                try
                {
                    _serialPort.Open();
                    if (_serialPort.IsOpen)
                    {
                        SendCommand(new SerialPortCommands().GetCommand(SerialPortCommands.Commands.GetDeviceName));
                        Thread.Sleep(100);
                        _serialPort.Close();
                    }
                }
                catch (Exception e)
                {
                    Trace.WriteLine($"Error: {e.Message}");
                }
            }
            
            List<string> busyTagPortList = new List<string>();
            foreach (var item in _serialDeviceList)
            {
                if (item.Value)
                {
                    busyTagPortList.Add(item.Key);
                }
            }
            FoundSerialDevices?.Invoke(this, _serialDeviceList);
            FoundBusyTagSerialDevices?.Invoke(this, busyTagPortList);
            ctsForConnection.Cancel(); // Was CancelAsync
        }, ctsForConnection.Token);
    }

    private void SendCommand(string data)
        {
            if (_serialPort != null && _serialPort.IsOpen)
            {
                long timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                Trace.WriteLine($"[{UnixToDate(timestamp, "HH:mm:ss.fff")}]TX:{data}");
                _serialPort.WriteLine(data);
            }
        }
    private void sp_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        var port = (SerialPort)sender;
        int buf_size = 32;
        var buf = new byte[buf_size];
        int len = _serialPort.Read(buf, 0, buf_size);
        string data = System.Text.Encoding.UTF8.GetString(buf, 0, buf.Length);
        long timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        Trace.WriteLine($"[{UnixToDate(timestamp, "HH:mm:ss.fff")}]RX:{data}");
        if(data.Contains("+DN:busytag-"))
        {
            _serialDeviceList[port.PortName] = true;
        }
    }
        
        private static string UnixToDate(long timestamp, string convertFormat)
        {
            DateTime convertedUnixTime = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).DateTime;
            return convertedUnixTime.ToString(convertFormat);
        }
}