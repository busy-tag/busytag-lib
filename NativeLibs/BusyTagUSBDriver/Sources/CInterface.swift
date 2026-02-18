import Foundation

// MARK: - C callback types

public typealias BTUSBDataCallback = @convention(c) (UnsafePointer<UInt8>?, Int32, UnsafeMutableRawPointer?) -> Void
public typealias BTUSBConnectionCallback = @convention(c) (Int32, UnsafeMutableRawPointer?) -> Void
public typealias BTUSBLogCallback = @convention(c) (UnsafePointer<CChar>?, UnsafeMutableRawPointer?) -> Void

// MARK: - Bridge Delegate

/// Bridges USBDeviceManager delegate callbacks to C function pointers for P/Invoke consumption.
private class BridgeDelegate: USBDeviceManagerDelegate {
    var dataCallback: BTUSBDataCallback?
    var dataContext: UnsafeMutableRawPointer?
    var connectionCallback: BTUSBConnectionCallback?
    var connectionContext: UnsafeMutableRawPointer?
    var logCallback: BTUSBLogCallback?
    var logContext: UnsafeMutableRawPointer?

    func usbManagerDidConnect(_ manager: USBDeviceManager) {
        connectionCallback?(1, connectionContext)
    }

    func usbManagerDidDisconnect(_ manager: USBDeviceManager) {
        connectionCallback?(0, connectionContext)
    }

    func usbManagerReconnecting(_ manager: USBDeviceManager, attempt: Int) {
        // Reconnecting state - logged via log callback
    }

    func usbManagerReconnectFailed(_ manager: USBDeviceManager) {
        connectionCallback?(-1, connectionContext)
    }

    func usbManager(_ manager: USBDeviceManager, didReceiveData data: [UInt8]) {
        data.withUnsafeBufferPointer { buffer in
            dataCallback?(buffer.baseAddress, Int32(buffer.count), dataContext)
        }
    }

    func usbManager(_ manager: USBDeviceManager, didLog message: String) {
        message.withCString { cStr in
            logCallback?(cStr, logContext)
        }
    }
}

// MARK: - Managed Handle

/// Holds both the USBDeviceManager and its BridgeDelegate to prevent ARC from releasing them.
private class ManagedHandle {
    let manager: USBDeviceManager
    let bridgeDelegate: BridgeDelegate

    init() {
        bridgeDelegate = BridgeDelegate()
        manager = USBDeviceManager()
        manager.delegate = bridgeDelegate
    }
}

// MARK: - C Interface (exported via @_cdecl for P/Invoke)

/// Create a new USB transport handle. Returns an opaque pointer. Must be destroyed with btusb_destroy.
@_cdecl("btusb_create")
public func btusb_create() -> UnsafeMutableRawPointer {
    let handle = ManagedHandle()
    return Unmanaged.passRetained(handle).toOpaque()
}

/// Destroy a USB transport handle and release all resources.
@_cdecl("btusb_destroy")
public func btusb_destroy(_ rawHandle: UnsafeMutableRawPointer) {
    let handle = Unmanaged<ManagedHandle>.fromOpaque(rawHandle).takeRetainedValue()
    handle.manager.stopMonitoring()
    handle.manager.disconnect()
    // ARC releases handle when takeRetainedValue's reference goes out of scope
}

/// Start monitoring for USB device connections (VID:0x303A, PID:0x81DF).
/// Must be called from the main thread, or will dispatch to it synchronously.
@_cdecl("btusb_start_monitoring")
public func btusb_start_monitoring(_ rawHandle: UnsafeMutableRawPointer) {
    let handle = Unmanaged<ManagedHandle>.fromOpaque(rawHandle).takeUnretainedValue()
    if Thread.isMainThread {
        handle.manager.startMonitoring()
    } else {
        DispatchQueue.main.sync {
            handle.manager.startMonitoring()
        }
    }
}

/// Stop monitoring for USB device connections.
@_cdecl("btusb_stop_monitoring")
public func btusb_stop_monitoring(_ rawHandle: UnsafeMutableRawPointer) {
    let handle = Unmanaged<ManagedHandle>.fromOpaque(rawHandle).takeUnretainedValue()
    if Thread.isMainThread {
        handle.manager.stopMonitoring()
    } else {
        DispatchQueue.main.sync {
            handle.manager.stopMonitoring()
        }
    }
}

/// Returns 1 if the USB device is connected (interface seized, endpoints ready), 0 otherwise.
@_cdecl("btusb_is_connected")
public func btusb_is_connected(_ rawHandle: UnsafeMutableRawPointer) -> Int32 {
    let handle = Unmanaged<ManagedHandle>.fromOpaque(rawHandle).takeUnretainedValue()
    return handle.manager.isConnected ? 1 : 0
}

/// Returns 1 if a matching USB device is present in the IOKit registry, 0 otherwise.
@_cdecl("btusb_is_device_present")
public func btusb_is_device_present(_ rawHandle: UnsafeMutableRawPointer) -> Int32 {
    let handle = Unmanaged<ManagedHandle>.fromOpaque(rawHandle).takeUnretainedValue()
    return handle.manager.isDevicePresent ? 1 : 0
}

/// Send raw bytes via USB bulk OUT transfer. Returns 1 on success, 0 on failure.
@_cdecl("btusb_send")
public func btusb_send(_ rawHandle: UnsafeMutableRawPointer, _ data: UnsafePointer<UInt8>, _ length: Int32) -> Int32 {
    let handle = Unmanaged<ManagedHandle>.fromOpaque(rawHandle).takeUnretainedValue()
    let bytes = Array(UnsafeBufferPointer(start: data, count: Int(length)))
    return handle.manager.sendData(bytes) ? 1 : 0
}

/// Send a string as an AT command (appends \r\n). Returns 1 on success, 0 on failure.
@_cdecl("btusb_send_string")
public func btusb_send_string(_ rawHandle: UnsafeMutableRawPointer, _ str: UnsafePointer<CChar>) -> Int32 {
    let handle = Unmanaged<ManagedHandle>.fromOpaque(rawHandle).takeUnretainedValue()
    let command = String(cString: str) + "\r\n"
    let data = Array(command.utf8)
    return handle.manager.sendData(data) ? 1 : 0
}

/// Register a callback for received data from USB bulk IN transfers.
/// callback: (data_ptr, length, context) -> void
@_cdecl("btusb_set_data_callback")
public func btusb_set_data_callback(
    _ rawHandle: UnsafeMutableRawPointer,
    _ callback: BTUSBDataCallback?,
    _ context: UnsafeMutableRawPointer?
) {
    let handle = Unmanaged<ManagedHandle>.fromOpaque(rawHandle).takeUnretainedValue()
    handle.bridgeDelegate.dataCallback = callback
    handle.bridgeDelegate.dataContext = context
}

/// Register a callback for connection state changes.
/// callback: (connected_state, context) -> void
/// connected_state: 1 = connected, 0 = disconnected, -1 = reconnect failed
@_cdecl("btusb_set_connection_callback")
public func btusb_set_connection_callback(
    _ rawHandle: UnsafeMutableRawPointer,
    _ callback: BTUSBConnectionCallback?,
    _ context: UnsafeMutableRawPointer?
) {
    let handle = Unmanaged<ManagedHandle>.fromOpaque(rawHandle).takeUnretainedValue()
    handle.bridgeDelegate.connectionCallback = callback
    handle.bridgeDelegate.connectionContext = context
}

/// Register a callback for log messages.
/// callback: (message_cstr, context) -> void
@_cdecl("btusb_set_log_callback")
public func btusb_set_log_callback(
    _ rawHandle: UnsafeMutableRawPointer,
    _ callback: BTUSBLogCallback?,
    _ context: UnsafeMutableRawPointer?
) {
    let handle = Unmanaged<ManagedHandle>.fromOpaque(rawHandle).takeUnretainedValue()
    handle.bridgeDelegate.logCallback = callback
    handle.bridgeDelegate.logContext = context
}
