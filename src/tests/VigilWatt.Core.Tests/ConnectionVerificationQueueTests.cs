using VigilWatt.Core.Ble;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 验证同设备只执行一次连接确认、增量合并与快速重连保留最新请求。
/// 2026/10/20 20:12:00 CodexSandboxOffline
/// </summary>
public class ConnectionVerificationQueueTests
{
    private static readonly IReadOnlyDictionary<string, object> Empty = new Dictionary<string, object>();

    [Fact]
    public void RepeatedEventsMergeLatestPropertiesWithoutStartingAnotherRead()
    {
        var queue = new ConnectionVerificationQueue(new object());
        Assert.NotNull(queue.Enqueue("id", new Dictionary<string, object> { ["battery"] = 50 }, "mouse", 1));
        Assert.Null(queue.Enqueue("ID", new Dictionary<string, object> { ["battery"] = 60, ["kind"] = "mouse" }, null, 1));
        var latest = queue.Latest("id", 1)!;
        Assert.Equal(60, latest.Props["battery"]);
        Assert.Equal("mouse", latest.Props["kind"]);
        Assert.Equal("mouse", latest.Name);
        Assert.Null(queue.Complete("id", 1, _ => true));
        Assert.Null(queue.Latest("id", 1));
        Assert.NotNull(queue.Enqueue("id", Empty, null, 1));
    }

    [Fact]
    public void OneHundredReconnectsCannotAccumulatePendingReads()
    {
        var queue = new ConnectionVerificationQueue(new object());
        Assert.NotNull(queue.Enqueue("id", Empty, null, 1));
        for (long token = 2; token <= 101; token++)
        {
            queue.Invalidate("id");
            Assert.Null(queue.Enqueue("id", Empty, null, token));
        }
        Assert.Null(queue.Latest("id", 1));
        var next = queue.Complete("id", 1, _ => true);
        Assert.Equal(101L, next!.Epoch);
        Assert.Null(queue.Complete("id", 1, _ => true));
        Assert.NotNull(queue.Latest("id", 101));
        Assert.Null(queue.Complete("id", 101, _ => true));
    }

    [Fact]
    public void WatcherRebuildInvalidatesPayloadButStillWaitsForOldNativeCall()
    {
        var queue = new ConnectionVerificationQueue(new object());
        queue.Enqueue("id", Empty, "old", 1);
        queue.Clear();
        Assert.Null(queue.Latest("id", 1));
        Assert.Null(queue.Enqueue("id", Empty, "new", 2));
        Assert.Equal("new", queue.Complete("id", 1, _ => true)!.Name);
    }

    [Fact]
    public void DisconnectedOrDisposedConnectionCannotStartQueuedRequest()
    {
        var queue = new ConnectionVerificationQueue(new object());
        queue.Enqueue("id", Empty, null, 1);
        queue.Enqueue("id", Empty, null, 2);
        Assert.Null(queue.Complete("id", 1, _ => false));
        Assert.Null(queue.Latest("id", 2));
        Assert.NotNull(queue.Enqueue("id", Empty, null, 3));
        queue.Invalidate("id");
        Assert.Null(queue.Complete("id", 3, _ => true));
    }

    [Fact]
    public void DifferentEndpointsHaveIndependentInFlightReads()
    {
        var queue = new ConnectionVerificationQueue(new object());
        Assert.NotNull(queue.Enqueue("a", Empty, null, 1));
        Assert.NotNull(queue.Enqueue("b", Empty, null, 2));
        queue.Invalidate("a");
        Assert.NotNull(queue.Latest("b", 2));
        Assert.Null(queue.Complete("a", 1, _ => true));
        Assert.NotNull(queue.Latest("b", 2));
    }

    [Fact]
    public void PayloadCopiesCallerPropertiesAndDoesNotReusePriorConnectionBattery()
    {
        var queue = new ConnectionVerificationQueue(new object());
        var props = new Dictionary<string, object> { ["battery"] = 50 };
        queue.Enqueue("id", props, null, 1);
        props["battery"] = 10;
        Assert.Equal(50, queue.Latest("id", 1)!.Props["battery"]);
        queue.Enqueue("id", Empty, null, 2);
        Assert.Empty(queue.Complete("id", 1, _ => true)!.Props);
    }
}
