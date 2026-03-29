using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ThinkPadBacklightTray;

/// <summary>
///     Controls keyboard backlight via the Lenovo IBMPmDrv kernel driver (IOCTL).
///     This is the IOCTL path used on ThinkPad machines that
///     have no dedicated HID backlight endpoint.
/// </summary>
public sealed class PmDriverBacklightController : IDisposable
{
    private const string DriverPath = @"\\.\IBMPmDrv";

    // IOCTL function codes (passed to CTL_CODE with DeviceType=FILE_DEVICE_UNKNOWN)
    private const uint FnMlcGet = 2464; // MLCG – Multiple Light Control Get
    private const uint FnMlcSet = 2465; // MLCS – Multiple Light Control Set
    private const uint FnKbagGet = 2456; // KBAG – Keyboard Backlight Agent Get
    private const uint FnKbagSet = 2457; // KBAS – Keyboard Backlight Agent Set

    private SafeFileHandle? _handle;

    public void Dispose()
    {
        if (_handle is { IsClosed: false })
            _handle.Dispose();
        _handle = null;
    }

    // ── public API ──────────────────────────────────────────────

    public static bool TryCreate(out PmDriverBacklightController? controller, out string summary)
    {
        controller = null;
        try
        {
            var handle = NativeMethods.CreateFile(
                DriverPath, FileAccess.Read, FileShare.Read,
                IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);

            if (handle.IsInvalid)
            {
                summary = $"IBMPmDrv open failed (win32={Marshal.GetLastWin32Error()})";
                return false;
            }

            // Probe: try MLCG to confirm the driver responds
            var mlcgOk = SendIoctl(handle, FnMlcGet, 0, out var mlcgRaw);

            if (!mlcgOk)
            {
                // Fallback: try KBAG
                if (!SendIoctl(handle, FnKbagGet, 0, out _))
                {
                    handle.Dispose();
                    summary = "IBMPmDrv opened but neither MLCG nor KBAG responded";
                    return false;
                }

                controller = new PmDriverBacklightController { _handle = handle };
                summary = "IBMPmDrv OK (KBAG fallback)";
                return true;
            }

            var mlcg = new MlcgResult(mlcgRaw);
            var present = (mlcg.PhysicalPresence & 1) == 1;
            var enabled = (mlcg.CurrentEnableState & 1) == 1;

            controller = new PmDriverBacklightController { _handle = handle };
            summary = $"IBMPmDrv OK (present={present}, enabled={enabled}, maxLevel={mlcg.MaxBacklightLevel})";
            return true;
        }
        catch (Exception ex)
        {
            summary = $"IBMPmDrv exception: {ex.Message}";
            return false;
        }
    }

    public bool SetBacklightLevel(int level)
    {
        if (_handle == null || _handle.IsInvalid || _handle.IsClosed) return false;
        if (level < 0 || level > 2) return false;

        // Try MLCG/MLCS first
        if (TrySetViaMLCG(level))
            return true;

        // Fallback to KBAG/KBAS
        if (TrySetViaKBAG(level))
            return true;

        Debug.WriteLine($"PmDriver: SetBacklightLevel({level}) failed on all paths");
        return false;
    }

    public bool TryGetBacklightLevel(out int level)
    {
        level = 0;
        if (_handle == null || _handle.IsInvalid || _handle.IsClosed) return false;

        // Try MLCG
        if (TryGetViaMLCG(out level))
            return true;

        // Fallback to KBAG
        if (TryGetViaKBAG(out level))
            return true;

        return false;
    }

    // ── MLCG / MLCS ─────────────────────────────────────────────

    private bool TrySetViaMLCG(int level)
    {
        if (!SendIoctl(_handle!, FnMlcGet, 0, out var raw))
            return false;

        var mlcg = new MlcgResult(raw);
        if ((mlcg.PhysicalPresence & 1) != 1 || (mlcg.CurrentEnableState & 1) != 1)
            return false;

        // Build MLCS_ARG: backlight level in bits [3:0], ThinkLight in bits [7:4], CycleMode at bit 8.
        // Reference: arg = ((cycleMode != 0) ? 0x100 : 0) | (ThinkLight << 4) | level
        var arg = (uint)level
                  | (mlcg.CurrentThinkLightLevel << 4)
                  | (mlcg.CurrentCycleMode << 8);

        if (!SendIoctl(_handle!, FnMlcSet, arg, out var setRaw))
            return false;

        // Check error state (bit 31)
        return (setRaw & 0x80000000) == 0;
    }

    private bool TryGetViaMLCG(out int level)
    {
        level = 0;
        if (!SendIoctl(_handle!, FnMlcGet, 0, out var raw))
            return false;

        var mlcg = new MlcgResult(raw);
        if ((mlcg.PhysicalPresence & 1) != 1 || (mlcg.CurrentEnableState & 1) != 1)
            return false;

        level = (int)mlcg.CurrentBacklightLevel;
        return true;
    }

    // ── KBAG / KBAS ─────────────────────────────────────────────

    private bool TrySetViaKBAG(int level)
    {
        if (!SendIoctl(_handle!, FnKbagGet, 0, out var raw))
            return false;

        var kbag = new KbagResult(raw);
        if (kbag.IsExist != 1 || kbag.IsSoftwareControllable != 1)
            return false;

        return SendIoctl(_handle!, FnKbagSet, (uint)level, out _);
    }

    private bool TryGetViaKBAG(out int level)
    {
        level = 0;
        if (!SendIoctl(_handle!, FnKbagGet, 0, out var raw))
            return false;

        var kbag = new KbagResult(raw);
        if (kbag.IsExist != 1 || kbag.IsSoftwareControllable != 1)
            return false;

        level = (int)kbag.CurrentBacklightLevel;
        return true;
    }

    // ── IOCTL helper ────────────────────────────────────────────

    private static uint CtlCode(uint function)
    {
        // FILE_DEVICE_UNKNOWN=34, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0
        return (34u << 16) | (0u << 14) | (function << 2) | 0u;
    }

    private static bool SendIoctl(SafeFileHandle handle, uint function, uint input, out uint output)
    {
        output = 0;
        return NativeMethods.DeviceIoControl(
            handle, CtlCode(function),
            ref input, sizeof(uint),
            ref output, sizeof(uint),
            out _, IntPtr.Zero);
    }

    // ── bitfield parsers ──

    private readonly struct MlcgResult
    {
        public readonly uint CurrentBacklightLevel;
        public readonly uint CurrentThinkLightLevel;
        public readonly uint MaxBacklightLevel;
        public readonly uint CurrentEnableState;
        public readonly uint PhysicalPresence;
        public readonly uint CurrentCycleMode;

        public MlcgResult(uint raw)
        {
            CurrentBacklightLevel = raw & 0xFu;
            CurrentThinkLightLevel = (raw >> 4) & 0xFu;
            MaxBacklightLevel = (raw >> 8) & 0xFu;
            CurrentEnableState = (raw >> 16) & 0x3u;
            PhysicalPresence = (raw >> 18) & 0x3u;
            CurrentCycleMode = (raw >> 21) & 0x1u; // CycleMode flag
        }
    }

    private readonly struct KbagResult
    {
        public readonly uint CurrentBacklightLevel;
        public readonly uint MaximumLevel;
        public readonly uint IsExist;
        public readonly uint IsSoftwareControllable;

        public KbagResult(uint raw)
        {
            CurrentBacklightLevel = raw & 0xFFu;
            MaximumLevel = (raw >> 8) & 0xFFu;
            IsExist = (raw >> 16) & 0x1u;
            IsSoftwareControllable = (raw >> 17) & 0x1u;
        }
    }

    // ── P/Invoke ────────────────────────────────────────────────

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(
            string lpFileName,
            [MarshalAs(UnmanagedType.U4)] FileAccess dwDesiredAccess,
            [MarshalAs(UnmanagedType.U4)] FileShare dwShareMode,
            IntPtr lpSecurityAttributes,
            [MarshalAs(UnmanagedType.U4)] FileMode dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            ref uint lpInBuffer,
            uint nInBufferSize,
            ref uint lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);
    }
}