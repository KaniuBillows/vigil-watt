using VigilWatt.Core.Devices;
using VigilWatt.Core.Models;
using VigilWatt.Core.Storage;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>覆盖过期、重连待确认、缓存去重及同值成功读数的真实历史采样。</summary>
public sealed class ReadingFreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static DeviceStatus Reading() => new()
    {
        Id = "test:one", Name = "设备", Source = DeviceSource.Hid24G, BatteryPercent = 60,
        BatteryObservedAt = Now, ReadingConnectionConfirmed = true,
    };

    [Fact]
    public void BoundaryAndUnknownEvidenceFailConservatively()
    {
        var status = Reading();
        Assert.True(ReadingFreshness.IsFresh(status, Now.AddMinutes(5)));
        Assert.True(ReadingFreshness.IsStale(status, Now.AddMinutes(5).AddTicks(1)));
        Assert.False(ReadingFreshness.IsFresh(status, Now.AddTicks(-1)));
        status.BatteryObservedAt = null;
        Assert.True(ReadingFreshness.IsStale(status, Now));
        status.BatteryPercent = null;
        Assert.False(ReadingFreshness.IsStale(status, Now));
    }

    [Fact]
    public void ConnectionAndNameUpdatesCannotRefreshBatteryEvidence()
    {
        var status = Reading();
        status.LastUpdated = Now.AddMinutes(10);
        status.ConnectionObservedAt = Now.AddMinutes(10);
        status.Name = "新名称";
        Assert.True(ReadingFreshness.IsStale(status, Now.AddMinutes(10)));
        var copy = status.Clone();
        Assert.Equal(status.BatteryObservedAt, copy.BatteryObservedAt);
        Assert.Equal(status.ConnectionObservedAt, copy.ConnectionObservedAt);
        Assert.True(copy.ReadingConnectionConfirmed);
    }

    [Fact]
    public void OfflineAndReconnectRequireANewSuccessfulReading()
    {
        var status = Reading();
        status.IsOnline = false;
        status.ReadingConnectionConfirmed = false;
        Assert.False(ReadingFreshness.IsFresh(status, Now));
        status.IsOnline = true;
        Assert.True(ReadingFreshness.IsStale(status, Now));
        status.ObserveBattery(Now.AddSeconds(1));
        Assert.True(ReadingFreshness.IsFresh(status, Now.AddSeconds(1)));
    }

    [Fact]
    public void CacheAndMetadataNeverBecomeAdditionalDatabaseSamples()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vigilwatt-freshness-{Guid.NewGuid():N}.db");
        try
        {
            using var history = new HistoryStore(path);
            var recorder = new ReadingSampleRecorder();
            var status = Reading();
            Assert.True(recorder.Record(status, Now, history.Insert));
            for (int i = 0; i < 100; i++)
            {
                status.LastUpdated = Now.AddSeconds(i);
                Assert.False(recorder.Record(status, Now.AddSeconds(i), history.Insert));
            }
            status.ObserveBattery(Now.AddMinutes(1));
            Assert.True(recorder.Record(status, Now.AddMinutes(1), history.Insert));
            status.IsOnline = false;
            Assert.False(recorder.Record(status, Now.AddMinutes(2), history.Insert));
            status.IsOnline = true;
            status.ReadingConnectionConfirmed = false;
            Assert.False(recorder.Record(status, Now.AddMinutes(2), history.Insert));
            var samples = history.Query(status.Id, Now.AddMinutes(-1), Now.AddMinutes(10));
            Assert.Equal(2, samples.Count);
            Assert.Equal(new[] { Now, Now.AddMinutes(1) }, samples.Select(s => s.Timestamp));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    [Fact]
    public void FailedInsertCanRetryAndConcurrentSamplesAreDeduplicated()
    {
        var recorder = new ReadingSampleRecorder();
        var status = Reading();
        Assert.Throws<IOException>(() => recorder.Record(status, Now, _ => throw new IOException("injected")));
        int inserted = 0;
        Parallel.For(0, 100, _ => recorder.Record(status, Now, sample =>
        {
            Assert.Equal(Now, sample.LastUpdated);
            Interlocked.Increment(ref inserted);
        }));
        Assert.Equal(1, inserted);
    }
}
