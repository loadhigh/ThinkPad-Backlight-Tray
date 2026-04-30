using System.Diagnostics;
using Microsoft.Win32;

namespace ThinkPadBacklightTray;

/// <summary>
///     Manages application settings stored in Windows Registry.
///     Root: HKCU\Software\ThinkPad-Backlight-Tray
/// </summary>
public static class SettingsManager
{
    private static readonly string RegistryPath = @"Software\ThinkPad-Backlight-Tray";
    private static readonly string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly string RunValueName = "ThinkPad-Backlight-Tray";
    private static readonly object SyncRoot = new();
    private static RegistryKey? _regKey;

    public static void Initialize()
    {
        try
        {
            lock (SyncRoot)
            {
                _regKey?.Dispose();
                _regKey = Registry.CurrentUser.CreateSubKey(RegistryPath);
            }
            Debug.WriteLine($"Registry key initialized: HKCU\\{RegistryPath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error initializing registry: {ex.Message}");
        }
    }

    private static void EnsureInitialized()
    {
        lock (SyncRoot)
        {
            if (_regKey == null)
                _regKey = Registry.CurrentUser.CreateSubKey(RegistryPath);
        }
    }

    public static void Shutdown()
    {
        lock (SyncRoot)
        {
            _regKey?.Dispose();
            _regKey = null;
        }
    }

    // ── registry helpers ──────────────────────

    private static int GetDword(string name, int defaultValue)
    {
        EnsureInitialized();
        try   { return Convert.ToInt32(_regKey?.GetValue(name, defaultValue)); }
        catch { return defaultValue; }
    }

    private static void SetDword(string name, int value)
    {
        EnsureInitialized();
        try
        {
            _regKey?.SetValue(name, value, RegistryValueKind.DWord);
            Debug.WriteLine($"Registry saved {name}={value}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error saving {name}: {ex.Message}");
        }
    }

    // ── settings accessors ────────────────────

    /// <summary>Get the saved backlight level (default: Full/2).</summary>
    public static int GetBacklightLevel() => GetDword("BacklightLevel", 2);

    /// <summary>Save the backlight level.</summary>
    public static void SetBacklightLevel(int level) => SetDword("BacklightLevel", level);

    /// <summary>Get whether automatic backlight restore on system events is enabled (default: true).</summary>
    public static bool GetAutoRestore() => GetDword("AutoRestore", 1) != 0;

    /// <summary>Save the auto-restore enabled state.</summary>
    public static void SetAutoRestore(bool enable) => SetDword("AutoRestore", enable ? 1 : 0);

    /// <summary>
    ///     Get the restore-to mode (default: 0 = Last).
    ///     0 = Last (use saved BacklightLevel), 1 = Dim, 2 = Full.
    /// </summary>
    public static int GetRestoreLevel()
    {
        var val = GetDword("RestoreLevel", 0);
        return val is >= 0 and <= 2 ? val : 0;
    }

    /// <summary>Set the restore-to mode. 0 = Last, 1 = Dim, 2 = Full.</summary>
    public static void SetRestoreLevel(int level) => SetDword("RestoreLevel", level);

    /// <summary>
    ///     Returns the backlight level that should be applied on restore.
    ///     When RestoreLevel is 0 (Last), returns the saved BacklightLevel;
    ///     otherwise returns the fixed RestoreLevel value (1 = Dim, 2 = Full).
    /// </summary>
    public static int GetEffectiveRestoreLevel()
    {
        var mode = GetRestoreLevel();
        return mode == 0 ? GetBacklightLevel() : mode;
    }

    /// <summary>Check whether the app is registered to run at user login.</summary>
    public static bool GetRunAtStartup()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunRegistryPath, false);
            return runKey?.GetValue(RunValueName) != null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error reading run-at-startup: {ex.Message}");
            return false;
        }
    }

    /// <summary>Register or unregister the app to run at user login.</summary>
    public static void SetRunAtStartup(bool enable)
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunRegistryPath, true);
            if (runKey == null) return;

            if (enable)
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    runKey.SetValue(RunValueName, $"\"{exePath}\"", RegistryValueKind.String);
                    Debug.WriteLine($"Run-at-startup enabled: {exePath}");
                }
            }
            else
            {
                runKey.DeleteValue(RunValueName, false);
                Debug.WriteLine("Run-at-startup disabled");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error setting run-at-startup: {ex.Message}");
        }
    }
}