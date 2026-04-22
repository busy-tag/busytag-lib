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
            // On non-Windows (macOS, Mac Catalyst, Linux): check macOS first, then Linux.
            var macPath = Path.Combine(appDir, "Tools", "macos", "esptool");
            if (File.Exists(macPath)) return macPath;

            var linuxPath = Path.Combine(appDir, "Tools", "linux", "esptool");
            if (File.Exists(linuxPath)) return linuxPath;

            // Mac Catalyst app bundle: AppContext.BaseDirectory points to Contents/MonoBundle.
            // Walk up to Contents/ and check the standard Tools path.
            var contentsDir = Path.GetDirectoryName(appDir.TrimEnd(Path.DirectorySeparatorChar));
            if (contentsDir != null)
            {
                var monoBundlePath = Path.Combine(contentsDir, "MonoBundle", "Tools", "macos", "esptool");
                if (File.Exists(monoBundlePath)) return monoBundlePath;
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

        // 3. Known macOS install locations — checked explicitly because the
        //    App Store sandbox strips PATH to system dirs only, so FindInPath
        //    misses Homebrew (/opt/homebrew/bin) and pip-user installs.
        //    The sandbox also remaps HOME to the container, so we derive the
        //    real user home from Environment.UserName (/Users/<name>) instead.
        if (!isWindows)
        {
            foreach (var p in new[] { "/opt/homebrew/bin/esptool", "/usr/local/bin/esptool" })
                if (File.Exists(p)) return p;

            // Real macOS home — not the sandboxed container home
            var realHome = Path.Combine("/Users", Environment.UserName);

            // pip --user: ~/Library/Python/3.x/bin/esptool
            var libPython = Path.Combine(realHome, "Library", "Python");
            if (Directory.Exists(libPython))
            {
                foreach (var ver in Directory.GetDirectories(libPython).OrderByDescending(d => d))
                {
                    foreach (var name in new[] { "esptool", "esptool.py" })
                    {
                        var p = Path.Combine(ver, "bin", name);
                        if (File.Exists(p)) return p;
                    }
                }
            }

            // ~/.local/bin/esptool
            var localBin = Path.Combine(realHome, ".local", "bin", "esptool");
            if (File.Exists(localBin)) return localBin;
        }

        // 5. ESP-IDF venv paths
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
        var home = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.Combine("/Users", Environment.UserName);
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
    /// Tries to install esptool via pip3/pip (or python3 -m pip) and returns
    /// its path if the install succeeded. Also checks common user pip locations
    /// (e.g. ~/Library/Python/3.x/bin) that may not be on PATH.
    /// </summary>
    public static async Task<string?> InstallEsptoolAsync(CancellationToken ct = default)
    {
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        // Build priority list: pip first, then Homebrew (macOS), then python -m pip
        var candidates = new List<(string exe, string args)>();

        if (!isWindows)
        {
            // Fixed macOS paths checked first — the sandbox strips PATH so FindInPath
            // won't see Homebrew or user-installed binaries.
            foreach (var pip in new[] {
                "/opt/homebrew/bin/pip3", "/usr/local/bin/pip3", "/usr/bin/pip3" })
            {
                if (File.Exists(pip)) candidates.Add((pip, "install esptool"));
            }
        }

        foreach (var pip in new[] { "pip3", "pip" })
        {
            var p = FindInPath(pip);
            if (p != null) candidates.Add((p, "install esptool"));
        }

        if (!isWindows)
        {
            // Homebrew — works even without Python installed.
            foreach (var brew in new[] { "/opt/homebrew/bin/brew", "/usr/local/bin/brew" })
            {
                if (File.Exists(brew)) { candidates.Add((brew, "install esptool")); break; }
            }
            var brewInPath = FindInPath("brew");
            if (brewInPath != null) candidates.Add((brewInPath, "install esptool"));

            // Fixed paths for python3 as fallback
            foreach (var py in new[] {
                "/opt/homebrew/bin/python3", "/usr/local/bin/python3", "/usr/bin/python3" })
            {
                if (File.Exists(py)) candidates.Add((py, "-m pip install esptool"));
            }
        }

        foreach (var py in new[] { "python3", "python" })
        {
            var p = FindInPath(py);
            if (p != null) candidates.Add((p, "-m pip install esptool"));
        }

        foreach (var (exe, args) in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) continue;
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode != 0) continue;

                return FindEsptool() ?? FindEsptoolInKnownPaths(isWindows);
            }
            catch { /* try next candidate */ }
        }

        return null;
    }

    /// <summary>
    /// Checks pip/brew user install locations that may not be on the app's PATH.
    /// </summary>
    private static string? FindEsptoolInKnownPaths(bool isWindows)
    {
        var home = isWindows
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.Combine("/Users", Environment.UserName);

        if (isWindows)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var scriptsRoot = Path.Combine(appData, "Python");
            if (Directory.Exists(scriptsRoot))
            {
                foreach (var dir in Directory.GetDirectories(scriptsRoot).OrderByDescending(d => d))
                {
                    var p = Path.Combine(dir, "Scripts", "esptool.exe");
                    if (File.Exists(p)) return p;
                }
            }
        }
        else
        {
            // Homebrew install locations (Apple Silicon and Intel)
            foreach (var p in new[] { "/opt/homebrew/bin/esptool", "/usr/local/bin/esptool" })
                if (File.Exists(p)) return p;

            // pip --user: ~/Library/Python/3.x/bin/
            var libPython = Path.Combine(home, "Library", "Python");
            if (Directory.Exists(libPython))
            {
                foreach (var ver in Directory.GetDirectories(libPython).OrderByDescending(d => d))
                {
                    foreach (var name in new[] { "esptool", "esptool.py" })
                    {
                        var p = Path.Combine(ver, "bin", name);
                        if (File.Exists(p)) return p;
                    }
                }
            }

            // ~/.local/bin/esptool
            var localBin = Path.Combine(home, ".local", "bin", "esptool");
            if (File.Exists(localBin)) return localBin;
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
        // PyInstaller --onedir bundles find their _internal/ dir relative to WorkingDirectory.
        var workingDir = Path.GetDirectoryName(_esptoolPath) ?? AppContext.BaseDirectory;

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _esptoolPath,
                Arguments = arguments,
                WorkingDirectory = workingDir,
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

        Util.FileLogger.Log($"[esptool] Starting: {_esptoolPath} {arguments}");
        Util.FileLogger.Log($"[esptool] WorkingDirectory: {workingDir}");
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            Util.FileLogger.Log($"[esptool] Process.Start threw: {ex}");
            return (-1, "");
        }
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

        Util.FileLogger.Log($"[esptool] ExitCode={process.ExitCode}");
        if (process.ExitCode != 0)
        {
            var error = errorBuilder.ToString().Trim();
            Util.FileLogger.Log($"[esptool] stderr={error}");
            if (!string.IsNullOrEmpty(error))
                OutputReceived?.Invoke(this, $"ERROR: {error}");
        }

        return (process.ExitCode, outputBuilder.ToString());
    }
}

public record FlashProgressInfo(int Percent, string StatusMessage);
