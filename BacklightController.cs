using System.Diagnostics;

namespace ThinkPadBacklightTray;

/// <summary>
///     Controls keyboard backlight via the IBMPmDrv kernel driver.
/// </summary>
public static class BacklightController
{
    public enum BacklightLevel
    {
        Off = 0,
        Dim = 1,
        Full = 2
    }

    private static readonly object SyncRoot = new();
    private static readonly object IoSyncRoot = new();

    private static bool _initialized;
    private static bool _available;
    private static string _providerDetails = "Not initialized";
    private static PmDriverBacklightController? _pmDriverController;

    public static bool Initialize()
    {
        lock (SyncRoot)
        {
            if (_initialized) return _available;
            _initialized = true;

            try
            {
                if (PmDriverBacklightController.TryCreate(out var controller, out var summary) && controller != null)
                {
                    _pmDriverController = controller;
                    _providerDetails = summary;
                    _available = true;
                    Debug.WriteLine($"BacklightController: IBMPmDrv ready — {summary}");
                    return true;
                }

                _providerDetails = summary;
            }
            catch (Exception ex)
            {
                _providerDetails = $"IBMPmDrv exception: {ex.Message}";
                Debug.WriteLine(_providerDetails);
            }

            _available = false;
            Debug.WriteLine($"BacklightController: no provider — {_providerDetails}");
            return false;
        }
    }

    public static bool SetBacklightLevel(BacklightLevel level)
    {
        if (!_initialized) Initialize();
        PmDriverBacklightController? controller;
        lock (SyncRoot)
        {
            if (!_available || _pmDriverController == null) return false;
            controller = _pmDriverController;
        }

        const int maxRetries = 3;
        var delayMs = 100;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            bool ok;
            lock (IoSyncRoot)
            {
                ok = controller.SetBacklightLevel((int)level);
            }

            if (ok)
            {
                Debug.WriteLine($"Backlight set to {(int)level} (attempt {attempt})");
                return true;
            }

            if (attempt < maxRetries)
                Thread.Sleep(delayMs);
            delayMs *= 2;
        }

        Debug.WriteLine($"SetBacklightLevel({level}) failed after {maxRetries} attempts");
        return false;
    }

    public static BacklightLevel? GetBacklightLevel()
    {
        if (!_initialized) Initialize();
        PmDriverBacklightController? controller;
        lock (SyncRoot)
        {
            if (!_available || _pmDriverController == null) return null;
            controller = _pmDriverController;
        }

        bool ok;
        int level;
        lock (IoSyncRoot)
        {
            ok = controller.TryGetBacklightLevel(out level);
        }

        if (ok && level is >= 0 and <= 2)
            return (BacklightLevel)level;

        return null;
    }

    public static string GetProviderStatusSummary()
    {
        return
            $"Provider: {(_available ? "IBMPmDrv" : "None")}\n" +
            $"Initialized: {_initialized}\n" +
            $"Details: {_providerDetails}";
    }
}