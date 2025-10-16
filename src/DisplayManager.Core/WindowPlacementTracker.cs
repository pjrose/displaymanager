#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace DisplayManagerLib;

/// <summary>
/// Tracks windows that have been positioned with <see cref="DisplayManager.ApplyWindow"/> so that they can be
/// re-applied when the display topology changes.
/// </summary>
public sealed class WindowPlacementTracker
{
    private readonly object _sync = new();
    private readonly List<TrackedWindow> _tracked = new();

    /// <summary>Records the placement for the supplied window/monitor pair.</summary>
    public void Record(Window window, DisplayManager.MonitorInfo monitor, WindowPlacementOptions options, ILogger? log = null)
    {
        if (window is null) throw new ArgumentNullException(nameof(window));

        lock (_sync)
        {
            CleanupDeadEntries();

            var existing = FindTrackedWindow(window);
            if (existing != null)
            {
                existing.Update(monitor, options, log);
                return;
            }

            var trackedWindow = new TrackedWindow(this, window, monitor, options, log);
            _tracked.Add(trackedWindow);
        }
    }

    /// <summary>Stops tracking the supplied window (e.g., before disposing it manually).</summary>
    public void Forget(Window window)
    {
        if (window is null) throw new ArgumentNullException(nameof(window));

        lock (_sync)
        {
            var tracked = FindTrackedWindow(window);
            if (tracked is null)
                return;

            tracked.Detach();
            _tracked.Remove(tracked);
        }
    }

    /// <summary>
    /// Attempts to re-apply placements for all tracked windows. Missing monitors fall back to the primary display.
    /// </summary>
    public void RestoreTrackedWindows(ILogger? log = null)
    {
        List<TrackedWindow> snapshot;

        lock (_sync)
        {
            CleanupDeadEntries();
            snapshot = _tracked.ToList();
        }

        foreach (var tracked in snapshot)
        {
            if (!tracked.WindowRef.TryGetTarget(out var window))
            {
                RemoveDeadEntry(tracked);
                continue;
            }

            DisplayManager.MonitorInfo? monitor = DisplayManager.Find(tracked.AdapterLuid, tracked.TargetId, log);
            if (monitor is null && !string.IsNullOrWhiteSpace(tracked.LastKnownFriendlyName))
                monitor = DisplayManager.FindByFriendlyName(tracked.LastKnownFriendlyName, log);

            if (monitor is null)
            {
                try
                {
                    monitor = DisplayManager.PickBySettingsOrFallback(
                        tracked.AdapterLuid,
                        tracked.TargetId,
                        tracked.LastKnownFriendlyName,
                        log);
                }
                catch (InvalidOperationException ex)
                {
                    log?.LogWarning(ex, "Unable to restore placement for {window}: no monitors available.", tracked.WindowName);
                    continue;
                }
            }

            if (monitor is null)
            {
                log?.LogWarning("Unable to restore placement for {window}: matching monitor not found.", tracked.WindowName);
                continue;
            }

            DisplayManager.ApplyWindow(
                window,
                monitor,
                tracked.Options.Maximize,
                tracked.Options.WidthDip,
                tracked.Options.HeightDip,
                tracked.Options.MarginDip,
                log,
                this);
        }
    }

    private void HandleWindowClosed(TrackedWindow tracked)
    {
        lock (_sync)
        {
            tracked.Detach();
            _tracked.Remove(tracked);
        }
    }

    private TrackedWindow? FindTrackedWindow(Window window)
        => _tracked.FirstOrDefault(t => t.WindowRef.TryGetTarget(out var w) && ReferenceEquals(w, window));

    private void CleanupDeadEntries()
    {
        for (int i = _tracked.Count - 1; i >= 0; i--)
        {
            if (_tracked[i].WindowRef.TryGetTarget(out _))
                continue;

            _tracked[i].Detach();
            _tracked.RemoveAt(i);
        }
    }

    private void RemoveDeadEntry(TrackedWindow tracked)
    {
        lock (_sync)
        {
            if (_tracked.Remove(tracked))
                tracked.Detach();
        }
    }

    private sealed class TrackedWindow
    {
        private readonly WindowPlacementTracker _owner;
        private readonly EventHandler _closedHandler;

        public WeakReference<Window> WindowRef { get; }
        public long AdapterLuid { get; private set; }
        public uint TargetId { get; private set; }
        public string LastKnownFriendlyName { get; private set; } = string.Empty;
        public string WindowName { get; }
        public WindowPlacementOptions Options { get; private set; }

        public TrackedWindow(WindowPlacementTracker owner, Window window, DisplayManager.MonitorInfo monitor, WindowPlacementOptions options, ILogger? log)
        {
            _owner = owner;
            WindowRef = new WeakReference<Window>(window);
            WindowName = string.IsNullOrWhiteSpace(window.Title) ? window.GetType().Name : window.Title;
            _closedHandler = (_, _) => _owner.HandleWindowClosed(this);
            window.Closed += _closedHandler;

            Update(monitor, options, log);
        }

        public void Update(DisplayManager.MonitorInfo monitor, WindowPlacementOptions options, ILogger? log)
        {
            AdapterLuid = monitor.AdapterLuid;
            TargetId = monitor.TargetId;
            LastKnownFriendlyName = monitor.FriendlyName;
            Options = options;

            log?.LogDebug("Tracking window {window} on monitor {monitor}", WindowName, monitor.ToString());
        }

        public void Detach()
        {
            if (WindowRef.TryGetTarget(out var window))
                window.Closed -= _closedHandler;
        }
    }
}

/// <summary>Options describing how a window was positioned.</summary>
public readonly record struct WindowPlacementOptions(bool Maximize, double? WidthDip, double? HeightDip, double MarginDip);
