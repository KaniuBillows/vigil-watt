using System.Text.Json;
using VigilWatt.App.Services;
using VigilWatt.Core.Settings;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 贴片左右定位、独立拖动记忆与旧配置兼容的回归测试。
/// 2026/10/20 19:43:26 CodexSandboxOffline
/// </summary>
public class TaskbarPositionTests
{
    private static readonly PixelRect Bar = new(0, 1032, 1920, 48);
    private static readonly PixelRect Tray = new(1700, 1032, 220, 48);

    [Fact]
    public void LegacySettingsKeepRightSideAndExistingDraggedPosition()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """{"TaskbarBatteryOffsetX":-420,"TaskbarBatteryOffsetDpi":144}""")!;
        var offset = TaskbarBatteryPosition.GetOffset(settings, settings.TaskbarBatteryPosition);
        var actual = TaskbarLayout.Place(Bar, Tray, 200, 36, offset.Offset, 6, out _, settings.TaskbarBatteryPosition);
        Assert.Equal(TaskbarBatteryPosition.Right, settings.TaskbarBatteryPosition);
        Assert.Equal((-420, 144), offset);
        Assert.Equal((1074, 1038), actual);
        Assert.Equal((0, 0), TaskbarBatteryPosition.GetOffset(settings, TaskbarBatteryPosition.Left));
    }

    [Theory]
    [InlineData(-1920, 200)]
    [InlineData(0, 200)]
    [InlineData(1920, 200)]
    [InlineData(1920, 500)]
    public void LeftSideAnchorsToTaskbarStartRegardlessOfScreenOriginAndPillWidth(int origin, int width)
    {
        var bar = new PixelRect(origin, 1032, 1920, 48);
        var tray = new PixelRect(origin + 1700, 1032, 220, 48);
        var actual = TaskbarLayout.Place(bar, tray, width, 36, 30, 6, out int applied, TaskbarBatteryPosition.Left);
        Assert.Equal((origin + 36, 1038), actual);
        Assert.Equal(30, applied);
    }

    [Fact]
    public void LeftSideRemainsAnchoredWhenTrayIsMissingOrChangesWidth()
    {
        var initial = TaskbarLayout.Place(Bar, Tray, 200, 36, 0, 6, out _, TaskbarBatteryPosition.Left);
        var withoutTray = TaskbarLayout.Place(Bar, default, 200, 36, 0, 6, out _, TaskbarBatteryPosition.Left);
        var widerTray = TaskbarLayout.Place(Bar, new(1500, 1032, 420, 48), 200, 36, 0, 6,
            out _, TaskbarBatteryPosition.Left);
        Assert.Equal((6, 1038), initial);
        Assert.Equal(initial, withoutTray);
        Assert.Equal(initial, widerTray);
    }

    [Fact]
    public void LeftDragCanReverseImmediatelyAfterClampingAtEitherEdge()
    {
        var atLeft = TaskbarLayout.Place(Bar, Tray, 200, 36, -5000, 6, out int leftOffset, TaskbarBatteryPosition.Left);
        var awayFromLeft = TaskbarLayout.Place(Bar, Tray, 200, 36, leftOffset + 10, 6,
            out _, TaskbarBatteryPosition.Left);
        var atRight = TaskbarLayout.Place(Bar, Tray, 200, 36, 5000, 6, out int rightOffset, TaskbarBatteryPosition.Left);
        var awayFromRight = TaskbarLayout.Place(Bar, Tray, 200, 36, rightOffset - 10, 6,
            out _, TaskbarBatteryPosition.Left);
        Assert.Equal(4, atLeft.X);
        Assert.Equal(atLeft.X + 10, awayFromLeft.X);
        Assert.Equal(1716, atRight.X);
        Assert.Equal(atRight.X - 10, awayFromRight.X);
    }

    [Fact]
    public void SwitchingSidesPreservesBothOffsetsAndDpiThroughJsonRoundTrip()
    {
        var settings = new AppSettings();
        TaskbarBatteryPosition.SetOffset(settings, TaskbarBatteryPosition.Right, -45, 96);
        settings.TaskbarBatteryPosition = TaskbarBatteryPosition.Left;
        TaskbarBatteryPosition.SetOffset(settings, settings.TaskbarBatteryPosition, 60, 144);
        settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(TaskbarBatteryPosition.Left, settings.TaskbarBatteryPosition);
        Assert.Equal((-45, 96), TaskbarBatteryPosition.GetOffset(settings, TaskbarBatteryPosition.Right));
        Assert.Equal((60, 144), TaskbarBatteryPosition.GetOffset(settings, TaskbarBatteryPosition.Left));
        var leftOffset = TaskbarBatteryPosition.GetOffset(settings, settings.TaskbarBatteryPosition);
        int scaled = TaskbarLayout.ScaleOffset(leftOffset.Offset, (uint)leftOffset.Dpi, 96);
        Assert.Equal(46, TaskbarLayout.Place(Bar, Tray, 200, 36, scaled, 6, out _, settings.TaskbarBatteryPosition).X);
        settings.TaskbarBatteryPosition = TaskbarBatteryPosition.Right;
        var rightOffset = TaskbarBatteryPosition.GetOffset(settings, settings.TaskbarBatteryPosition);
        Assert.Equal(1449, TaskbarLayout.Place(Bar, Tray, 200, 36, rightOffset.Offset, 6, out _, settings.TaskbarBatteryPosition).X);
        Assert.Equal((60, 144), TaskbarBatteryPosition.GetOffset(settings, TaskbarBatteryPosition.Left));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("left")]
    [InlineData("invalid")]
    public void UnknownPositionKeepsLegacyRightOffsetAndGeometry(string? position)
    {
        var settings = new AppSettings { TaskbarBatteryOffsetX = -50, TaskbarBatteryLeftOffsetX = 80 };
        var offset = TaskbarBatteryPosition.GetOffset(settings, position);
        Assert.Equal((-50, 0), offset);
        Assert.Equal((1444, 1038), TaskbarLayout.Place(Bar, Tray, 200, 36, offset.Offset, 6, out _, position));
    }

    [Fact]
    public void VerticalTaskbarUsesTopEndpointForLeftPosition()
    {
        var bar = new PixelRect(0, 0, 48, 1080);
        var tray = new PixelRect(0, 860, 48, 220);
        Assert.Equal((0, 26), TaskbarLayout.Place(bar, tray, 200, 36, 20, 6, out _, TaskbarBatteryPosition.Left));
    }
}
