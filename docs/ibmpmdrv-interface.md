# ibmpmdrv.sys / ibmpmsvc.exe Interface Reference

Interface documentation for the Lenovo power management kernel driver and service,
used for keyboard backlight control and other hardware management on ThinkPad systems.

**References:**
- Linux kernel driver: [`drivers/platform/x86/thinkpad_acpi.c`](https://github.com/torvalds/linux/blob/master/drivers/platform/x86/thinkpad_acpi.c)
- [pspatel321/auto-backlight-for-thinkpad](https://github.com/pspatel321/auto-backlight-for-thinkpad)

---

## 1. Device Paths

| Path | Description |
|---|---|
| `\\.\IBMPmDrv` | Primary kernel driver interface — all IOCTLs go here |
| `\\.\PMDRVS` | Secondary/alternate driver interface (older) |
| `\\.\tp4track` | TrackPoint device |

---

## 2. Opening a Handle

```c
HANDLE h = CreateFile(
    "\\\\.\\IBMPmDrv",
    GENERIC_READ,       // 0x80000000
    FILE_SHARE_READ,    // 1
    NULL,
    OPEN_EXISTING,      // 3
    0,
    NULL
);
// check h != INVALID_HANDLE_VALUE
CloseHandle(h);
```

All IOCTLs use `METHOD_BUFFERED` — pass a `DWORD` input buffer and receive a `DWORD` output buffer via `DeviceIoControl`.

```c
DWORD input = ..., output = 0, bytesReturned;
OVERLAPPED ov = {0};
DeviceIoControl(h, IOCTL_CODE, &input, sizeof(input),
                               &output, sizeof(output),
                               &bytesReturned, &ov);
```

---

## 3. IOCTL Code Structure

All codes use `CTL_CODE(DeviceType=0x22, Function, METHOD_BUFFERED, FILE_ANY_ACCESS)`:

```
Bits 31-16: DeviceType = 0x0022 (FILE_DEVICE_UNKNOWN)
Bits 15-14: Access     = 0 (FILE_ANY_ACCESS)
Bits 13-2:  Function   (see table below)
Bits  1-0:  Method     = 0 (METHOD_BUFFERED)
```

---

## 4. Buffer Schema Notes

### How to read the schema tables

Each IOCTL uses `METHOD_BUFFERED`. The kernel copies:
- `InputBuffer` → `SystemBuffer` before the call (size = `InputBufferLength`)
- Driver writes the response into the same `SystemBuffer` (size = `OutputBufferLength`)

The driver validates sizes at entry and returns `STATUS_INVALID_PARAMETER (0xC000000D)` if they are too small.

### Minimum buffer sizes per handler

These are the **minimum** required sizes for each handler. `OutputBufferLength` and `InputBufferLength` are validated in the `IO_STACK_LOCATION`.

| IOCTL / Handler | ACPI Methods | Min InputLen | Min OutputLen | Notes |
|---|---|---|---|---|
| `0x00222A00` (MLCG GET) | `MLCG` | **8 bytes** (2×DWORD) | **4 bytes** (1×DWORD) | Input: `[DWORD arg, DWORD flags]`; Output: status word |
| `0x00222A04` (MLCS SET) | `MLCS` | 4 bytes | **16 bytes** | Output buffer larger than expected |
| `0x0022270C` / `0x00222728` | `DKHML` | — | — | No literal size check found |
| `0x0022242C`–`0x22244C` range | `CSHM`/`TWGX` | — | **8 bytes** | `OutLen>=8` check at handler entry |
| `0x002225B4` / IQHM handlers | `IQHM` | **8 bytes** | **20 bytes (0x14)** | `OutLen>=0x14`, `InLen>=8` |
| `0x002225CC`–`0x002225D4` | `BWUS`/`BWUG` | — | **16 bytes (0x10)** | `OutLen>=0x10` |
| `0x0022260C`–`0x00222624` | `SSSP`/`GSSP` | **4 bytes** | **256 bytes (0x100)** | Large output for smart standby state blob |
| `0x0022265C`–`0x00222660` | `SCLK`/`GCLK`/`LSHS`/`LSHG` | **4 bytes** | **4 bytes** (also `0xED` variant) | Lid sensor: simple DWORD exchange |
| `0x00222674`–`0x00222678` | `LSHS`/`LSHG` | **4 bytes** | **4 bytes** | Same handler |
| `0x00222688`–`0x0022268C` | `FISW`/`SSDB` | **4 bytes** + **8 bytes** | **20 bytes (0x14)** | Multi-check |
| `0x002226E8` | `SCLK`/`GCLK` | **4 bytes** | **20 bytes (0x14)** + **4 bytes** | |
| `0x002225E0`–`0x002225E4` | `VKHM`/`CDBS` | **8 bytes** | — | |
| `0x00222564`–`0x00222578` | `TTHM`/`TSHM` | **2 bytes** | **20 bytes (0x14)** | |
| `0x00222590` | `TCHM`/`TFHM` | **2 bytes** | — | |
| `0x00222744` | `IPSS`/`SCTS` | **4 bytes** + **8 bytes** | — | Two-stage input check |
| `0x00222740` | `DPTS`/`DMCG` | **4 bytes** | — | |

---

## 5. Keyboard Backlight IOCTLs

These are the primary IOCTLs for backlight control.

### 5.1 GET — Read backlight state (`0x00222A00`, Function `0xA80`)

```c
DWORD input = 0, output = 0;
DeviceIoControl(h, 0x00222A00, &input, 4, &output, 4, &bytesReturned, &ov);
```

**Response word bit layout:**

| Bits | Mask | Field | Description |
|---|---|---|---|
| 1:0 | `0x00000003` | current level | Active brightness (0=off, 1=low, 2=full) |
| 9 | `0x00000200` | hw present | Must be set — confirms backlight hardware exists |
| 11:8 | `0x00000F00` | max level | Highest supported level (typically 2) |
| 19:16 | `0x000F0000` | hw ready | Must equal `0x5` (`word & 0x000F0000 == 0x00050000`) |
| 21 | `0x00200000` | feature flag | Used to construct the SET input word |

**Validity check:**
```c
if (!(output & (1 << 9)))           // no backlight hardware
if ((output & 0x000F0000) != 0x00050000)  // hardware not ready
```

### 5.2 SET — Write backlight state (`0x00222A04`, Function `0xA81`)

Construct the input word from the GET response:

```c
DWORD get_response = ...;   // from GET call
DWORD new_level    = ...;   // 0, 1, or 2

DWORD set_input = ((get_response & 0x00200000) ? 0x100 : 0)
                | (get_response & 0x000000F0)
                | new_level;

DWORD set_output = 0;
DeviceIoControl(h, 0x00222A04, &set_input, 4, &set_output, 4, &bytesReturned, &ov);
// set_output == 0 on success
```

**SET input word fields:**

| Bits | Source | Meaning |
|---|---|---|
| 8 (`0x100`) | Set if bit 21 of GET response is set | Feature modifier passthrough |
| 7:4 (`0xF0`) | Bits 7:4 of GET response | Preserved capability flags |
| 1:0 | `new_level` | Desired brightness (0–2) |

**Underlying ACPI methods:** `MLCG` (get), `MLCS` (set) on the `\_SB.HKEY` ACPI handle.

---

## 6. Full IOCTL Dispatch Table

All entries: `DeviceType=0x22`, `Access=0`, `Method=METHOD_BUFFERED`.
ACPI method names are from the driver's dispatch table context.

| IOCTL | Function | Adjacent ACPI Methods | Subsystem |
|---|---|---|---|
| `0x00222400` | `0x900` | `DKHML` | Hotkey mask |
| `0x00222414` | `0x905` | `DKHML` | Hotkey mask |
| `0x00222418` | `0x906` | `BKHML` | Hotkey mask (base) |
| `0x0022242C` | `0x90B` | `CSHM` | Custom hotkey mask |
| `0x002224A8` | `0x92A` | `TLHM` `TAHM` | Thermal hotkey mask |
| `0x0022251C` | `0x947` | `CDBG` | Debug channel |
| `0x00222520` | `0x948` | `TGHM` `TQHM` `PSHM` `PGHM` `PQHM` | Thermal / power hotkey masks |
| `0x00222530` | `0x94C` | `VKHM` `CDBS` | Video / CD-ROM hotkey mask |
| `0x00222534` | `0x94D` | `PSHM` `PGHM` `WGHM` `ESHM` | Power / wireless hotkey masks |
| `0x00222564` | `0x959` | `TTHM` `TSHM` | Thermal hotkey set/get |
| `0x0022257C` | `0x95F` | `IILG` `ISLG` | Indicator light get |
| `0x00222590` | `0x964` | `TCHM` `TFHM` `IILS` | Thermal / indicator light |
| `0x002225B4` | `0x96D` | `IQHM` `BWUS` `BWUG` | Hotkey mask / Bluetooth wakeup set/get |
| `0x002225B8` | `0x96E` | `OWAU` `SLWG` `MDHM` | One-way wakeup / sleep-lid-wake |
| `0x002225CC` | `0x973` | `NLIG` `OWAU` | NumLock indicator / one-way wakeup |
| `0x002225D0` | `0x974` | `FCHM` `SLWS` `NLWS` `NAWG` `NAWS` | Fn-key mask / NL wake / NumLock wake |
| `0x002225E0` | `0x978` | `NLIS` `NLIG` | NumLock indicator set/get |
| `0x002225E4` | `0x979` | `NLWG` `NAWG` `SLBP` | NL wake get / sleep battery |
| `0x0022260C` | `0x983` | `SSSP` `GSSP` `SSBS` | Smart standby set/get |
| `0x00222620` | `0x988` | `SSSP` `GSSP` | Smart standby |
| `0x00222624` | `0x989` | `SSBS` `GSBS` `SSSD` `SBSP` `SCCB` `GTCB` | Smart standby / battery / charge control |
| `0x00222638` | `0x98E` | `SCIB` `GCIB` `GSCB` | Smart charge input buffer set/get |
| `0x0022265C` | `0x997` | `GCLK` `LSHS` `LSHG` | Clock get / lid sensor set/get |
| `0x00222660` | `0x998` | `SSCB` `GSSI` `SKMS` `SKMG` | Smart charge / system info / keyboard map |
| `0x00222678` | `0x99E` | `SABK` `GABK` `SCLM` `GCLM` `SSFF` `GSFF` | Airplane mode / backlight / fan |
| `0x00222688` | `0x9A2` | `FISW` `SSDB` `GSDB` `SSCB` | Fan ISW / debug get/set |
| `0x0022268C` | `0x9A3` | `GCLM` `SSFF` `GSFF` `DPTG` `DMCS` | Fan / display / media control set |
| `0x002226E8` | `0x9BA` | `SCLK` `GCLK` `LSHS` `LSHG` | Clock set/get / lid sensor |
| `0x002226EC` | `0x9BB` | `DMCG` `POTG` `CTYD` `CSPA` | Display media get / power off / CPU type |
| `0x0022272C` | `0x9CB` | `DPTS` `DMCG` `POTG` | Display power set / media / power-off |
| `0x00222730` | `0x9CC` | `CSPA` `SMMG` `SFLG` `SCTG` `ILWS` | CPU sleep / smart mode / flag / IL wake |
| `0x00222740` | `0x9D0` | `SSPG` `DPTS` `DMCG` | Smart standby power get / display set |
| `0x00222744` | `0x9D1` | `ILWS` `IPSS` `SCTS` `ILBG` `LKSS` | Indicator light wake / power state / backlight get |
| `0x00222774` | `0x9DD` | `ILBS` | Indicator light backlight set |
| `0x002227F8` | `0x9FE` | `TGDG` `SMTG` `ILBS` | Thermal get debug / IL backlight |
| `0x002227FC` | `0x9FF` | `SWCG` `DDWH` | Switch control get / display wake-on-hotkey |
| `0x00222A00` | `0xA80` | `MLCG` *(runtime)* | **Keyboard backlight GET** |
| `0x00222A04` | `0xA81` | `MLCS` *(runtime)* | **Keyboard backlight SET** |

**Naming conventions in ACPI method names:**
- `G`/`S` prefix = Get / Set
- `HM` suffix = Hotkey Mask
- `WS`/`WG` suffix = Wake Set / Wake Get
- `SS`/`GS` prefix = Smart-Set / Smart-Get
- `IL` prefix = Indicator Light
- `NL` prefix = NumLock
- `BW` prefix = Bluetooth Wake

---

## 7. ACPI HKEY Event Codes

Reported via `MHKP` poll (Linux) / `IBMPMSVC\Parameters\Notification` registry bit 17 flip (Windows) when Fn keys are pressed.

| Code | Event |
|---|---|
| `0x1001` | Hotkey base (Fn+F1) |
| `0x1010` | Brightness up |
| `0x1011` | Brightness down |
| `0x1012` | **Keyboard backlight toggle (Fn+Space)** |
| `0x1015` | Volume up / unmute |
| `0x1016` | Volume down |
| `0x1017` | Mute output |
| `0x130F` | Privacy guard toggle |
| `0x131A` | AMT toggle |
| `0x2304` | Undock requested (S3) |
| `0x2305` | Bay eject requested (S3) |
| `0x2313` | Battery critically low (S3) |
| `0x2404` | Undock requested (S4) |
| `0x2405` | Bay eject requested (S4) |
| `0x2413` | Battery critically low (S4) |
| `0x3003` | Bay ejection complete |
| `0x3006` | Optical drive tray ejected |
| `0x4003` | Undock complete |
| `0x4010` | Docked to hotplug dock |
| `0x4011` | Undocked from hotplug dock |
| `0x4012` | Keyboard cover attached |
| `0x4013` | Keyboard cover detached/folded |
| `0x5001` | Lid closed |
| `0x5002` | Lid opened |
| `0x5009` | Tablet mode (swivel up) |
| `0x500A` | Notebook mode (swivel down) |
| `0x500B` | Tablet pen inserted |
| `0x500C` | Tablet pen removed |
| `0x5010` | Backlight control event |
| `0x6000` | NumLock key |
| `0x6005` | Fn key pressed |
| `0x6011` | Battery too hot |
| `0x6012` | Battery critically hot |
| `0x6013` | Battery charge limit changed |
| `0x6021` | Sensor too hot |
| `0x6022` | Sensor critically hot |
| `0x6030` | Thermal table changed |
| `0x6032` | Thermal control set completed |
| `0x6040` | AC adapter status changed |
| `0x60B0` | Palm detected |
| `0x60B1` | Palm undetected |
| `0x60C0` | Tablet mode changed (X1 Yoga 2016+) |
| `0x60F0` | Thermal transformation changed |
| `0x7000` | RF kill switch changed |

---

## 8. Hotkey Notification (Registry Monitor)

When Fn+Space is pressed by the user, IBMPMSVC flips **bit 17** of the DWORD at:

```
HKLM\SYSTEM\CurrentControlSet\Services\IBMPMSVC\Parameters\Notification
```

Monitor with `RegNotifyChangeKeyValue`:

```c
HKEY hKey;
RegOpenKeyEx(HKEY_LOCAL_MACHINE,
    L"SYSTEM\\CurrentControlSet\\Services\\IBMPMSVC\\Parameters\\Notification",
    0, KEY_NOTIFY | KEY_QUERY_VALUE, &hKey);

HANDLE hEvent = CreateEvent(NULL, FALSE, FALSE, NULL);
for (;;) {
    RegNotifyChangeKeyValue(hKey, FALSE, REG_NOTIFY_CHANGE_LAST_SET, hEvent, TRUE);
    WaitForSingleObject(hEvent, INFINITE);
    DWORD value = 0, size = sizeof(value);
    RegQueryValueEx(hKey, NULL, NULL, NULL, (LPBYTE)&value, &size);
    if (value & (1 << 17)) {
        // Fn+Space pressed — re-query backlight level via 0x00222A00
    }
}
```

---

## 9. Named Kernel Events

Other processes can wait on these global events to react to power state changes without polling:

| Event | Signal meaning |
|---|---|
| `Global\LenovoPowerManagementDriverEventNotifyStart` | IBMPMSVC started |
| `Global\LenovoPowerManagementDriverEventNotifyStop` | IBMPMSVC stopping |
| `Global\LenovoPowerManagementDriverEventNotifySleep` | System suspending |
| `Global\LenovoPowerManagementDriverEventNotifyResume` | System resumed from sleep |
| `Global\LenovoPowerManagementDriverEventNotifyNotifyEvent` | ACPI hotkey/event fired |

```c
HANDLE hEvent = OpenEvent(SYNCHRONIZE, FALSE,
    L"Global\\LenovoPowerManagementDriverEventNotifyNotifyEvent");
WaitForSingleObject(hEvent, INFINITE);
// then poll registry or issue GET IOCTL
```

---

## 10. Service Control Interface

External Lenovo software communicates with IBMPMSVC via `ControlService()` using these custom control codes:

| Symbolic Name | Purpose |
|---|---|
| `LCDCS_STARTINHIBITLIDEVENT` | Prevent lid-close from sleeping the system |
| `LCDCS_STOPINHIBITLIDEVENT` | Re-enable lid-close sleep |
| `LCDCS_GETINHIBITCAPABILITY` | Query whether lid inhibit is supported |
| `SET_INTELLIGENT_COOLING_ENABLE` | Start Intelligent Cooling (DYTC) |
| `SET_INTELLIGENT_COOLING_DISABLE` | Stop Intelligent Cooling |
| `SET_INTELLIGENT_COOLING_STOP` | Kill IC worker thread |
| `SET_INTELLIGENT_COOLING_RESTART` | Restart IC worker thread |
| `SET_INTELLIGENT_COOLING_DYNAMIC_CPU_CONTROL_ENABLE` | Enable Dynamic CPU Control |
| `SET_INTELLIGENT_COOLING_DYNAMIC_CPU_CONTROL_DISABLE` | Disable Dynamic CPU Control |
| `SET_INTELLIGENT_COOLING_DYNAMIC_ON_HAND_DETECTED` | Notify: device lifted (on-hand) |
| `SET_INTELLIGENT_COOLING_DYNAMIC_ON_HAND_UNDETECTED` | Notify: device set down |

---

## 11. Intelligent Cooling (DYTC) ACPI Interface

The service wraps the `DYTC` ACPI method with three versioned implementations.

### Version detection

```
MHKV >= 0x200           → proceed
MHKA(2), MHKA(3), MHKA(4) → enumerate capability types
DYTC version from BIOS  → select v2 or v3 init path
```

| Version | Key ACPI methods | Notes |
|---|---|---|
| v1 | `DYTC` | Cool / High Performance only |
| v2 | `DYTC`, `GOPT` | Adds tablet option attach detection |
| v3 | `DYTC`, `GOPT`, `GMMS` | Full feature set |

### v3 Feature Flags

- Standard mode
- Cool Quiet on Laptop
- Two-In-One (detachable keyboard)
- Multi Yoga Hinge
- Skin Temperature Protection
- Cool Quiet Hinge
- Dynamic CPU Control
- Single Fan
- Dock Mode Control

### Thermal modes set via `DYTC`

| Mode | Description |
|---|---|
| Nominal | Balanced (default) |
| High Performance | Maximum performance, higher thermals |
| Cool Quiet | Lower noise/heat, reduced performance |

### Registry layout for Intelligent Cooling state

```
HKLM\SYSTEM\CurrentControlSet\Services\IBMPMSVC\
├── Parameters\
│   ├── Notification       (DWORD — hotkey event bits)
│   ├── Capability         (DWORD — hotkey capability mask)
│   ├── CurrentSetting     (DWORD)
│   ├── InterfaceVersion   (DWORD — ACPI HKEY version)
│   └── OverrideSetting    (DWORD)
├── Parameters2\
│   ├── Type10\Capability, CurrentSetting, Notification, OverrideSetting
│   └── Type20\Capability, Notification
├── IC\
│   ├── CQH\   (Cool Quiet Hinge)
│   ├── MYH\   (Multi Yoga Hinge)
│   ├── TIO\   (Two-In-One)
│   ├── DCC\   (Dynamic CPU Control)
│   ├── DMC\   (Dock Mode Control)
│   └── FHP\   (Force High Performance)
├── ICT\Parameters\        (legacy Intelligent Cooling)
└── Transform\
    ├── CurrentStatus
    └── Parameters
```

---

## 12. C# P/Invoke Reference

```csharp
[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess,
    uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
    uint dwFlagsAndAttributes, IntPtr hTemplateFile);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
    [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3), In]  byte[] lpInBuffer,  uint nInBufferSize,
    [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 6), Out] byte[] lpOutBuffer, uint nOutBufferSize,
    out uint lpBytesReturned, ref NativeOverlapped lpOverlapped);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool CloseHandle(IntPtr handle);
```

---

## 13. Fan Speed Access

Fan speed telemetry is **not** available through IBMPmDrv on current ThinkPad models.

The fan-related ACPI methods (`SSFF`, `GSFF`, `FISW`) mapped in the dispatch table
(functions `0x99E`, `0x9A2`, `0x9A3`) either return stubs or are absent from the
driver's active dispatch table.

### EC-based fan access on Linux

The Linux `thinkpad_acpi.c` driver reads fan RPM via **EC (Embedded Controller) register I/O**:
- Register `0x2F` (`HFSP`): fan duty/level byte
- Registers `0x84`–`0x85`: tachometer value (16-bit LE; `RPM ≈ 1350000 / raw_value`)

This requires ring-0 port I/O (`inb`/`outb` to ports `0x62`/`0x66`), which is not available
from Windows user mode without a dedicated kernel driver (e.g., WinRing0, LibreHardwareMonitor's
bundled driver).

---

## 14. Lid State via Registry

IBMPMSVC maintains a live lid-open/close state in the registry:

```
HKLM\SYSTEM\CurrentControlSet\Services\IBMPMSVC\Transform\CurrentStatus
    Lid   (DWORD)   — 2 = open, 1 = closed (unconfirmed), 0 = unknown
    MM    (DWORD)   — Multi-Mode state (0 = laptop, non-zero for tablet/tent)
```

This key is readable by standard users and can be monitored with `RegNotifyChangeKeyValue`
to detect lid transitions without polling. When `Lid` changes from `1` → `2` (close → open),
a backlight restore should be triggered.

### Related Transform registry layout

```
HKLM\SYSTEM\CurrentControlSet\Services\IBMPMSVC\Transform\
├── CurrentStatus\
│   ├── Lid    (DWORD)   — live lid state
│   └── MM     (DWORD)   — multi-mode / form-factor state
└── Parameters\
    ├── CurrentSetting   (DWORD)   — active DYTC mode (1 = balanced)
    ├── ChkINode         (DWORD)   — internal node check flag
    └── ChkOSV           (DWORD)   — OS version check flag
```

---

## 15. Notification Bit Layout

The `Notification\(default)` DWORD at `HKLM\...\IBMPMSVC\Parameters\Notification`
encodes multiple hotkey event flags beyond the Fn+Space bit:

| Bit | Mask | Event |
|---|---|---|
| 11 | `0x00000800` | Keyboard backlight toggle event |
| 15 | `0x00008000` | Hotkey event (type varies) |
| 17 | `0x00020000` | **Fn+Space pressed** (keyboard backlight cycle) |

A secondary `Type2` DWORD at the same key holds additional notification bits:

| Bit | Mask | Event |
|---|---|---|
| 4 | `0x00000010` | Type2 notification (purpose TBD) |
| 16 | `0x00010000` | Type2 notification (purpose TBD) |

---

## 16. Named Kernel Events

The events listed in §9 exist on running systems but are **restricted to elevated processes**.
`OpenEvent(SYNCHRONIZE, ...)` returns `ERROR_ACCESS_DENIED` (5) for standard users.
This makes them unsuitable for non-elevated tray applications.

---

## 17. IOCTL Subsystem Availability

Not all dispatch-table IOCTLs are active on every model. Tested on ThinkPad P-series:

| Subsystem | IOCTLs | Status |
|---|---|---|
| Keyboard backlight (MLCG/MLCS) | `0x00222A00`, `0x00222A04` | ✅ Active |
| Lid sensor (LSHG, Fn `0x99D`) | `0x00222674` | ✅ Returns `0x0` (state) |
| Airplane/backlight (SABK, Fn `0x99E`) | `0x00222678` | ⚠️ Stub — always returns `0x0` |
| Smart standby (GSSP) | `0x00222620` | ✅ Returns `0x0` |
| Indicator light (IILG, ILBG, ILBS) | `0x0022257C`, `0x00222744`, `0x00222774` | ❌ Absent (err=2) |
| Thermal (TTHM/TSHM) | `0x00222564` | ❌ err=87 (invalid parameter) |
| Clock/lid alt (SCLK/GCLK) | `0x002226E8` | ❌ Absent (err=2) |
| Display/media (DMCG, DPTS) | `0x002226EC`, `0x00222740` | ❌ Absent (err=2) |
| Smart mode (CSPA/SMMG) | `0x00222730` | ❌ Absent (err=2) |
| Fan (SSFF/GSFF, FISW) | `0x00222688`, `0x0022268C` | ❌ Absent (err=2) |

