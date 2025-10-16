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

    public event EventHandler? TrackedWindowsChanged;

    /// <summary>Records the placement for the supplied window/monitor pair.</summary>
    public void Record(Window window, DisplayManager.MonitorInfo monitor, WindowPlacementOptions options, ILogger? log = null)
    {
        if (window is null) throw new ArgumentNullException(nameof(window));

        var changed = false;

        lock (_sync)
        {
            changed |= CleanupDeadEntriesUnsafe();

            var existing = FindTrackedWindow(window);
            if (existing != null)
            {
                existing.Update(monitor, options, log);
                return;
            }

            var trackedWindow = new TrackedWindow(this, window, monitor, options, log);
            _tracked.Add(trackedWindow);
            changed = true;
        }

        if (changed)
            OnTrackedWindowsChanged();
    }

    /// <summary>Stops tracking the supplied window (e.g., before disposing it manually).</summary>
    public void Forget(Window window)
    {
        if (window is null) throw new ArgumentNullException(nameof(window));

        var changed = false;

        lock (_sync)
        {
            var tracked = FindTrackedWindow(window);
            if (tracked is null)
                return;

            tracked.Detach();
            _tracked.Remove(tracked);
            changed = true;
        }

        if (changed)
            OnTrackedWindowsChanged();
    }

    /// <summary>
    /// Attempts to re-apply placements for all tracked windows. Missing monitors fall back to the primary display.
    /// </summary>
    public void RestoreTrackedWindows(ILogger? log = null)
    {
        List<TrackedWindow> snapshot;
        var removedDead = false;

        lock (_sync)
        {
            removedDead = CleanupDeadEntriesUnsafe();
            snapshot = _tracked.ToList();
        }

        if (removedDead)
            OnTrackedWindowsChanged();

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

    public IReadOnlyList<TrackedWindowInfo> GetTrackedWindows()
    {
        List<TrackedWindow> snapshot;

        lock (_sync)
        {
            CleanupDeadEntriesUnsafe();
            snapshot = _tracked.ToList();
        }

        return snapshot.Select(t => t.ToInfo()).ToList();
    }

    public bool TryGetWindow(Guid id, out Window? window)
    {
        TrackedWindow? tracked;

        lock (_sync)
        {
            tracked = _tracked.FirstOrDefault(t => t.Id == id);
            if (tracked is null)
            {
                window = null;
                return false;
            }

            if (tracked.WindowRef.TryGetTarget(out var resolved))
            {
                window = resolved;
                return true;
            }
        }

        RemoveDeadEntry(tracked!);
        window = null;
        return false;
    }

    private void HandleWindowClosed(TrackedWindow tracked)
    {
        var removed = false;

        lock (_sync)
        {
            tracked.Detach();
            removed = _tracked.Remove(tracked);
        }

        if (removed)
            OnTrackedWindowsChanged();
    }

    private TrackedWindow? FindTrackedWindow(Window window)
        => _tracked.FirstOrDefault(t => t.WindowRef.TryGetTarget(out var w) && ReferenceEquals(w, window));

    private bool CleanupDeadEntriesUnsafe()
    {
        var removed = false;
        for (int i = _tracked.Count - 1; i >= 0; i--)
        {
            if (_tracked[i].WindowRef.TryGetTarget(out _))
                continue;

            _tracked[i].Detach();
            _tracked.RemoveAt(i);
            removed = true;
        }
        return removed;
    }

    private void RemoveDeadEntry(TrackedWindow tracked)
    {
        var removed = false;

        lock (_sync)
        {
            removed = _tracked.Remove(tracked);
        }

        if (removed)
        {
            tracked.Detach();
            OnTrackedWindowsChanged();
        }
    }

    private void OnTrackedWindowsChanged()
        => TrackedWindowsChanged?.Invoke(this, EventArgs.Empty);

    private sealed class TrackedWindow
    {
        private readonly WindowPlacementTracker _owner;
        private readonly EventHandler _closedHandler;

        public Guid Id { get; } = Guid.NewGuid();

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

        public TrackedWindowInfo ToInfo()
        {
            var isAlive = WindowRef.TryGetTarget(out _);
            return new TrackedWindowInfo(Id, WindowName, isAlive, AdapterLuid, TargetId, LastKnownFriendlyName, Options);
        }
    }
}

/// <summary>Options describing how a window was positioned.</summary>
public readonly record struct WindowPlacementOptions(bool Maximize, double? WidthDip, double? HeightDip, double MarginDip);

/// <summary>Snapshot information about a window tracked by <see cref="WindowPlacementTracker"/>.</summary>
public sealed record class TrackedWindowInfo(
    Guid Id,
    string WindowName,
    bool IsAlive,
    long AdapterLuid,
    uint TargetId,
    string LastKnownFriendlyName,
    WindowPlacementOptions Options);
