// ReSharper disable CommentTypo
// ReSharper disable StringLiteralTypo

namespace BusyTag.Lib.Util;

public class SerialPortCommands
{
    public enum Commands
    {
        GetDeviceName,
        GetManufactureName,
        GetDeviceId,
        GetFirmwareVersion,
        // GetPictureList,
        // GetFileList,
        // GetLocalHostAddress,
        // GetFreeStorageSize,
        // GetTotalStorageSize,
        GetSolidColor,
        GetCustomPattern,
        GetDisplayBrightness,
        GetShowAfterDrop,
        GetAllowedWebServer,
        GetWifiConfig,
        GetUsbMassStorageActive,
        SetUsbMassStorageActive
    }

    private readonly Dictionary<Commands, string> _commandList = new()
    {
        { Commands.GetDeviceName, "AT+GDN\r\n" },
        { Commands.GetManufactureName, "AT+GMN\r\n" },
        { Commands.GetDeviceId, "AT+GID\r\n" },
        { Commands.GetFirmwareVersion, "AT+GFV\r\n" },
        // { Commands.GetPictureList, "AT+GPL\r\n" },
        // { Commands.GetFileList, "AT+GFL\r\n" },
        // { Commands.GetLocalHostAddress, "AT+GLHA\r\n" },
        // { Commands.GetFreeStorageSize, "AT+GFSS\r\n" },
        // { Commands.GetTotalStorageSize, "AT+GTSS\r\n" },
        { Commands.GetSolidColor, "AT+SC?\r\n" },
        { Commands.GetCustomPattern, "AT+CP?\r\n" },
        { Commands.GetDisplayBrightness, "AT+DB?\r\n" },
        { Commands.GetShowAfterDrop, "AT+SAD?\r\n" },
        { Commands.GetAllowedWebServer, "AT+AWFS?\r\n" },
        { Commands.GetWifiConfig, "AT+WC?\r\n" },
        { Commands.GetUsbMassStorageActive, "AT+UMSA?\r\n" },
        { Commands.SetUsbMassStorageActive, "AT+UMSA=1\r\n"}
    };

    // ReSharper disable once InconsistentNaming
    public string GetCommand(Commands var)
    {
        return _commandList[var];
    }
}