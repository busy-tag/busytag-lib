using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace BusyTag.Lib;

public class EspToolRunner
{
    public event EventHandler<string>? OutputReceived;

    private readonly string _esptoolPath;

    public EspToolRunner(string? esptoolPath = null)
    {
        _esptoolPath = esptoolPath ?? FindEsptool()
            ?? throw new FileNotFoundException(
                "esptool not found. Install ESP-IDF, run 'pip install esptool', " +
                "or specify path with --esptool <path>.");
    }

    /// <summary>
    /// Locates esptool executable. Search order:
    /// 1. Same directory as CLI executable
    /// 2. In PATH
    /// 3. ESP-IDF venv paths
    /// </summary>
    public static string? FindEsptool()
    {
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var exeName = isWindows ? "esptool.exe" : "esptool";

        // 1. Bundled: platform-specific Tools subdirectory
        var appDir = AppContext.BaseDirectory;

        // Check platform-specific paths, prioritizing the current platform.
        // Mac Catalyst reports as iOS (not macOS) at runtime, so we can't rely on
        // RuntimeInformation.IsOSPlatform(OSPlatform.OSX). Instead, check the
        // correct platform first based on what we can detect reliably.
        if (isWindows)
        {
            var winPath = Path.Combine(appDir, "Tools", "win", "esptool.exe");
            if (File.Exists(winPath)) return winPath;
        }
        else
        {
            // On non-Windows (macOS, Mac Catalyst, Linux): check macOS first, then Linux
            var macPath = Path.Combine(appDir, "Tools", "macos", "esptool");
            if (File.Exists(macPath)) return macPath;

            var linuxPath = Path.Combine(appDir, "Tools", "linux", "esptool");
            if (File.Exists(linuxPath)) return linuxPath;

            // Mac Catalyst app bundle: AppContext.BaseDirectory may point to Contents/MacOS/
            // but esptool is in Contents/MonoBundle/Tools/macos/. Walk up and check MonoBundle.
            var contentsDir = Path.GetDirectoryName(appDir.TrimEnd(Path.DirectorySeparatorChar));
            if (contentsDir != null)
            {
                var monoBundlePath = Path.Combine(contentsDir, "MonoBundle", "Tools", "macos", "esptool");
                if (File.Exists(monoBundlePath)) return monoBundlePath;
                // Also check Resources (some bundle layouts)
                var resourcesPath = Path.Combine(contentsDir, "Resources", "Tools", "macos", "esptool");
                if (File.Exists(resourcesPath)) return resourcesPath;
            }
        }

        // Fallback: check app root
        var localPath = Path.Combine(appDir, exeName);
        if (File.Exists(localPath)) return localPath;

        // 2. Check PATH
        var pathResult = FindInPath(exeName);
        if (pathResult != null)
            return pathResult;

        if (!isWindows)
        {
            pathResult = FindInPath("esptool.py");
            if (pathResult != null)
                return pathResult;
        }

        // 3. ESP-IDF venv paths
        var espressifDir = GetEspressifDir();
        if (espressifDir != null && Directory.Exists(espressifDir))
        {
            var pythonEnvDir = Path.Combine(espressifDir, "python_env");
            if (Directory.Exists(pythonEnvDir))
            {
                var venvDirs = Directory.GetDirectories(pythonEnvDir)
                    .OrderByDescending(d => d)
                    .ToArray();

                foreach (var venvDir in venvDirs)
                {
                    string esptoolPath = isWindows
                        ? Path.Combine(venvDir, "Scripts", "esptool.exe")
                        : Path.Combine(venvDir, "bin", "esptool.py");

                    if (File.Exists(esptoolPath))
                        return esptoolPath;
                }
            }
        }

        return null;
    }

    private static string? GetEspressifDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var espDir = Path.Combine(home, ".espressif");
        return Directory.Exists(espDir) ? espDir : null;
    }

    private static string? FindInPath(string exeName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        foreach (var dir in pathVar.Split(separator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var fullPath = Path.Combine(dir.Trim(), exeName);
            if (File.Exists(fullPath))
                return fullPath;
        }
        return null;
    }

    /// <summary>
    /// Reads chip info from a device in boot mode.
    /// </summary>
    public async Task<string?> GetChipInfoAsync(string port, CancellationToken ct = default)
    {
        var args = $"--port {port} chip_id";
        var (exitCode, output) = await RunEspToolAsync(args, ct);
        return exitCode == 0 ? output : null;
    }

    /// <summary>
    /// Flashes all firmware components to the device.
    /// </summary>
    public async Task<bool> FlashFirmwareAsync(
        string port,
        FirmwarePackage firmware,
        IProgress<FlashProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        var args = $"--chip {firmware.Chip} --port {port} --baud 921600 " +
                   $"write_flash --flash_mode {firmware.FlashMode} --flash_freq {firmware.FlashFreq} " +
                   $"--flash_size {firmware.FlashSize} " +
                   $"0x0 \"{firmware.BootloaderPath}\" " +
                   $"0x8000 \"{firmware.PartitionTablePath}\" " +
                   $"0x10000 \"{firmware.ApplicationPath}\" " +
                   $"0x210000 \"{firmware.OtaDataPath}\"";

        var (exitCode, _) = await RunEspToolAsync(args, ct, progress);
        return exitCode == 0;
    }

    /// <summary>
    /// Erases the entire flash.
    /// </summary>
    public async Task<bool> EraseFlashAsync(string port, CancellationToken ct = default)
    {
        var args = $"--chip esp32s3 --port {port} erase_flash";
        var (exitCode, _) = await RunEspToolAsync(args, ct);
        return exitCode == 0;
    }

    private async Task<(int exitCode, string output)> RunEspToolAsync(
        string arguments,
        CancellationToken ct,
        IProgress<FlashProgressInfo>? progress = null)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _esptoolPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        var outputBuilder = new System.Text.StringBuilder();
        var errorBuilder = new System.Text.StringBuilder();
        var progressRegex = new Regex(@"\((\d+)\s*%\)");

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            outputBuilder.AppendLine(e.Data);
            OutputReceived?.Invoke(this, e.Data);

            // Parse progress
            if (progress != null)
            {
                var match = progressRegex.Match(e.Data);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int pct))
                {
                    progress.Report(new FlashProgressInfo(pct, e.Data));
                }
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            errorBuilder.AppendLine(e.Data);
            OutputReceived?.Invoke(this, e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* ignore */ }
            throw;
        }

        if (process.ExitCode != 0)
        {
            var error = errorBuilder.ToString().Trim();
            if (!string.IsNullOrEmpty(error))
                OutputReceived?.Invoke(this, $"ERROR: {error}");
        }

        return (process.ExitCode, outputBuilder.ToString());
    }
}

public record FlashProgressInfo(int Percent, string StatusMessage);
