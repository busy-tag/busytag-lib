using System.Reflection;

namespace BusyTag.Lib;

public class FirmwarePackage : IDisposable
{
    public required string BootloaderPath { get; init; }      // -> 0x0
    public required string PartitionTablePath { get; init; }  // -> 0x8000
    public required string ApplicationPath { get; init; }     // -> 0x10000
    public required string OtaDataPath { get; init; }         // -> 0x210000

    public string FlashMode { get; init; } = "dio";
    public string FlashFreq { get; init; } = "80m";
    public string FlashSize { get; init; } = "detect";
    public string Chip { get; init; } = "esp32s3";

    private string? _tempDir;

    /// <summary>
    /// Creates a FirmwarePackage from embedded resources extracted to a temp directory.
    /// </summary>
    public static FirmwarePackage FromEmbeddedResources()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var tempDir = Path.Combine(Path.GetTempPath(), $"busytag-firmware-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        ExtractResource(assembly, "BusyTag.Lib.Firmware.bootloader.bin", Path.Combine(tempDir, "bootloader.bin"));
        ExtractResource(assembly, "BusyTag.Lib.Firmware.partition-table.bin", Path.Combine(tempDir, "partition-table.bin"));
        ExtractResource(assembly, "BusyTag.Lib.Firmware.file_server.bin", Path.Combine(tempDir, "file_server.bin"));
        ExtractResource(assembly, "BusyTag.Lib.Firmware.ota_data_initial.bin", Path.Combine(tempDir, "ota_data_initial.bin"));

        return new FirmwarePackage
        {
            BootloaderPath = Path.Combine(tempDir, "bootloader.bin"),
            PartitionTablePath = Path.Combine(tempDir, "partition-table.bin"),
            ApplicationPath = Path.Combine(tempDir, "file_server.bin"),
            OtaDataPath = Path.Combine(tempDir, "ota_data_initial.bin"),
            _tempDir = tempDir
        };
    }

    /// <summary>
    /// Creates a FirmwarePackage from a directory containing the firmware binaries.
    /// Looks for the standard ESP-IDF build output structure.
    /// </summary>
    public static FirmwarePackage FromDirectory(string buildDir)
    {
        return new FirmwarePackage
        {
            BootloaderPath = FindFile(buildDir, "bootloader.bin", "bootloader"),
            PartitionTablePath = FindFile(buildDir, "partition-table.bin", "partition_table"),
            ApplicationPath = FindFile(buildDir, "file_server.bin"),
            OtaDataPath = FindFile(buildDir, "ota_data_initial.bin")
        };
    }

    /// <summary>
    /// Validates that all files exist and have reasonable sizes.
    /// </summary>
    public bool Validate(out string? error)
    {
        var files = new (string Path, string Name, long MinSize)[]
        {
            (BootloaderPath, "bootloader.bin", 1024),
            (PartitionTablePath, "partition-table.bin", 512),
            (ApplicationPath, "file_server.bin", 100_000),
            (OtaDataPath, "ota_data_initial.bin", 1024)
        };

        foreach (var (path, name, minSize) in files)
        {
            if (!File.Exists(path))
            {
                error = $"Firmware file not found: {name} (expected at {path})";
                return false;
            }

            var size = new FileInfo(path).Length;
            if (size < minSize)
            {
                error = $"Firmware file {name} is too small ({size} bytes, expected at least {minSize})";
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Returns a summary of the firmware package for display.
    /// </summary>
    public string GetSummary()
    {
        var lines = new[]
        {
            $"  Bootloader bin:       ({FormatSize(BootloaderPath)}) -> 0x0",
            $"  Partition Table bin:  ({FormatSize(PartitionTablePath)}) -> 0x8000",
            $"  Application bin:      ({FormatSize(ApplicationPath)}) -> 0x10000",
            $"  OTA Data bin:         ({FormatSize(OtaDataPath)}) -> 0x210000"
        };
        return string.Join(Environment.NewLine, lines);
    }

    public bool HasEmbeddedResources()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var names = assembly.GetManifestResourceNames();
        return names.Any(n => n.Contains("Firmware.bootloader"));
    }

    private static string FormatSize(string path)
    {
        if (!File.Exists(path)) return "???";
        var size = new FileInfo(path).Length;
        return size switch
        {
            >= 1_048_576 => $"{size / 1_048_576.0:F1} MB",
            >= 1024 => $"{size / 1024.0:F1} KB",
            _ => $"{size} B"
        };
    }

    private static string FindFile(string baseDir, string fileName, string? subDir = null)
    {
        // Try a direct path first
        var direct = Path.Combine(baseDir, fileName);
        if (File.Exists(direct))
            return direct;

        // Try subdirectory
        if (subDir != null)
        {
            var subPath = Path.Combine(baseDir, subDir, fileName);
            if (File.Exists(subPath))
                return subPath;
        }

        // Return direct path (will fail validation)
        return direct;
    }

    private static void ExtractResource(Assembly assembly, string resourceName, string outputPath)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded firmware resource not found: {resourceName}. " +
                "Use --firmware-dir to specify firmware directory instead.");

        using var fileStream = File.Create(outputPath);
        stream.CopyTo(fileStream);
    }

    public void Dispose()
    {
        if (_tempDir != null && Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
        }
    }
}
