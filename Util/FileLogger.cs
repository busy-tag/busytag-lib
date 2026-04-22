namespace BusyTag.Lib.Util;

/// <summary>
/// Simple append-only file logger for diagnosing upload issues.
/// Log file: {TempPath}/busytag-lib.log  — readable after each run.
/// Call FileLogger.Clear() at app start to get a fresh log each session.
/// </summary>
public static class FileLogger
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "busytag-lib.log");

    private static readonly object _lock = new();

    public static string LogFilePath => LogPath;

    public static void Clear()
    {
        lock (_lock)
        {
            try { File.WriteAllText(LogPath, $"=== BusyTag lib log started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n"); }
            catch { /* ignore */ }
        }
    }

    public static void Log(string message)
    {
        lock (_lock)
        {
            try
            {
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
            }
            catch { /* ignore */ }
        }
    }
}
