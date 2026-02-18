// BusyTagUSBDriver.h
// C-compatible API for IOKit USB Bulk Transfer communication with BusyTag devices.
// This header documents the @_cdecl exports from the Swift framework.

#ifndef BusyTagUSBDriver_h
#define BusyTagUSBDriver_h

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// Opaque handle type
typedef void* btusb_handle_t;

// Callback types
typedef void (*btusb_data_callback_t)(const uint8_t* data, int32_t length, void* context);
typedef void (*btusb_connection_callback_t)(int32_t connected, void* context);
typedef void (*btusb_log_callback_t)(const char* message, void* context);

// Lifecycle
btusb_handle_t btusb_create(void);
void btusb_destroy(btusb_handle_t handle);

// Monitoring
void btusb_start_monitoring(btusb_handle_t handle);
void btusb_stop_monitoring(btusb_handle_t handle);

// State
int32_t btusb_is_connected(btusb_handle_t handle);
int32_t btusb_is_device_present(btusb_handle_t handle);

// Data transfer
int32_t btusb_send(btusb_handle_t handle, const uint8_t* data, int32_t length);
int32_t btusb_send_string(btusb_handle_t handle, const char* str);

// Callbacks
void btusb_set_data_callback(btusb_handle_t handle, btusb_data_callback_t callback, void* context);
void btusb_set_connection_callback(btusb_handle_t handle, btusb_connection_callback_t callback, void* context);
void btusb_set_log_callback(btusb_handle_t handle, btusb_log_callback_t callback, void* context);

#ifdef __cplusplus
}
#endif

#endif /* BusyTagUSBDriver_h */
