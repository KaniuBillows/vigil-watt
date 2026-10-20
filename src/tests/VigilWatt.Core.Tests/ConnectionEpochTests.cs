using VigilWatt.Core.Ble;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 验证断连、移除、重建与新连接后的异步晚到结果不会恢复旧状态。
/// 2026/10/20 20:04:52 CodexSandboxOffline
/// </summary>
public class ConnectionEpochTests
{
    [Theory]
    [InlineData("disconnect")]
    [InlineData("remove")]
    [InlineData("rebuild")]
    [InlineData("new-reading")]
    public async Task LateResultCannotPublishAfterConnectionInvalidation(string reason)
    {
        var epochs = new ConnectionEpochs(new object());
        long old = epochs.Observe("device", true);
        var result = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new List<int>();
        async Task<bool> ReadAsync()
        {
            int percent = await result.Task;
            return epochs.TryCommit("device", old, () => published.Add(percent));
        }
        var read = ReadAsync();
        switch (reason)
        {
            case "disconnect": epochs.Observe("device", false); break;
            case "rebuild": epochs.Clear(); break;
            default: epochs.Remove("device"); break;
        }
        if (reason == "new-reading")
        {
            long fresh = epochs.Observe("device", true);
            Assert.True(epochs.TryCommit("device", fresh, () => published.Add(80)));
        }
        result.SetResult(20);
        Assert.False(await read);
        Assert.DoesNotContain(20, published);
    }

    [Fact]
    public void SamePathReconnectNeverAcceptsOldGenerationAcrossOneHundredCycles()
    {
        var epochs = new ConnectionEpochs(new object());
        int published = 0;
        for (int i = 0; i < 100; i++)
        {
            long old = epochs.Observe("device", true);
            epochs.Observe("device", false);
            long current = epochs.Observe("device", true);
            Assert.False(epochs.TryCommit("device", old, () => published--));
            Assert.True(epochs.TryCommit("DEVICE", current, () => published++));
            epochs.Remove("device");
            Assert.False(epochs.TryCommit("device", current, () => published--));
        }
        Assert.Equal(100, published);
    }

    [Fact]
    public void MissingConnectionPropertyPreservesBothOnlineAndOfflineEvidence()
    {
        var epochs = new ConnectionEpochs(new object());
        long current = epochs.Observe("device", true);
        Assert.Equal(current, epochs.Observe("device", null));
        Assert.True(epochs.IsCurrent("device", current));
        long offline = epochs.Observe("device", false);
        Assert.Equal(offline, epochs.Observe("device", null));
        Assert.False(epochs.IsCurrent("device", offline));
        Assert.False(epochs.IsCurrent("device", current));
    }

    [Fact]
    public void ClearingGenerationsCannotReuseTokenForSameDevice()
    {
        var epochs = new ConnectionEpochs(new object());
        long old = epochs.Observe("device", true);
        epochs.Clear();
        long fresh = epochs.Observe("device", true);
        Assert.NotEqual(old, fresh);
        Assert.False(epochs.IsCurrent("device", old));
    }
}
