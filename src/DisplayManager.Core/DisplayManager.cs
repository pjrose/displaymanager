#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging;

namespace DisplayManagerLib;

public static class DisplayManager
{
    // =========================
    // Public contracts
    // =========================

    public sealed class MonitorInfo
    {
        public long AdapterLuid { get; init; }        // GPU identity (LUID: High<<32 | Low)
        public uint TargetId    { get; init; }        // Output/connector on that GPU
        public string DeviceName { get; init; } = ""; // GDI name: "\\.\\DISPLAYx"
        public string DevicePath { get; init; } = ""; // Device interface path: "\\?\DISPLAY#..."
        public string FriendlyName { get; init; } = "";
        public string? EdidManufacturerId { get; init; } // e.g., "DEL"
        public string? EdidProductCode    { get; init; } // hex
        public string? EdidSerial         { get; init; }
        public RectPx BoundsPx { get; init; }            // virtual desktop pixels
        public bool IsPrimary { get; init; }

        public override string ToString()
            => $"{FriendlyName} [{DeviceName}] Luid=0x{AdapterLuid:X}, TargetId={TargetId}, Bounds={BoundsPx}, Primary={IsPrimary}";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RectPx
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width  => Right - Left;
        public readonly int Height => Bottom - Top;
        public override readonly string ToString() => $"L={Left},T={Top},W={Width},H={Height}";
    }

    public enum Orientations
    {
        DEGREES_CW_0   = 0,
        DEGREES_CW_90  = 3,
        DEGREES_CW_180 = 2,
        DEGREES_CW_270 = 1
    }

    // =========================
    // Public API
    // =========================

    /// <summary>Enumerate active monitors using Display Configuration.</summary>
    public static IReadOnlyList<MonitorInfo> GetMonitors(ILogger? log = null)
    {
        InvalidOperationException? lastError = null;

        foreach (var flags in PreferredQueryFlags())
        {
            if (TryGetMonitors(flags, log, out var monitors, out var error))
            {
                log?.LogDebug("Enumerated monitors using QueryDisplayConfig flags=0x{flags:X}", flags);
                return monitors;
            }

            if (error is null)
                continue;

            if (IsInvalidParameter(error))
            {
                lastError = error;
                continue; // Try the next flag combination.
            }

            throw error;
        }

        throw lastError ?? new InvalidOperationException("QueryDisplayConfig failed for all flag combinations.");
    }

    private static bool TryGetMonitors(uint flags, ILogger? log, out List<MonitorInfo> monitors, out InvalidOperationException? error)
    {
        monitors = new List<MonitorInfo>();
        error = null;

        var rc = GetDisplayConfigBufferSizes(flags, out var numPath, out var numMode);
        if (rc != 0)
        {
            error = CreateDisplayConfigException($"GetDisplayConfigBufferSizes failed rc={rc} (flags=0x{flags:X})", rc, flags);
            return false;
        }

        if (numPath == 0 || numMode == 0)
        {
            monitors = new List<MonitorInfo>();
            return true;
        }

        int pathSize = Marshal.SizeOf<DISPLAYCONFIG_PATH_INFO>();
        int modeSize = Marshal.SizeOf<DISPLAYCONFIG_MODE_INFO>();
        IntPtr pPaths = Marshal.AllocHGlobal(pathSize * (int)numPath);
        IntPtr pModes = Marshal.AllocHGlobal(modeSize * (int)numMode);

        try
        {
            rc = QueryDisplayConfig(flags, ref numPath, pPaths, ref numMode, pModes, IntPtr.Zero);
            if (rc != 0)
            {
                error = CreateDisplayConfigException($"QueryDisplayConfig(data) failed rc={rc} (flags=0x{flags:X})", rc, flags);
                return false;
            }

            // Collect modes by id
            var modes = new DISPLAYCONFIG_MODE_INFO[numMode];
            for (int i = 0; i < numMode; i++)
                modes[i] = Marshal.PtrToStructure<DISPLAYCONFIG_MODE_INFO>(pModes + i * modeSize)!;

            var paths = new DISPLAYCONFIG_PATH_INFO[numPath];
            for (int i = 0; i < numPath; i++)
                paths[i] = Marshal.PtrToStructure<DISPLAYCONFIG_PATH_INFO>(pPaths + i * pathSize)!;

            var result = new List<MonitorInfo>(paths.Length);

            // Build per-target info
            foreach (var path in paths)
            {
                var targetInfo = path.targetInfo;
                var sourceInfo = path.sourceInfo;

                // target identity
                long luid = ((long)targetInfo.adapterId.HighPart << 32) | (uint)targetInfo.adapterId.LowPart;
                uint targetId = targetInfo.id;

                // names / EDID
                string gdiDeviceName = "", devicePath = "", friendly = "";
                string? mfg = null, prod = null, serial = null;

                var tname = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = targetInfo.adapterId,
                        id = targetId
                    }
                };
                int r1 = DisplayConfigGetDeviceInfo(ref tname);
                if (r1 == 0)
                {
                    devicePath = tname.monitorDevicePath;
                    friendly   = tname.monitorFriendlyDeviceName;
                }

                // EDID is exposed through the same struct’s fields:
                if (r1 == 0)
                {
                    mfg   = tname.edidManufactureId != 0 ? DecodeMfgId(tname.edidManufactureId) : null;
                    prod  = tname.edidProductCodeId != 0 ? $"0x{tname.edidProductCodeId:X4}" : null;
                    serial= tname.monitorDevicePath; // sometimes includes serial; no separate field reliably available
                }

                // source (bounds & GDI name)
                var sname = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = sourceInfo.adapterId,
                        id = sourceInfo.id
                    }
                };
                int r2 = DisplayConfigGetDeviceInfo(ref sname);
                if (r2 == 0)
                    gdiDeviceName = sname.viewGdiDeviceName ?? ""; // "\\.\\DISPLAYx"

                // Find source mode entry
                var srcMode = modes.FirstOrDefault(m => m.infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE
                                                        && m.id == sourceInfo.id
                                                        && m.adapterId.HighPart == sourceInfo.adapterId.HighPart
                                                        && m.adapterId.LowPart == sourceInfo.adapterId.LowPart);

                var bounds = new RectPx();
                if (srcMode.info.sourceMode.width != 0 && srcMode.info.sourceMode.height != 0)
                {
                    bounds.Left   = srcMode.info.sourceMode.position.x;
                    bounds.Top    = srcMode.info.sourceMode.position.y;
                    bounds.Right  = bounds.Left + (int)srcMode.info.sourceMode.width;
                    bounds.Bottom = bounds.Top  + (int)srcMode.info.sourceMode.height;
                }
                else
                {
                    // Fallback: zero if missing (shouldn’t happen for active paths)
                    bounds = default;
                }

                // primary heuristic: Windows defines primary at virtual (0,0)
                bool isPrimary = bounds.Left == 0 && bounds.Top == 0;

                var mi = new MonitorInfo
                {
                    AdapterLuid = luid,
                    TargetId = targetId,
                    DeviceName = gdiDeviceName,
                    DevicePath = devicePath,
                    FriendlyName = string.IsNullOrWhiteSpace(friendly)
                        ? (!string.IsNullOrWhiteSpace(gdiDeviceName) ? gdiDeviceName : devicePath)
                        : friendly,
                    EdidManufacturerId = mfg,
                    EdidProductCode = prod,
                    EdidSerial = serial,
                    BoundsPx = bounds,
                    IsPrimary = isPrimary
                };
                result.Add(mi);
            }

            // Deduplicate per target (paths can contain duplicates in clone mode)
            result = result
                .GroupBy(m => (m.AdapterLuid, m.TargetId))
                .Select(g => g.First())
                .OrderByDescending(m => m.IsPrimary)
                .ThenBy(m => m.BoundsPx.Left)
                .ThenBy(m => m.BoundsPx.Top)
                .ToList();

            // Log
            if (log != null)
            {
                foreach (var m in result)
                    log.LogInformation("Monitor: {info}", m.ToString());
            }

            monitors = result;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(pPaths);
            Marshal.FreeHGlobal(pModes);
        }
    }

    private static IEnumerable<uint> PreferredQueryFlags()
    {
        yield return QDC_ONLY_ACTIVE;
        yield return QDC_ONLY_ACTIVE | QDC_VIRTUAL_MODE_AWARE;
        yield return QDC_DATABASE_CURRENT;
        yield return QDC_DATABASE_CURRENT | QDC_VIRTUAL_MODE_AWARE;
        yield return QDC_ALL_PATHS;
        yield return QDC_ALL_PATHS | QDC_VIRTUAL_MODE_AWARE;
    }

    private static bool IsInvalidParameter(InvalidOperationException ex)
        => ex.Data.Contains("ErrorCode") && ex.Data["ErrorCode"] is int code && code == ERROR_INVALID_PARAMETER;

    private static InvalidOperationException CreateDisplayConfigException(string message, int errorCode, uint flags)
    {
        var ex = new InvalidOperationException(message);
        ex.Data["ErrorCode"] = errorCode;
        ex.Data["QueryFlags"] = flags;
        return ex;
    }

    /// <summary>Find by AdapterLuid + TargetId.</summary>
    public static MonitorInfo? Find(long adapterLuid, uint targetId, ILogger? log = null)
    {
        var m = GetMonitors(log).FirstOrDefault(x => x.AdapterLuid == adapterLuid && x.TargetId == targetId);
        log?.LogInformation(m is null
            ? "Monitor not found for Luid=0x{l:X},TargetId={t}"
            : "Resolved monitor: {m}", adapterLuid, targetId, m?.ToString());
        return m;
    }

    /// <summary>Find by contains(FriendlyName). Primary/left/top tie-break.</summary>
    public static MonitorInfo? FindByFriendlyName(string contains, ILogger? log = null)
    {
        var list = GetMonitors(log);
        var m = list.Where(x => x.FriendlyName.Contains(contains, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.IsPrimary).ThenBy(x => x.BoundsPx.Left).ThenBy(x => x.BoundsPx.Top)
                    .FirstOrDefault();
        log?.LogInformation(m is null
            ? "Monitor not found by name contains: {n}"
            : "Resolved monitor by name: {m}", contains, m?.ToString());
        return m;
    }

    /// <summary>Place/size a WPF window on a monitor; optionally maximize.</summary>
    public static void ApplyWindow(Window win, MonitorInfo mon, bool maximize, double? widthDip = null, double? heightDip = null, double marginDip = 0, ILogger? log = null, WindowPlacementTracker? tracker = null)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(win).EnsureHandle();
        var dpi = VisualTreeHelper.GetDpi(win); // Requires PMv2 DPI awareness

        int marginPx = DipToPx(marginDip, dpi.DpiScaleX);
        int x = mon.BoundsPx.Left + marginPx;
        int y = mon.BoundsPx.Top  + marginPx;

        int wPx = widthDip.HasValue ? DipToPx(widthDip.Value, dpi.DpiScaleX) : mon.BoundsPx.Width  - 2 * marginPx;
        int hPx = heightDip.HasValue? DipToPx(heightDip.Value, dpi.DpiScaleY) : mon.BoundsPx.Height - 2 * marginPx;

        log?.LogInformation("Applying window to {mon} at ({x},{y},{w},{h}) px", mon.ToString(), x, y, wPx, hPx);

        SetWindowPos(hwnd, IntPtr.Zero, x, y, wPx, hPx, SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        if (maximize) win.WindowState = WindowState.Maximized;

        tracker?.Record(win, mon, new WindowPlacementOptions(maximize, widthDip, heightDip, marginDip), log);
    }

    /// <summary>Rotate output for the given monitor.</summary>
    public static void Rotate(MonitorInfo mon, Orientations orientation, ILogger? log = null)
    {
        string deviceName = string.IsNullOrWhiteSpace(mon.DeviceName)
            ? ResolveDisplayDeviceName(mon)
            : mon.DeviceName;
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new InvalidOperationException("Cannot resolve \\.\\DISPLAYx for rotation.");

        log?.LogInformation("Rotating {mon} via GDI {gdi} to {ori}", mon.ToString(), deviceName, orientation);

        var devmode = new DEVMODE();
        devmode.dmSize = (short)Marshal.SizeOf<DEVMODE>();

        if (EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref devmode) == 0)
            throw new InvalidOperationException("EnumDisplaySettings failed.");

        bool swap = ((devmode.dmDisplayOrientation + (int)orientation) % 2) == 1;
        if (swap)
        {
            int t = devmode.dmPelsHeight;
            devmode.dmPelsHeight = devmode.dmPelsWidth;
            devmode.dmPelsWidth  = t;
        }

        devmode.dmFields = DM.DisplayOrientation | DM.PelsWidth | DM.PelsHeight;
        devmode.dmDisplayOrientation = orientation switch
        {
            Orientations.DEGREES_CW_0   => DMDO_DEFAULT,
            Orientations.DEGREES_CW_90  => DMDO_270,
            Orientations.DEGREES_CW_180 => DMDO_180,
            Orientations.DEGREES_CW_270 => DMDO_90,
            _ => DMDO_DEFAULT
        };

        var rc = (DISP_CHANGE)ChangeDisplaySettingsEx(deviceName, ref devmode, IntPtr.Zero, DisplaySettingsFlags.CDS_UPDATEREGISTRY, IntPtr.Zero);
        if (rc != DISP_CHANGE.Successful && rc != DISP_CHANGE.Restart)
            throw new InvalidOperationException($"ChangeDisplaySettingsEx failed rc={rc}");
    }

    /// <summary>Hook display topology/DPI changes and invoke callback (e.g., re-place windows).</summary>
    public static void HookDisplayChanges(Window window, Action callback)
    {
        var dispatcher = window.Dispatcher;

        void InvokeOnDispatcher()
        {
            if (dispatcher.CheckAccess())
                callback();
            else
                dispatcher.BeginInvoke(callback);
        }

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, __) => InvokeOnDispatcher();

        var src = System.Windows.Interop.HwndSource.FromHwnd(
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle());

        src.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            const int WM_DISPLAYCHANGE = 0x007E;
            const int WM_DPICHANGED    = 0x02E0;
            if (msg == WM_DISPLAYCHANGE || msg == WM_DPICHANGED) InvokeOnDispatcher();
            return IntPtr.Zero;
        });
    }

    /// <summary>Log a concise table to capture identities on fresh installs.</summary>
    public static void LogMonitors(ILogger log)
    {
        var list = GetMonitors(log);
        if (list.Count == 0) { log.LogWarning("No active monitors found."); return; }

        log.LogInformation("=== Active Monitors ({count}) ===", list.Count);
        foreach (var m in list)
        {
            log.LogInformation("Name={name} Gdi={gdi} Path={path} Luid=0x{l:X} TargetId={t} EDID={edid} Bounds={b} Primary={p}",
                m.FriendlyName, m.DeviceName, m.DevicePath, m.AdapterLuid, m.TargetId,
                FormatEdid(m), m.BoundsPx, m.IsPrimary);
        }
    }

    // =========================
    // Usage helpers
    // =========================

    public static MonitorInfo PickBySettingsOrFallback(long adapterLuid, uint targetId, string? nameContains, ILogger? log = null)
    {
        var byId = Find(adapterLuid, targetId, log);
        if (byId != null) return byId;

        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            var byName = FindByFriendlyName(nameContains, log);
            if (byName != null) return byName;
        }

        var list = GetMonitors(log);
        if (list.Count == 0) throw new InvalidOperationException("No monitors available.");
        return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.BoundsPx.Left).ThenBy(m => m.BoundsPx.Top).First();
    }

    // =========================
    // Internals & P/Invoke
    // =========================

    private static string FormatEdid(MonitorInfo m)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(m.EdidManufacturerId)) sb.Append(m.EdidManufacturerId);
        if (!string.IsNullOrWhiteSpace(m.EdidProductCode)) sb.Append('-').Append(m.EdidProductCode);
        return sb.Length == 0 ? "-" : sb.ToString();
    }

    private static string DecodeMfgId(uint id) // 5-bit encoding: A=1 → 'A'
    {
        char a = (char)('@' + ((id >> 10) & 0x1F));
        char b = (char)('@' + ((id >> 5)  & 0x1F));
        char c = (char)('@' + ( id        & 0x1F));
        return new string(new[] { a, b, c });
    }

    private static string ResolveDisplayDeviceName(MonitorInfo mon)
    {
        if (!string.IsNullOrWhiteSpace(mon.DeviceName))
            return mon.DeviceName;

        // As a fallback, enumerate DISPLAY_DEVICE and pick closest bounds match.
        var list = EnumerateDisplayDevicesWithBounds();
        if (list.Count == 0) return "";

        var best = list.OrderBy(d => RectDistanceSquared(d.Bounds, mon.BoundsPx)).First();
        return best.DeviceName;
    }

    private static double RectDistanceSquared(RectPx a, RectPx b)
    {
        double ax = (a.Left + a.Right) * 0.5, ay = (a.Top + a.Bottom) * 0.5;
        double bx = (b.Left + b.Right) * 0.5, by = (b.Top + b.Bottom) * 0.5;
        double dx = ax - bx, dy = ay - by; return dx * dx + dy * dy;
    }

    private sealed class DisplayDeviceInfo { public string DeviceName = ""; public RectPx Bounds; }

    private static List<DisplayDeviceInfo> EnumerateDisplayDevicesWithBounds()
    {
        var list = new List<DisplayDeviceInfo>();
        DISPLAY_DEVICE dd = new DISPLAY_DEVICE(); dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            if ((dd.StateFlags & DisplayDeviceStateFlags.AttachedToDesktop) == 0) continue;
            var devName = dd.DeviceName; // "\\.\\DISPLAYx"

            var dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
            if (EnumDisplaySettings(devName, ENUM_CURRENT_SETTINGS, ref dm) == 0) continue;

            var r = new RectPx
            {
                Left = dm.dmPosition.x,
                Top = dm.dmPosition.y,
                Right = dm.dmPosition.x + dm.dmPelsWidth,
                Bottom = dm.dmPosition.y + dm.dmPelsHeight
            };
            list.Add(new DisplayDeviceInfo { DeviceName = devName, Bounds = r });
            dd = new DISPLAY_DEVICE(); dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
        }
        return list;
    }

    private static int DipToPx(double dip, double scale) => (int)Math.Round(dip * scale);

    // -------------------------
    // Win32 P/Invoke
    // -------------------------

    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int ERROR_INVALID_PARAMETER = 87;

    private const uint QDC_ALL_PATHS           = 0x00000001;
    private const uint QDC_ONLY_ACTIVE         = 0x00000002;
    private const uint QDC_DATABASE_CURRENT    = 0x00000004;
    private const uint QDC_VIRTUAL_MODE_AWARE  = 0x00000010;

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags,
        ref uint numPath, IntPtr pathArray,
        ref uint numMode, IntPtr modeArray,
        IntPtr currentTopologyId /*unused*/);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME deviceName);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME deviceName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, int uFlags);
    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, DisplaySettingsFlags dwflags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    private const int ENUM_CURRENT_SETTINGS = -1;

    // -------------------------
    // DisplayConfig structs
    // -------------------------

    private enum DISPLAYCONFIG_MODE_INFO_TYPE : uint
    {
        DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1,
        DISPLAYCONFIG_MODE_INFO_TYPE_TARGET = 2,
        DISPLAYCONFIG_MODE_INFO_TYPE_DESKTOP_IMAGE = 3
    }

    private enum DISPLAYCONFIG_DEVICE_INFO_TYPE : uint
    {
        DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1,
        DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public int LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public DISPLAYCONFIG_MODE_INFO_TYPE infoType;
        public uint id;
        public LUID adapterId;
        public DISPLAYCONFIG_MODE_INFO_UNION info;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct DISPLAYCONFIG_MODE_INFO_UNION
    {
        [FieldOffset(0)] public DISPLAYCONFIG_TARGET_MODE targetMode;
        [FieldOffset(0)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_TARGET_MODE
    {
        public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
    {
        public ulong pixelRate;
        public DISPLAYCONFIG_RATIONAL hSyncFreq;
        public DISPLAYCONFIG_RATIONAL vSyncFreq;
        public DISPLAYCONFIG_2DREGION activeSize;
        public DISPLAYCONFIG_2DREGION totalSize;
        public uint videoStandard;
        public DISPLAYCONFIG_SCANLINE_ORDERING scanLineOrdering;
    }

    private enum DISPLAYCONFIG_SCANLINE_ORDERING : uint { }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_2DREGION { public uint cx; public uint cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SOURCE_MODE
    {
        public uint width;
        public uint height;
        public int pixelFormat;
        public POINTL position;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTL { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public DISPLAYCONFIG_DEVICE_INFO_TYPE type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS { public uint value; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    // -------------------------
    // Legacy rotation structs (needed by ChangeDisplaySettingsEx)
    // -------------------------

    [Flags]
    private enum DisplayDeviceStateFlags : int
    {
        AttachedToDesktop = 0x1,
        PrimaryDevice = 0x4,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public DisplayDeviceStateFlags StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [Flags]
    private enum DisplaySettingsFlags : int
    {
        CDS_UPDATEREGISTRY = 0x00000001
    }

    private enum DISP_CHANGE : int
    {
        Successful = 0,
        Restart = 1,
        Failed = -1
    }

    private const int DMDO_DEFAULT = 0;
    private const int DMDO_90 = 1;
    private const int DMDO_180 = 2;
    private const int DMDO_270 = 3;

    [Flags]
    private enum DM : int
    {
        Orientation = 0x00000001,
        Position = 0x00000020,
        DisplayOrientation = 0x00000080,
        PelsWidth = 0x00100000,
        PelsHeight = 0x00100000,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        private const int CCHDEVICENAME = 32;
        private const int CCHFORMNAME = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public DM dmFields;

        public POINTL dmPosition;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;

        public short dmColor; public short dmDuplex; public short dmYResolution; public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
        public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel; public int dmPelsWidth; public int dmPelsHeight;
        public int dmDisplayFlags; public int dmNup; public int dmDisplayFrequency;
    }
}
