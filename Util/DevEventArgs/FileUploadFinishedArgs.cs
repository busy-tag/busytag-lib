namespace BusyTag.Lib.Util.DevEventArgs;

public class FileUploadFinishedArgs : EventArgs
{
    public bool Success { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public UploadErrorType ErrorType { get; set; } = UploadErrorType.None;
}

public enum UploadErrorType
{
    None,
    FilenameToolong,
    InsufficientStorage,
    ConnectionLost,
    TransferInterrupted,
    DeviceError,
    Cancelled,
    Unknown
}