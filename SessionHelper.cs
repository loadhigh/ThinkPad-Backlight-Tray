using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ThinkPadBacklightTray;

/// <summary>
///     Detects whether the current session is the physical console
///     (not an RDP or remote desktop session).
/// </summary>
internal static class SessionHelper
{
    private const int SM_REMOTESESSION = 0x1000;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>
    ///     Returns <c>true</c> when the process is running on the local
    ///     physical console; <c>false</c> for RDP / remote sessions.
    /// </summary>
    public static bool IsConsoleSession()
    {
        try
        {
            return GetSystemMetrics(SM_REMOTESESSION) == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SessionHelper: failed to detect session type: {ex.Message}");
            return true; // graceful degradation – assume console
        }
    }
}