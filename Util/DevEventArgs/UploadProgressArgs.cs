namespace BusyTag.Lib.Util.DevEventArgs;

public class UploadProgressArgs: EventArgs
{
    public string FileName = null!;
    public float ProgressLevel;
}