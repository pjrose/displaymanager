Display Manager toolkit
=======================

This repository now ships with a .NET 8 solution that contains the original `DisplayManager` helper, a reusable friendly-name
store, a WPF harness for runtime experimentation, and unit tests for the persistence components. The layout is:

* `src/DisplayManager.Core` – class library that exposes the `DisplayManager` API and the new `FriendlyNameStore` helper.
* `src/DisplayManager.WpfApp` – Windows-only sample application that lets you assign friendly names, rotate displays, and place
  a test window using the library.
* `tests/DisplayManager.Tests` – xUnit tests that validate the friendly-name persistence helpers.

To build and test on Windows, run:

```
dotnet build DisplayManager.sln
dotnet test DisplayManager.sln
```

> **Note**: cross-compiling the WPF project from non-Windows platforms requires the .NET 8 SDK with Windows Desktop targeting
> packs installed. The project files enable `EnableWindowsTargeting` so CI environments can perform cross-compilation without
> running the app.

### Sample application

Launch the harness (`DisplayManager.WpfApp`) to exercise the APIs interactively:

1. **Refresh Monitors** – queries `DisplayManager.GetMonitors` and populates the grid with GDI names, adapter LUIDs, and EDID
   metadata.
2. **Assign Friendly Name** – opens a dialog that stores your label via `FriendlyNameStore`, writing to a JSON file under the
   user's application data folder.
3. **Locate by Friendly Name** – demonstrates lookup by stored friendly name and falls back to selecting another monitor if the
   requested display is offline.
4. **Rotate Display** – prompts for a stored name, lets you pick an orientation, and calls `DisplayManager.Rotate` with the GDI
   identifier captured during enumeration.
5. **Show Test Window** – spawns a sample window and positions it with `DisplayManager.ApplyWindow` so you can confirm bounds
   handling.

Usage examples
--------------
1) Log identities on startup (capture AdapterLuid/TargetId)
// At app boot:
DisplayManager.LogMonitors(_logger);

// Sample log line (copy into settings):
// Name=DELL U2720Q Gdi=\\.\DISPLAY2 Path=\\?\DISPLAY#DEL1234#5&27b30f9b&0&UID0 Luid=0x1234567800012345 TargetId=2 EDID=DEL-0xA0B1 Bounds=L=0,T=0,W=3840,H=2160 Primary=True

2) Resolve the intended monitor from settings and place the window
// Retrieve from your SettingsManager (first-run: paste from logs; later runs: persisted)
long adapterLuid = SettingsManager.Instance.Get<long>("Participant", "AdapterLuid");
uint targetId    = SettingsManager.Instance.Get<uint>("Participant", "TargetId");
// Optional helper if you prefer name-only config:
string? contains = SettingsManager.Instance.TryGet<string>("Participant", "NameContains", out var v) ? v : null;

// Pick monitor (by id → by name → primary)
var mon = DisplayManager.PickBySettingsOrFallback(adapterLuid, targetId, contains, _logger);

// Place and maximize
DisplayManager.ApplyWindow(_floatingWindow, mon, maximize: true, log: _logger);
_floatingWindow.Show();
_floatingWindow.Activate();
_fractWebViewControl.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));

3) Reset rotation on that same output
private void ResetRotateScreenCommand()
{
    if (SingleScreenMode) return;

    long adapterLuid = SettingsManager.Instance.Get<long>("Participant", "AdapterLuid");
    uint targetId    = SettingsManager.Instance.Get<uint>("Participant", "TargetId");
    var mon = DisplayManager.Find(adapterLuid, targetId, _logger)
             ?? throw new InvalidOperationException("Target monitor not found.");
    DisplayManager.Rotate(mon, DisplayManager.Orientations.DEGREES_CW_0, _logger);
    isParticipantDisplayFlipped = false;
}

4) React to topology/DPI changes (re-place windows)
DisplayManager.HookDisplayChanges(_floatingWindow, () =>
{
    try
    {
        var mon = DisplayManager.PickBySettingsOrFallback(
            SettingsManager.Instance.Get<long>("Participant", "AdapterLuid"),
            SettingsManager.Instance.Get<uint>("Participant", "TargetId"),
            SettingsManager.Instance.TryGet<string>("Participant", "NameContains", out var s) ? s : null,
            _logger);

        DisplayManager.ApplyWindow(_floatingWindow, mon, maximize: true, log: _logger);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Re-placing window on display change failed.");
    }
});

Implementation notes (concise)

Identity: store AdapterLuid (GPU) + TargetId (connector). That pair selects the same physical output reliably.

Bounds: taken from DISPLAYCONFIG_SOURCE_MODE.position/width/height (virtual desktop space).

DeviceName: resolved from DISPLAYCONFIG_SOURCE_DEVICE_NAME (always "\\.\\DISPLAYx"), with DevicePath exposing the PnP interface string when you need it for diagnostics.

DPI: ensure Per-Monitor-V2 DPI awareness (app manifest or SetThreadDpiAwarenessContext) so VisualTreeHelper.GetDpi is correct.

Logging: LogMonitors prints everything you need to seed settings on fresh machines.

Display change hooks: callbacks are marshalled onto the window's Dispatcher so WPF UI work is safe.

This is the modern, deterministic way to query, place, and rotate windows on specific displays in .NET 8/WPF.
