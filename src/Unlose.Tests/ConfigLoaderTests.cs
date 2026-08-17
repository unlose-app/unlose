using Unlose.Core.Config;
using Xunit;

namespace Unlose.Tests;

public class ConfigLoaderTests
{
    [Fact]
    public async Task LoadAsync_FileNotExist_ReturnsDefault()
    {
        var config = await ConfigLoader.LoadAsync("nonexistent_config_12345.json");
        Assert.NotNull(config);
        Assert.NotNull(config.Snapshot);
        Assert.NotNull(config.Agent);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrip_PreservesValues()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            var original = new UnloseConfig
            {
                Snapshot = new SnapshotConfig { IntervalHours = 2, MaxCount = 5 },
                Service = new ServiceConfig { PipeName = "TestPipe" }
            };
            await ConfigLoader.SaveAsync(original, tempPath);
            var loaded = await ConfigLoader.LoadAsync(tempPath);
            Assert.Equal(2, loaded.Snapshot.IntervalHours);
            Assert.Equal(5, loaded.Snapshot.MaxCount);
            Assert.Equal("TestPipe", loaded.Service.PipeName);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task LoadAsync_FileNotExist_NotificationLevelDefaultsToAll()
    {
        var config = await ConfigLoader.LoadAsync("nonexistent_config_12345.json");
        Assert.Equal("all", config.Snapshot.NotificationLevel);
    }

    [Fact]
    public async Task SaveAndLoad_NotificationLevel_RoundTrips()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            var original = new UnloseConfig
            {
                Snapshot = new SnapshotConfig { NotificationLevel = "failures-only" }
            };
            await ConfigLoader.SaveAsync(original, tempPath);
            var loaded = await ConfigLoader.LoadAsync(tempPath);
            Assert.Equal("failures-only", loaded.Snapshot.NotificationLevel);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
