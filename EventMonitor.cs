using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Timer = System.Threading.Timer;

namespace ThinkPadBacklightTray;

/// <summary>
///     Monitors system events that should trigger backlight restoration
/// </summary>
public class EventMonitor : IDisposable
{
    public delegate void BacklightRestoreEventHandler();

    // Suppress Fn+Space level-change events for this long after any restore trigger,
    // as a safety margin against spurious hardware-reset notifications.
    private const int FnSpaceSuppressMs = 5000;

    // Debounce window for WMI restore triggers — collapses a burst of noisy
    // events (e.g. docking, USB plug-in) into a single RestoreBacklight call.
    private const int WmiDebouncePeriodMs = 800;

    // After resume, hold off ALL restore triggers for this long so the IBMPmDrv
    // driver has time to re-sync.  Only the dedicated resume restore fires.
    private const int ResumeHoldoffMs = 5000;

    private const int SM_CMONITORS = 80;

    private Timer? _displayMonitorTimer;
    private ManualResetEvent? _fnSpaceStop;
    private Thread? _fnSpaceThread;
    private uint _lastDisplayState;

    // Ticks after which non-resume restore triggers are allowed again.
    private long _resumeHoldoffUntil;

    // One-shot timer for the post-resume restore (avoids blocking a thread pool thread).
    private Timer? _resumeRestoreTimer;

    private bool _subscribed;
    private volatile bool _isStopping;

    // Ticks (DateTime.UtcNow.Ticks) after which Fn+Space level changes are allowed again.
    private long _suppressFnSpaceUntil;

    private ManagementEventWatcher? _watcher1;
    private ManagementEventWatcher? _watcher2;
    private Timer? _wmiDebounceTimer;

    /// <summary>
    ///     Dispose resources
    /// </summary>
    public void Dispose()
    {
        Stop();
    }

    public event BacklightRestoreEventHandler? OnRestoreBacklight;

    /// <summary>
    ///     Fired exclusively after the post-resume hold-off has elapsed.
    ///     The handler should kick the driver to a different level first,
    ///     then set the real target — this breaks the post-sleep desync.
    /// </summary>
    public event BacklightRestoreEventHandler? OnResumeRestoreBacklight;

    /// <summary>
    ///     Fired when the user changes the backlight level via Fn+Space (hardware key).
    ///     The integer argument is the new level read from the hardware (0/1/2).
    ///     Sourced by monitoring bit 17 of HKLM\...\IBMPMSVC\Parameters\Notification.
    /// </summary>
    public event Action<int>? OnFnSpaceLevelChanged;

    private void FireRestore()
    {
        if (_isStopping) return;

        // During the post-resume hold-off window, skip restores from non-resume
        // sources (WMI, display polling) — the driver is still desynced.
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _resumeHoldoffUntil))
        {
            Debug.WriteLine("FireRestore skipped: within resume hold-off window");
            return;
        }

        // Suppress Fn+Space level-change events for FnSpaceSuppressMs after any
        // restore trigger so a transient hardware reset (lid close) cannot
        // overwrite the user's saved backlight level.
        SuppressFnSpace();
        OnRestoreBacklight?.Invoke();
    }

    /// <summary>
    ///     Fires a restore after the post-resume hold-off has elapsed.
    ///     Bypasses the hold-off check (since we *are* the resume restore).
    /// </summary>
    private void FireRestoreFromResume()
    {
        if (_isStopping) return;

        SuppressFnSpace();
        OnResumeRestoreBacklight?.Invoke();
    }

    /// <summary>
    ///     Debounced variant of <see cref="FireRestore" /> for WMI event handlers.
    ///     Repeated calls within <see cref="WmiDebouncePeriodMs" /> are collapsed into
    ///     a single restore — this prevents the noisy
    ///     <c>Win32_SystemConfigurationChangeEvent</c> from triggering dozens of
    ///     redundant IOCTL round-trips during a docking or USB-hub event burst.
    /// </summary>
    private void DebouncedFireRestore()
    {
        try
        {
            _wmiDebounceTimer?.Change(WmiDebouncePeriodMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Ignore races with Stop().
        }
    }

    /// <summary>
    ///     Start monitoring for system events
    /// </summary>
    public bool Start()
    {
        if (_subscribed)
            return true;

        try
        {
            _isStopping = false;
            Debug.WriteLine("Starting event monitor...");

            // Shared debounce timer used by WMI event handlers below.
            _wmiDebounceTimer = new Timer(_ => FireRestore(), null,
                Timeout.Infinite, Timeout.Infinite);

            // Method 1: Monitor Win32_SystemConfigurationChangeEvent for hardware changes
            try
            {
                var query1 = new WqlEventQuery(
                    "SELECT * FROM Win32_SystemConfigurationChangeEvent");

                _watcher1 = new ManagementEventWatcher(query1);
                _watcher1.EventArrived += (_, e) =>
                {
                    e.NewEvent?.Dispose(); // release COM wrapper to prevent WMI leak
                    Debug.WriteLine(
                        "SystemConfigurationChangeEvent triggered — debouncing");
                    DebouncedFireRestore();
                };

                _watcher1.Start();
                Debug.WriteLine("SystemConfigurationChangeEvent watcher started");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"Error starting SystemConfigurationChangeEvent watcher: {ex.Message}");
            }

            // Method 2: Monitor Win32_PowerSupplyEvent for AC adapter changes (often triggered by lid)
            try
            {
                var query2 = new WqlEventQuery(
                    "SELECT * FROM Win32_PowerSupplyEvent");

                _watcher2 = new ManagementEventWatcher(query2);
                _watcher2.EventArrived += (_, e) =>
                {
                    e.NewEvent?.Dispose(); // release COM wrapper to prevent WMI leak
                    Debug.WriteLine("PowerSupplyEvent triggered — debouncing");
                    DebouncedFireRestore();
                };

                _watcher2.Start();
                Debug.WriteLine("PowerSupplyEvent watcher started");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error starting PowerSupplyEvent watcher: {ex.Message}");
            }

            // Method 3: Polling for display state changes (fallback for lid detection)
            _displayMonitorTimer = new Timer(_ =>
            {
                try
                {
                    var currentDisplayState = GetDisplayState();
                    if (currentDisplayState != _lastDisplayState)
                    {
                        Debug.WriteLine(
                            $"Display state changed from {_lastDisplayState} to {currentDisplayState}");
                        _lastDisplayState = currentDisplayState;
                        FireRestore();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error in display monitor timer: {ex.Message}");
                }
            }, null, Timeout.Infinite, Timeout.Infinite);

            _lastDisplayState = GetDisplayState();
            _displayMonitorTimer.Change(0, 2000); // Check every 2 seconds
            Debug.WriteLine("Display state polling started");

            // Method 4: SystemEvents.PowerModeChanged — the most reliable source for
            // lid-open / wake-from-sleep events on Windows (fires on Resume).
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            Debug.WriteLine("PowerModeChanged handler registered");

            StartFnSpaceWatch();

            _subscribed = true;
            Debug.WriteLine("Event monitor fully started");
            return true;
        }
        catch (OperationCanceledException)
        {
            // Expected if called via cancellation token
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error starting event monitor: {ex.Message}");
            Stop(); // ensure partial startup resources are released
            throw new InvalidOperationException(
                $"EventMonitor failed to start: {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     Get the current display monitor count via a lightweight Win32 call.
    ///     Previous implementation used a WMI query every 2 seconds which was
    ///     extremely expensive (COM/WMI infrastructure spin-up on each tick).
    /// </summary>
    private static uint GetDisplayState()
    {
        try
        {
            return (uint)GetSystemMetrics(SM_CMONITORS);
        }
        catch
        {
            return 0;
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>
    ///     Handles system power mode changes.
    ///     Suspend: suppress Fn+Space immediately so a hardware-reset bit-17 flip during
    ///     sleep cannot overwrite the saved level.
    ///     Resume: suppress again (extends the window), then restore after a short delay
    ///     to let the hardware finish waking.
    /// </summary>
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (_isStopping) return;

        if (e.Mode == PowerModes.Suspend)
        {
            Debug.WriteLine("PowerModeChanged: Suspend — suppressing Fn+Space");
            SuppressFnSpace();
        }
        else if (e.Mode == PowerModes.Resume)
        {
            Debug.WriteLine("PowerModeChanged: Resume — holding off all triggers, scheduling restore in 5 s");
            // Suppress Fn+Space immediately so the watcher thread can't overwrite
            // the saved level with the transient hardware-reset value.
            SuppressFnSpace();

            // Block WMI / display-polling restores for the hold-off period so
            // they don't hit the driver while it's still desynced.
            Interlocked.Exchange(ref _resumeHoldoffUntil,
                DateTime.UtcNow.Ticks + ResumeHoldoffMs * TimeSpan.TicksPerMillisecond);

            // After the hold-off, fire the one authoritative restore via a one-shot timer
            // (avoids blocking a thread pool thread with Thread.Sleep).
            _resumeRestoreTimer?.Dispose();
            _resumeRestoreTimer = new Timer(_ => FireRestoreFromResume(), null,
                ResumeHoldoffMs, Timeout.Infinite);
        }
    }

    /// <summary>
    ///     Extends the Fn+Space suppression window from now, without firing a restore.
    /// </summary>
    private void SuppressFnSpace()
    {
        Interlocked.Exchange(ref _suppressFnSpaceUntil,
            DateTime.UtcNow.Ticks + FnSpaceSuppressMs * TimeSpan.TicksPerMillisecond);
    }

    /// <summary>
    ///     Watches IBMPMSVC\Parameters\Notification for bit-17 flips caused by Fn+Space.
    ///     Based on technique from pspatel321/auto-backlight-for-thinkpad.
    /// </summary>
    private void StartFnSpaceWatch()
    {
        try
        {
            using var probe = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\IBMPMSVC\Parameters\Notification");
            if (probe == null)
            {
                Debug.WriteLine("FnSpace watch: IBMPMSVC Notification key not found, skipping");
                return;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FnSpace watch: probe failed ({ex.Message}), skipping");
            return;
        }

        _fnSpaceStop = new ManualResetEvent(false);
        _fnSpaceThread = new Thread(() =>
            {
                try
                {
                    using var notifyKey = Registry.LocalMachine.OpenSubKey(
                        @"SYSTEM\CurrentControlSet\Services\IBMPMSVC\Parameters\Notification");
                    if (notifyKey == null) return;

                    using var changeEvent = new EventWaitHandle(false, EventResetMode.AutoReset);

                    var lastVal = ReadNotifyValue(notifyKey);
                    var waitHandles = new WaitHandle[] { changeEvent, _fnSpaceStop! };

                    while (true)
                    {
                        try
                        {
                            var ret = RegNotifyChangeKeyValue(
                                notifyKey.Handle.DangerousGetHandle(),
                                false, 4,
                                changeEvent.SafeWaitHandle.DangerousGetHandle(),
                                true);

                            if (ret != 0)
                            {
                                Debug.WriteLine($"FnSpace watch: RegNotifyChangeKeyValue returned {ret}, stopping");
                                break;
                            }

                            var which = WaitHandle.WaitAny(waitHandles);
                            if (which == 1) break; // stop requested

                            var newVal = ReadNotifyValue(notifyKey);
                            var changed = lastVal ^ newVal;
                            lastVal = newVal;

                            // Bit 17 flip = Fn+Space backlight level change
                            if (((changed >> 17) & 1) == 1)
                            {
                                Debug.WriteLine("FnSpace watch: bit 17 flipped — Fn+Space pressed");

                                // Suppress if a system restore event fired recently — lid-close
                                // can transiently reset the hardware level to Off and flip the
                                // same bit, which would overwrite the user's saved preference.
                                if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _suppressFnSpaceUntil))
                                {
                                    Debug.WriteLine("FnSpace watch: suppressed (within restore window)");
                                }
                                else
                                {
                                    var level = BacklightController.GetBacklightLevel();
                                    if (level.HasValue)
                                        OnFnSpaceLevelChanged?.Invoke((int)level.Value);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"FnSpace watch thread failed: {ex.Message}");
                            // Exit the loop on unexpected errors to prevent thread hang
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"FnSpace watch thread outer exception: {ex.Message}");
                }
            })
        { IsBackground = true, Name = "FnSpace-Watch" };

        _fnSpaceThread.Start();
        Debug.WriteLine("FnSpace registry watcher started");
    }

    private static uint ReadNotifyValue(RegistryKey key)
    {
        try
        {
            return (uint)(int)(key.GetValue(null) ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegNotifyChangeKeyValue(
        IntPtr hKey, bool watchSubtree, uint notifyFilter,
        IntPtr hEvent, bool asynchronous);

    /// <summary>
    ///     Stop monitoring for system events
    /// </summary>
    public void Stop()
    {
        _isStopping = true;

        if (_watcher1 != null)
            try
            {
                _watcher1.Stop();
                _watcher1.Dispose();
                _watcher1 = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error stopping watcher1: {ex.Message}");
            }

        if (_watcher2 != null)
            try
            {
                _watcher2.Stop();
                _watcher2.Dispose();
                _watcher2 = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error stopping watcher2: {ex.Message}");
            }

        if (_displayMonitorTimer != null)
            try
            {
                _displayMonitorTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _displayMonitorTimer.Dispose();
                _displayMonitorTimer = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error stopping display monitor timer: {ex.Message}");
            }

        if (_wmiDebounceTimer != null)
            try
            {
                _wmiDebounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _wmiDebounceTimer.Dispose();
                _wmiDebounceTimer = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error stopping WMI debounce timer: {ex.Message}");
            }

        if (_resumeRestoreTimer != null)
            try
            {
                _resumeRestoreTimer.Dispose();
                _resumeRestoreTimer = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error stopping resume restore timer: {ex.Message}");
            }

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        // Stop Fn+Space watcher
        if (_fnSpaceStop != null)
        {
            _fnSpaceStop.Set();
            if (_fnSpaceThread != null && !_fnSpaceThread.Join(500))
                Debug.WriteLine("FnSpace watch thread did not exit within 500 ms");
            _fnSpaceStop.Dispose();
            _fnSpaceStop = null;
            _fnSpaceThread = null;
        }

        _subscribed = false;
        Debug.WriteLine("Event monitor stopped");
    }
}