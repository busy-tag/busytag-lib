#if MACCATALYST
using System.Diagnostics;
using System.Runtime.InteropServices;
using ObjCRuntime;

namespace BusyTag.Lib.Platforms.MacCatalyst;

/// <summary>
/// IOKit USB Bulk Transfer transport for macOS. Bypasses the serial port driver
/// and communicates directly with the BusyTag device via USB bulk endpoints,
/// achieving native USB speed instead of serial port driver limitations.
/// </summary>
public class IOKitUsbTransport : IDisposable
{
    private const string LibName = "@rpath/BusyTagUSBDriver.framework/BusyTagUSBDriver";

    // MARK: - Native P/Invoke declarations

    [DllImport(LibName)]
    private static extern IntPtr btusb_create();

    [DllImport(LibName)]
    private static extern void btusb_destroy(IntPtr handle);

    [DllImport(LibName)]
    private static extern void btusb_start_monitoring(IntPtr handle);

    [DllImport(LibName)]
    private static extern void btusb_stop_monitoring(IntPtr handle);

    [DllImport(LibName)]
    private static extern int btusb_is_connected(IntPtr handle);

    [DllImport(LibName)]
    private static extern int btusb_is_device_present(IntPtr handle);

    [DllImport(LibName)]
    private static extern int btusb_send(IntPtr handle, byte[] data, int length);

    [DllImport(LibName, CharSet = CharSet.Ansi)]
    private static extern int btusb_send_string(IntPtr handle, string str);

    // MARK: - Callback delegate types

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DataCallbackDelegate(IntPtr data, int length, IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConnectionCallbackDelegate(int connected, IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LogCallbackDelegate(IntPtr message, IntPtr context);

    [DllImport(LibName)]
    private static extern void btusb_set_data_callback(IntPtr handle, DataCallbackDelegate? callback, IntPtr context);

    [DllImport(LibName)]
    private static extern void btusb_set_connection_callback(IntPtr handle, ConnectionCallbackDelegate? callback, IntPtr context);

    [DllImport(LibName)]
    private static extern void btusb_set_log_callback(IntPtr handle, LogCallbackDelegate? callback, IntPtr context);

    // MARK: - Events

    /// <summary>
    /// Fired when data is received from the USB device (bulk IN transfer).
    /// The byte array contains the raw received data.
    /// </summary>
    public event Action<byte[]>? DataReceived;

    /// <summary>
    /// Fired when the USB connection state changes.
    /// True = connected, False = disconnected.
    /// </summary>
    public event Action<bool>? ConnectionChanged;

    // MARK: - Instance state

    private IntPtr _handle;
    private bool _disposed;

    // Static callback delegates — pinned so GC doesn't collect them.
    // Using static delegates with instance lookup via GCHandle context pointer.
    private static readonly DataCallbackDelegate s_dataCallback = OnDataReceived;
    private static readonly ConnectionCallbackDelegate s_connectionCallback = OnConnectionChanged;
    private static readonly LogCallbackDelegate s_logCallback = OnLogMessage;

    // GCHandle to prevent GC from moving/collecting this instance while native code holds a pointer
    private GCHandle _gcHandle;

    /// <summary>
    /// Returns true if the USB device is connected and the interface is seized.
    /// </summary>
    public bool IsConnected => _handle != IntPtr.Zero && btusb_is_connected(_handle) != 0;

    /// <summary>
    /// Returns true if a matching USB device is present in the IOKit registry.
    /// </summary>
    public bool IsDevicePresent => _handle != IntPtr.Zero && btusb_is_device_present(_handle) != 0;

    public IOKitUsbTransport()
    {
        _handle = btusb_create();
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("[IOKit] Failed to create USB transport handle");

        // Pin this instance for the native callback context
        _gcHandle = GCHandle.Alloc(this);
        var contextPtr = GCHandle.ToIntPtr(_gcHandle);

        // Register callbacks with native code
        btusb_set_data_callback(_handle, s_dataCallback, contextPtr);
        btusb_set_connection_callback(_handle, s_connectionCallback, contextPtr);
        btusb_set_log_callback(_handle, s_logCallback, contextPtr);

        Debug.WriteLine("[IOKit] USB transport created");
    }

    /// <summary>
    /// Start monitoring for BusyTag USB device connections (VID:0x303A, PID:0x81DF).
    /// When a device is found, the native layer automatically seizes the CDC Data interface
    /// and sets up bulk IN/OUT endpoints.
    /// </summary>
    public void StartMonitoring()
    {
        if (_handle != IntPtr.Zero)
            btusb_start_monitoring(_handle);
    }

    /// <summary>
    /// Stop monitoring for USB device connections.
    /// </summary>
    public void StopMonitoring()
    {
        if (_handle != IntPtr.Zero)
            btusb_stop_monitoring(_handle);
    }

    /// <summary>
    /// Send raw bytes to the USB device via bulk OUT transfer.
    /// </summary>
    public bool Send(byte[] data, int offset, int count)
    {
        if (_handle == IntPtr.Zero || data == null) return false;

        byte[] chunk;
        if (offset == 0 && count == data.Length)
        {
            chunk = data;
        }
        else
        {
            chunk = new byte[count];
            Buffer.BlockCopy(data, offset, chunk, 0, count);
        }

        return btusb_send(_handle, chunk, count) != 0;
    }

    /// <summary>
    /// Send a string as an AT command. The native layer appends \r\n.
    /// </summary>
    public bool SendLine(string command)
    {
        if (_handle == IntPtr.Zero) return false;
        return btusb_send_string(_handle, command) != 0;
    }

    // MARK: - Static callbacks (P/Invoke requires static methods)

    [MonoPInvokeCallback(typeof(DataCallbackDelegate))]
    private static void OnDataReceived(IntPtr data, int length, IntPtr context)
    {
        try
        {
            if (context == IntPtr.Zero || data == IntPtr.Zero || length <= 0) return;

            var gcHandle = GCHandle.FromIntPtr(context);
            if (!gcHandle.IsAllocated) return;

            var transport = (IOKitUsbTransport)gcHandle.Target!;
            if (transport._disposed) return;

            var buffer = new byte[length];
            Marshal.Copy(data, buffer, 0, length);

            transport.DataReceived?.Invoke(buffer);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IOKit] Data callback error: {ex.Message}");
        }
    }

    [MonoPInvokeCallback(typeof(ConnectionCallbackDelegate))]
    private static void OnConnectionChanged(int connected, IntPtr context)
    {
        try
        {
            if (context == IntPtr.Zero) return;

            var gcHandle = GCHandle.FromIntPtr(context);
            if (!gcHandle.IsAllocated) return;

            var transport = (IOKitUsbTransport)gcHandle.Target!;
            if (transport._disposed) return;

            var isConnected = connected == 1;
            Debug.WriteLine($"[IOKit] Connection changed: {(isConnected ? "CONNECTED via USB Bulk Transfer" : "disconnected")}");
            transport.ConnectionChanged?.Invoke(isConnected);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IOKit] Connection callback error: {ex.Message}");
        }
    }

    [MonoPInvokeCallback(typeof(LogCallbackDelegate))]
    private static void OnLogMessage(IntPtr message, IntPtr context)
    {
        try
        {
            if (message == IntPtr.Zero) return;
            var msg = Marshal.PtrToStringUTF8(message);
            Debug.WriteLine($"[IOKit] {msg}");
        }
        catch
        {
            // Swallow log callback errors to avoid cascading failures
        }
    }

    // MARK: - IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_handle != IntPtr.Zero)
        {
            // Clear callbacks before destroying to avoid callbacks during teardown
            btusb_set_data_callback(_handle, null, IntPtr.Zero);
            btusb_set_connection_callback(_handle, null, IntPtr.Zero);
            btusb_set_log_callback(_handle, null, IntPtr.Zero);
            btusb_destroy(_handle);
            _handle = IntPtr.Zero;
        }

        if (_gcHandle.IsAllocated)
            _gcHandle.Free();

        Debug.WriteLine("[IOKit] USB transport disposed");
        GC.SuppressFinalize(this);
    }

    ~IOKitUsbTransport()
    {
        Dispose();
    }
}
#endif
