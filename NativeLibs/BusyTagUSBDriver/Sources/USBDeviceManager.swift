import Foundation
import IOKit
import IOKit.usb

// MARK: - IOKit UUID Constants
// These C preprocessor macros from IOUSBLib.h and IOCFPlugIn.h are not
// auto-bridged to Swift. We define them with exact UUID bytes from the SDK.

// 2D9786C6-9EF3-11D4-AD51-000A27052861
private let kUSBInterfaceUserClientUUID = CFUUIDGetConstantUUIDWithBytes(nil,
    0x2d, 0x97, 0x86, 0xc6, 0x9e, 0xf3, 0x11, 0xD4,
    0xad, 0x51, 0x00, 0x0a, 0x27, 0x05, 0x28, 0x61)

// C244E858-109C-11D4-91D4-0050E4C6426F
private let kIOCFPlugInUUID = CFUUIDGetConstantUUIDWithBytes(nil,
    0xC2, 0x44, 0xE8, 0x58, 0x10, 0x9C, 0x11, 0xD4,
    0x91, 0xD4, 0x00, 0x50, 0xE4, 0xC6, 0x42, 0x6F)

// 8FDB8455-74A6-11D6-97B1-003065D3608E  (IOUSBInterfaceInterface190 — has USBInterfaceOpenSeize)
private let kUSBInterfaceInterface190UUID = CFUUIDGetConstantUUIDWithBytes(nil,
    0x8f, 0xdb, 0x84, 0x55, 0x74, 0xa6, 0x11, 0xD6,
    0x97, 0xb1, 0x00, 0x30, 0x65, 0xd3, 0x60, 0x8e)

// MARK: - USBDeviceManager Delegate

protocol USBDeviceManagerDelegate: AnyObject {
    func usbManagerDidConnect(_ manager: USBDeviceManager)
    func usbManagerDidDisconnect(_ manager: USBDeviceManager)
    func usbManagerReconnecting(_ manager: USBDeviceManager, attempt: Int)
    func usbManagerReconnectFailed(_ manager: USBDeviceManager)
    func usbManager(_ manager: USBDeviceManager, didReceiveData data: [UInt8])
    func usbManager(_ manager: USBDeviceManager, didLog message: String)
}

// MARK: - USB Device Manager

class USBDeviceManager {
    weak var delegate: USBDeviceManagerDelegate?
    private(set) var isConnected = false
    private(set) var isDevicePresent = false

    // COM interface pointer (IOUSBInterfaceInterface190**), stored as raw
    private var interfaceRef: UnsafeMutableRawPointer?

    // Bulk endpoint pipes
    private var bulkInPipe: UInt8 = 0
    private var bulkOutPipe: UInt8 = 0

    // Async read buffer
    private var readBuffer: UnsafeMutablePointer<UInt8>?
    private let readBufferSize: UInt32 = 4096

    // Async run loop source — runs on dedicated IO thread, not main thread
    private var asyncRunLoopSource: CFRunLoopSource?
    private var ioThread: Thread?
    private var ioRunLoop: CFRunLoop?
    private let ioRunLoopReady = DispatchSemaphore(value: 0)

    // Device monitoring
    private var notifyPort: IONotificationPortRef?
    private var arrivalIterator: io_iterator_t = 0
    private var removalIterator: io_iterator_t = 0

    // Reconnect
    private var reconnectTimer: Timer?
    private var reconnectAttempts = 0
    private let maxReconnectAttempts = 30  // ~30 seconds

    let vendorID: Int = 0x303A   // 12346
    let productID: Int = 0x81DF  // 33247

    deinit {
        cancelReconnect()
        disconnect()
        stopMonitoring()
        stopIOThread()
    }

    private func log(_ message: String) {
        delegate?.usbManager(self, didLog: message)
    }

    // MARK: - COM Vtable Access

    /// Double-dereference the COM pointer to get the IOUSBInterfaceInterface190 vtable.
    private var vtable: IOUSBInterfaceInterface190 {
        interfaceRef!
            .assumingMemoryBound(to: UnsafeMutablePointer<IOUSBInterfaceInterface190>.self)
            .pointee.pointee
    }

    // MARK: - Device Monitoring

    func startMonitoring() {
        notifyPort = IONotificationPortCreate(kIOMainPortDefault)
        guard let port = notifyPort else {
            log("ERROR: Could not create notification port")
            return
        }

        let runLoopSource = IONotificationPortGetRunLoopSource(port).takeUnretainedValue()
        CFRunLoopAddSource(CFRunLoopGetMain(), runLoopSource, .defaultMode)

        // Match on USB device by VID/PID
        guard let matching = IOServiceMatching(kIOUSBDeviceClassName) as NSMutableDictionary? else {
            log("ERROR: Could not create matching dictionary")
            return
        }
        matching[kUSBVendorID] = vendorID
        matching[kUSBProductID] = productID

        let selfPtr = Unmanaged.passUnretained(self).toOpaque()

        // IOKit consumes the matching dict, so we need separate copies
        guard let matchArrival = matching.copy() as? NSDictionary,
              let matchRemoval = matching.copy() as? NSDictionary else { return }

        // Watch for device arrival
        let krAdd = IOServiceAddMatchingNotification(
            port,
            kIOFirstMatchNotification,
            matchArrival,
            usbInterfaceArrivedCallback,
            selfPtr,
            &arrivalIterator
        )
        if krAdd == KERN_SUCCESS {
            drainIterator(arrivalIterator, arrived: true)
        } else {
            log("WARNING: Arrival notification registration failed: 0x\(String(format: "%x", krAdd))")
        }

        // Watch for device removal
        let krRemove = IOServiceAddMatchingNotification(
            port,
            kIOTerminatedNotification,
            matchRemoval,
            usbInterfaceRemovedCallback,
            selfPtr,
            &removalIterator
        )
        if krRemove == KERN_SUCCESS {
            drainIterator(removalIterator, arrived: false)
        } else {
            log("WARNING: Removal notification registration failed: 0x\(String(format: "%x", krRemove))")
        }

        log("Monitoring for USB device VID:0x\(String(format: "%04X", vendorID)) PID:0x\(String(format: "%04X", productID))")
    }

    func stopMonitoring() {
        if arrivalIterator != 0 { IOObjectRelease(arrivalIterator); arrivalIterator = 0 }
        if removalIterator != 0 { IOObjectRelease(removalIterator); removalIterator = 0 }
        if let port = notifyPort { IONotificationPortDestroy(port); notifyPort = nil }
    }

    fileprivate func drainIterator(_ iterator: io_iterator_t, arrived: Bool) {
        while case let service = IOIteratorNext(iterator), service != 0 {
            if arrived {
                isDevicePresent = true
                if !isConnected {
                    cancelReconnect()
                    log("USB device appeared — looking for CDC Data interface...")
                    attemptConnection(device: service)
                }
            } else {
                isDevicePresent = false
                if isConnected {
                    log("USB device removed")
                    disconnect()
                }
            }
            IOObjectRelease(service)
        }
    }

    private func attemptConnection(device: io_service_t) {
        if let cdcInterface = findCDCDataInterface(device: device) {
            log("Found CDC Data interface — attempting to claim...")
            connectToInterface(service: cdcInterface)
            IOObjectRelease(cdcInterface)

            if isConnected {
                reconnectAttempts = 0
                return
            }
        }

        // Connection failed — device may still be initializing after reset
        reconnectAttempts += 1
        if reconnectAttempts <= maxReconnectAttempts {
            log("Connection attempt \(reconnectAttempts) failed — retrying in 1s...")
            delegate?.usbManagerReconnecting(self, attempt: reconnectAttempts)
            scheduleReconnect()
        } else {
            log("Gave up reconnecting after \(maxReconnectAttempts) attempts")
            reconnectAttempts = 0
            delegate?.usbManagerReconnectFailed(self)
        }
    }

    private func scheduleReconnect() {
        cancelReconnect()
        reconnectTimer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: false) { [weak self] _ in
            guard let self = self, !self.isConnected else { return }
            self.retryConnection()
        }
    }

    private func retryConnection() {
        // Look for the device fresh from the IOKit registry
        guard let matching = IOServiceMatching(kIOUSBDeviceClassName) as NSMutableDictionary? else { return }
        matching[kUSBVendorID] = vendorID
        matching[kUSBProductID] = productID

        let service = IOServiceGetMatchingService(kIOMainPortDefault, matching)
        guard service != 0 else {
            // Device not in registry yet — schedule another retry
            reconnectAttempts += 1
            if reconnectAttempts <= maxReconnectAttempts {
                log("Device not found in registry — retry \(reconnectAttempts)/\(maxReconnectAttempts)...")
                delegate?.usbManagerReconnecting(self, attempt: reconnectAttempts)
                scheduleReconnect()
            } else {
                log("Gave up reconnecting after \(maxReconnectAttempts) attempts")
                reconnectAttempts = 0
                delegate?.usbManagerReconnectFailed(self)
            }
            return
        }

        attemptConnection(device: service)
        IOObjectRelease(service)
    }

    func cancelReconnect() {
        reconnectTimer?.invalidate()
        reconnectTimer = nil
    }

    /// Walk the device's children to find the IOUSBHostInterface with bInterfaceClass=10 (CDC Data)
    private func findCDCDataInterface(device: io_service_t) -> io_service_t? {
        var childIterator: io_iterator_t = 0
        guard IORegistryEntryGetChildIterator(device, kIOServicePlane, &childIterator) == KERN_SUCCESS else {
            return nil
        }

        var child = IOIteratorNext(childIterator)
        while child != 0 {
            if let prop = IORegistryEntryCreateCFProperty(child, "bInterfaceClass" as CFString, kCFAllocatorDefault, 0) {
                let ifClass = (prop.takeRetainedValue() as? NSNumber)?.intValue ?? -1
                if ifClass == 10 {  // CDC Data
                    IOObjectRelease(childIterator)
                    return child  // caller must release
                }
            }
            IOObjectRelease(child)
            child = IOIteratorNext(childIterator)
        }
        IOObjectRelease(childIterator)
        return nil
    }

    // MARK: - Connection

    private func connectToInterface(service: io_service_t) {
        log("Creating IOKit plugin interface...")

        // Step 1: Create plugin interface for the IOUSBHostInterface service
        var pluginInterface: UnsafeMutablePointer<UnsafeMutablePointer<IOCFPlugInInterface>?>?
        var score: Int32 = 0

        let kr = IOCreatePlugInInterfaceForService(
            service,
            kUSBInterfaceUserClientUUID,
            kIOCFPlugInUUID,
            &pluginInterface,
            &score
        )

        guard kr == KERN_SUCCESS, let plugin = pluginInterface, plugin.pointee != nil else {
            log("ERROR: IOCreatePlugInInterfaceForService failed: 0x\(String(format: "%x", kr))")
            if kr == kIOReturnNotPermitted {
                log("  Sandbox may be blocking USB access. Try disabling App Sandbox.")
            }
            return
        }

        // Step 2: QueryInterface for IOUSBInterfaceInterface190
        var result: UnsafeMutableRawPointer?
        let uuidBytes = CFUUIDGetUUIDBytes(kUSBInterfaceInterface190UUID)

        let hr = plugin.pointee!.pointee.QueryInterface(
            UnsafeMutableRawPointer(plugin),
            uuidBytes,
            &result
        )

        // Release plugin interface (QueryInterface AddRef'd the result)
        _ = plugin.pointee!.pointee.Release(UnsafeMutableRawPointer(plugin))

        guard hr == 0 /* S_OK */, let rawInterface = result else {
            log("ERROR: QueryInterface for IOUSBInterfaceInterface190 failed: 0x\(String(format: "%x", hr))")
            return
        }

        interfaceRef = rawInterface

        // Step 3: Seize the interface from Apple's CDC ACM driver
        log("Seizing interface from Apple's CDC ACM driver...")
        let openResult = vtable.USBInterfaceOpenSeize!(interfaceRef!)

        guard openResult == kIOReturnSuccess else {
            log("ERROR: USBInterfaceOpenSeize failed: 0x\(String(format: "%x", openResult))")
            releaseInterface()
            return
        }

        log("Interface seized successfully!")

        // Step 4: Set up async event source for ReadPipeAsync callbacks
        setupAsyncEventSource()

        // Step 5: Find bulk IN/OUT endpoints
        guard findEndpoints() else {
            log("ERROR: Could not find bulk IN/OUT endpoints")
            closeAndRelease()
            return
        }

        isConnected = true
        delegate?.usbManagerDidConnect(self)

        // Step 6: Start async reading
        startReading()
    }

    // MARK: - Dedicated IO Thread

    /// Start a background thread with its own CFRunLoop for USB async callbacks.
    /// This prevents IOKit read callbacks from being blocked by main thread UI work.
    private func startIOThread() {
        guard ioThread == nil else { return }
        let thread = Thread { [weak self] in
            guard let self = self else { return }
            self.ioRunLoop = CFRunLoopGetCurrent()

            // Add a dummy source to keep the run loop alive until the real source is added
            var ctx = CFRunLoopSourceContext()
            if let keepAlive = CFRunLoopSourceCreate(nil, 0, &ctx) {
                CFRunLoopAddSource(CFRunLoopGetCurrent(), keepAlive, .defaultMode)
            }

            self.ioRunLoopReady.signal()
            CFRunLoopRun()
            self.ioRunLoop = nil
        }
        thread.name = "BusyTagUSBDriver.IO"
        thread.qualityOfService = .userInteractive
        thread.start()
        ioThread = thread
        ioRunLoopReady.wait()
    }

    private func stopIOThread() {
        if let runLoop = ioRunLoop {
            CFRunLoopStop(runLoop)
        }
        ioThread = nil
        ioRunLoop = nil
    }

    private func setupAsyncEventSource() {
        guard interfaceRef != nil else { return }

        // Ensure IO thread is running
        startIOThread()
        guard let runLoop = ioRunLoop else {
            log("WARNING: IO thread run loop not available")
            return
        }

        // Create the mach port for async notifications
        var port: mach_port_t = 0
        let portResult = vtable.CreateInterfaceAsyncPort!(interfaceRef!, &port)

        guard portResult == kIOReturnSuccess else {
            log("WARNING: CreateInterfaceAsyncPort failed: 0x\(String(format: "%x", portResult))")
            return
        }

        // Create a CFRunLoopSource from the mach port
        guard let machPort = CFMachPortCreateWithPort(nil, port, nil, nil, nil) else {
            log("WARNING: CFMachPortCreateWithPort failed")
            return
        }

        let source = CFMachPortCreateRunLoopSource(nil, machPort, 0)
        asyncRunLoopSource = source
        CFRunLoopAddSource(runLoop, source, .defaultMode)
        log("Async event source added to dedicated IO thread")
    }

    private func findEndpoints() -> Bool {
        guard interfaceRef != nil else { return false }

        var numEndpoints: UInt8 = 0
        let result = vtable.GetNumEndpoints!(interfaceRef!, &numEndpoints)

        guard result == kIOReturnSuccess else {
            log("ERROR: GetNumEndpoints failed: 0x\(String(format: "%x", result))")
            return false
        }

        log("Interface has \(numEndpoints) endpoints")

        bulkInPipe = 0
        bulkOutPipe = 0

        for pipeRef: UInt8 in 1...numEndpoints {
            var direction: UInt8 = 0
            var number: UInt8 = 0
            var transferType: UInt8 = 0
            var maxPacketSize: UInt16 = 0
            var interval: UInt8 = 0

            let pipeResult = vtable.GetPipeProperties!(
                interfaceRef!, pipeRef,
                &direction, &number, &transferType, &maxPacketSize, &interval
            )

            guard pipeResult == kIOReturnSuccess else { continue }

            let dirStr = direction == 1 ? "IN" : "OUT"
            let typeStr: String
            switch transferType {
            case 0: typeStr = "Control"
            case 1: typeStr = "Isochronous"
            case 2: typeStr = "Bulk"
            case 3: typeStr = "Interrupt"
            default: typeStr = "Unknown(\(transferType))"
            }
            log("  Pipe \(pipeRef): \(dirStr) \(typeStr) maxPacket=\(maxPacketSize)")

            // Bulk = 2, IN = 1, OUT = 0
            if transferType == 2 {
                if direction == 1 && bulkInPipe == 0 {
                    bulkInPipe = pipeRef
                } else if direction == 0 && bulkOutPipe == 0 {
                    bulkOutPipe = pipeRef
                }
            }
        }

        guard bulkInPipe != 0 && bulkOutPipe != 0 else {
            log("ERROR: Missing bulk endpoints (IN=\(bulkInPipe), OUT=\(bulkOutPipe))")
            return false
        }

        log("Bulk IN pipe=\(bulkInPipe), Bulk OUT pipe=\(bulkOutPipe)")
        return true
    }

    // MARK: - Data Transfer

    func sendData(_ bytes: [UInt8]) -> Bool {
        guard interfaceRef != nil, isConnected, bulkOutPipe != 0 else {
            log("ERROR: Not connected — cannot send data")
            return false
        }

        let result = bytes.withUnsafeBufferPointer { buffer -> IOReturn in
            vtable.WritePipe!(
                interfaceRef!,
                bulkOutPipe,
                UnsafeMutableRawPointer(mutating: buffer.baseAddress!),
                UInt32(buffer.count)
            )
        }

        if result == kIOReturnSuccess {
            return true
        } else {
            log("ERROR: WritePipe failed: 0x\(String(format: "%x", result))")
            return false
        }
    }

    func startReading() {
        guard interfaceRef != nil, isConnected, bulkInPipe != 0 else { return }

        if readBuffer == nil {
            readBuffer = .allocate(capacity: Int(readBufferSize))
        }

        let selfPtr = Unmanaged.passUnretained(self).toOpaque()

        let result = vtable.ReadPipeAsync!(
            interfaceRef!,
            bulkInPipe,
            readBuffer!,
            readBufferSize,
            usbReadCompletionCallback,
            selfPtr
        )

        if result != kIOReturnSuccess {
            log("ERROR: ReadPipeAsync failed: 0x\(String(format: "%x", result))")
        }
    }

    fileprivate func handleReadCompletion(result: IOReturn, bytesRead: UInt32) {
        if result == kIOReturnSuccess && bytesRead > 0 {
            let data = Array(UnsafeBufferPointer(start: readBuffer!, count: Int(bytesRead)))
            delegate?.usbManager(self, didReceiveData: data)
        } else if result == kIOReturnAborted || result == kIOReturnNotResponding || result == kIOReturnNoDevice {
            // Device disconnected or removed
            if isConnected {
                log("USB read terminated (0x\(String(format: "%x", result))) — device disconnected")
                disconnect()
            }
            return
        } else if result != kIOReturnSuccess {
            log("Read error: 0x\(String(format: "%x", result))")
            if isConnected {
                disconnect()
            }
            return
        }

        // Re-arm the async read
        if isConnected {
            startReading()
        }
    }

    // MARK: - Cleanup

    func disconnect() {
        let wasConnected = isConnected
        isConnected = false
        closeAndRelease()
        if wasConnected {
            delegate?.usbManagerDidDisconnect(self)
        }
    }

    private func closeAndRelease() {
        if interfaceRef != nil {
            _ = vtable.USBInterfaceClose!(interfaceRef!)
        }
        if let source = asyncRunLoopSource, let runLoop = ioRunLoop {
            CFRunLoopRemoveSource(runLoop, source, .defaultMode)
            asyncRunLoopSource = nil
        }
        stopIOThread()
        releaseInterface()
    }

    private func releaseInterface() {
        if let ref = interfaceRef {
            let ptr = ref.assumingMemoryBound(
                to: UnsafeMutablePointer<IOUSBInterfaceInterface190>.self)
            _ = ptr.pointee.pointee.Release(ref)
            interfaceRef = nil
        }
        if let buf = readBuffer {
            buf.deallocate()
            readBuffer = nil
        }
        bulkInPipe = 0
        bulkOutPipe = 0
    }
}

// MARK: - C Callbacks (IOKit requires free functions, not closures)

private func usbInterfaceArrivedCallback(refcon: UnsafeMutableRawPointer?, iterator: io_iterator_t) {
    guard let refcon = refcon else { return }
    let manager = Unmanaged<USBDeviceManager>.fromOpaque(refcon).takeUnretainedValue()
    manager.drainIterator(iterator, arrived: true)
}

private func usbInterfaceRemovedCallback(refcon: UnsafeMutableRawPointer?, iterator: io_iterator_t) {
    guard let refcon = refcon else { return }
    let manager = Unmanaged<USBDeviceManager>.fromOpaque(refcon).takeUnretainedValue()
    manager.drainIterator(iterator, arrived: false)
}

private func usbReadCompletionCallback(refcon: UnsafeMutableRawPointer?, result: IOReturn, arg0: UnsafeMutableRawPointer?) {
    guard let refcon = refcon else { return }
    let manager = Unmanaged<USBDeviceManager>.fromOpaque(refcon).takeUnretainedValue()
    // arg0 carries the byte count packed as a pointer-sized value
    let bytesRead = UInt32(UInt(bitPattern: arg0))
    manager.handleReadCompletion(result: result, bytesRead: bytesRead)
}
