using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DisplayManagerLib;

/// <summary>
/// Provides persistence for user-defined friendly names bound to specific monitor identities.
/// </summary>
public sealed class FriendlyNameStore
{
    private readonly Dictionary<string, string> _assignments = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _serializerOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public FriendlyNameStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DisplayManager", "friendly-monitor-names.json");
    }

    public string FilePath => _filePath;

    public IReadOnlyDictionary<string, string> Assignments => _assignments;

    public static string CreateMonitorKey(DisplayManager.MonitorInfo monitor)
        => $"{monitor.AdapterLuid:X16}-{monitor.TargetId}";

    public void Load()
    {
        _assignments.Clear();
        if (!File.Exists(_filePath))
            return;

        using var stream = File.OpenRead(_filePath);
        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
        foreach (var pair in data)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                continue;
            _assignments[pair.Key] = pair.Value.Trim();
        }
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var stream = File.Create(_filePath);
        JsonSerializer.Serialize(stream, _assignments, _serializerOptions);
    }

    public string? GetFriendlyName(DisplayManager.MonitorInfo monitor)
    {
        var key = CreateMonitorKey(monitor);
        return _assignments.TryGetValue(key, out var name) ? name : null;
    }

    public void SetFriendlyName(DisplayManager.MonitorInfo monitor, string? friendlyName)
        => SetFriendlyName(CreateMonitorKey(monitor), friendlyName);

    public void SetFriendlyName(string monitorKey, string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(monitorKey))
            throw new ArgumentException("Monitor key cannot be empty.", nameof(monitorKey));

        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            _assignments.Remove(monitorKey);
            return;
        }

        _assignments[monitorKey] = friendlyName.Trim();
    }

    public bool TryGetMonitorKey(string friendlyName, out string monitorKey)
    {
        foreach (var pair in _assignments)
        {
            if (string.Equals(pair.Value, friendlyName, StringComparison.OrdinalIgnoreCase))
            {
                monitorKey = pair.Key;
                return true;
            }
        }

        monitorKey = string.Empty;
        return false;
    }

    public IEnumerable<string> KnownFriendlyNames()
        => _assignments.Values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase);
}
