namespace BusyTag.Lib.Util.DevEventArgs;

public class UploadProgressArgs: EventArgs
{
    public string FileName { get; set; } = string.Empty;
    public float ProgressLevel  { get; set; }
}