using System;
using System.IO;
using System.Linq;
using DisplayManagerLib;
using DisplayManager = DisplayManagerLib.DisplayManager;

namespace DisplayManagerLib.Tests;

public class FriendlyNameStoreTests
{
    [Fact]
    public void CreateMonitorKey_CombinesLuidAndTarget()
    {
        var monitor = new DisplayManager.MonitorInfo
        {
            AdapterLuid = 0x1A2B3C4D5E6F7081,
            TargetId = 3,
            DeviceName = "\\\\.\\DISPLAY1"
        };

        var key = FriendlyNameStore.CreateMonitorKey(monitor);
        Assert.Equal("1A2B3C4D5E6F7081-3", key);
    }

    [Fact]
    public void SetAndRetrieveFriendlyName_RoundTrips()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "names.json");
        var store = new FriendlyNameStore(path);

        var monitor = new DisplayManager.MonitorInfo { AdapterLuid = 42, TargetId = 7 };
        store.SetFriendlyName(monitor, "Work Display");
        store.Save();

        var reload = new FriendlyNameStore(path);
        reload.Load();

        var name = reload.GetFriendlyName(monitor);
        Assert.Equal("Work Display", name);
    }

    [Fact]
    public void TryGetMonitorKey_IsCaseInsensitive()
    {
        var store = new FriendlyNameStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        try
        {
            store.SetFriendlyName("abc", "Studio");
            Assert.True(store.TryGetMonitorKey("studio", out var key));
            Assert.Equal("abc", key);
        }
        finally
        {
            if (File.Exists(store.FilePath))
                File.Delete(store.FilePath);
        }
    }

    [Fact]
    public void KnownFriendlyNames_FiltersAndSorts()
    {
        var store = new FriendlyNameStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        store.SetFriendlyName("a", "Gamma");
        store.SetFriendlyName("b", "alpha");
        store.SetFriendlyName("c", " ");

        var names = store.KnownFriendlyNames().ToArray();

        Assert.Equal(new[] { "alpha", "Gamma" }, names);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
