using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Hid;

/// <summary>
/// 功 能： 管理单个 HID 句柄的报文 I/O、故障类别及在途操作收尾。
/// 2026/10/20 20:20:56 CodexSandboxOffline
/// </summary>
public sealed class HidDeviceHandle : IDisposable
{
    private readonly IHidIo _io;
    private readonly object _sync = new();
    private Task<HidInputResult>? _pendingRead;
    private Task<HidIoResult>? _pendingWrite;
    private TaskCompletionSource? _idle;
    private HidIoResult _lastResult = new(HidIoStatus.Success);
    private HidIoResult? _fault;
    private int _active;
    private bool _disposed;
    private long _failures;

    public HidDeviceInfo Info { get; }
    public HidIoResult LastResult { get { lock (_sync) return _lastResult; } }
    public bool NeedsRecovery { get { lock (_sync) return _disposed || _fault.HasValue; } }
    public Task Drained { get { lock (_sync) return _idle?.Task ?? Task.CompletedTask; } }

    internal HidDeviceHandle(HidDeviceInfo info, FileStream stream) : this(info, new HidFileIo(stream)) { }
    internal HidDeviceHandle(HidDeviceInfo info, IHidIo io) { Info = info; _io = io; }

    private bool Begin()
    {
        lock (_sync)
        {
            if (_disposed || _fault.HasValue) return false;
            if (_active++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    private void End()
    {
        lock (_sync)
            if (--_active == 0) _idle!.TrySetResult();
    }

    private HidIoResult Unavailable()
    {
        lock (_sync) return _fault ?? new(HidIoStatus.Disposed, 6);
    }

    private HidIoResult Record(HidIoResult result)
    {
        lock (_sync)
        {
            if (result.Status is not (HidIoStatus.Success or HidIoStatus.Timeout or HidIoStatus.NoData or HidIoStatus.Unsupported))
                _failures++;
            if (!_disposed && result.RequiresReopen && !_fault.HasValue)
            {
                _fault = result;
                Log.Warn($"HID 句柄失效 #{_failures}: {Info.DevicePath} {result.Status} Win32={result.Error}");
            }
            else if (!_disposed && result.Status is not (HidIoStatus.Success or HidIoStatus.Timeout or HidIoStatus.NoData or HidIoStatus.Unsupported)
                && (_failures == 1 || _failures % 60 == 0))
                Log.Debug($"HID I/O 失败 #{_failures}: {Info.DevicePath} {result.Status} Win32={result.Error}");
            _lastResult = _fault ?? (_disposed ? new(HidIoStatus.Disposed, 6) : result);
            return result;
        }
    }

    public byte[]? GetFeature(byte reportId, int? reportLength = null) => GetFeature(reportId, out _, reportLength);
    public byte[]? GetFeature(byte reportId, out int error, int? reportLength = null)
    {
        error = 0;
        int len = reportLength ?? Info.FeatureReportLength;
        if (len == 0) { Record(new(HidIoStatus.Unsupported)); return null; }
        if (len < 1 || len > Info.FeatureReportLength) throw new ArgumentOutOfRangeException(nameof(reportLength));
        if (!Begin()) { error = Unavailable().Error; return null; }
        try
        {
            var buffer = new byte[len];
            buffer[0] = reportId;
            bool success = _io.GetFeature(buffer, out error);
            Record(success ? new(HidIoStatus.Success) : HidIoResult.FromError(error));
            if (success && NeedsRecovery) { error = Unavailable().Error; return null; }
            return success ? buffer : null;
        }
        catch (Exception ex)
        {
            var result = Record(HidIoResult.FromException(ex));
            error = result.Error;
            return null;
        }
        finally { End(); }
    }

    public byte? GetOutputReportId()
    {
        if (!Begin()) return null;
        try
        {
            var id = _io.GetOutputReportId(out int error);
            if (error != 0) Record(HidIoResult.FromError(error));
            return NeedsRecovery ? null : id;
        }
        catch (Exception ex) { Record(HidIoResult.FromException(ex)); return null; }
        finally { End(); }
    }

    public bool SetFeature(byte reportId, byte[] payload, int? reportLength = null)
    {
        int len = reportLength ?? Info.FeatureReportLength;
        if (len == 0) { Record(new(HidIoStatus.Unsupported)); return false; }
        if (len < 1 || len > Info.FeatureReportLength) throw new ArgumentOutOfRangeException(nameof(reportLength));
        if (!Begin()) return false;
        try
        {
            var buffer = new byte[len];
            buffer[0] = reportId;
            Array.Copy(payload, 0, buffer, 1, Math.Min(payload.Length, len - 1));
            bool success = _io.SetFeature(buffer, out int error);
            Record(success ? new(HidIoStatus.Success) : HidIoResult.FromError(error));
            return success && !NeedsRecovery;
        }
        catch (Exception ex) { Record(HidIoResult.FromException(ex)); return false; }
        finally { End(); }
    }

    public bool WriteOutput(byte[] report, TimeSpan timeout, bool quiet = false)
    {
        Task<HidIoResult> task;
        lock (_sync)
        {
            // 超时不会结束底层写入；旧写未完成时不能再向同一句柄排第二份。
            if (_pendingWrite is { IsCompleted: false }) { Record(new(HidIoStatus.InProgress)); return false; }
            if (!Begin()) return false;
            task = _pendingWrite = WriteCoreAsync((byte[])report.Clone());
        }
        var result = task.Wait(timeout) ? task.GetAwaiter().GetResult() : Record(new(HidIoStatus.Timeout));
        if (result.Status == HidIoStatus.Success && !NeedsRecovery) return true;
        if (!quiet) Log.Warn($"WriteOutput 失败: {result.Status} Win32={result.Error}");
        return false;
    }

    private async Task<HidIoResult> WriteCoreAsync(byte[] report)
    {
        try { await _io.WriteAsync(report).ConfigureAwait(false); return Record(new(HidIoStatus.Success)); }
        catch (Exception ex) { return Record(HidIoResult.FromException(ex)); }
        finally { End(); }
    }

    public byte[]? ReadInput(TimeSpan timeout) => ReadInputAsync(timeout).GetAwaiter().GetResult();
    public async Task<byte[]?> ReadInputAsync(TimeSpan timeout)
        => (await ReadInputResultAsync(timeout).ConfigureAwait(false)).Data;

    public async Task<HidInputResult> ReadInputResultAsync(TimeSpan timeout)
    {
        Task<HidInputResult> pending;
        lock (_sync)
        {
            if (_disposed || _fault.HasValue) return new(Unavailable());
            if (Info.InputReportLength == 0) return new(Record(new(HidIoStatus.Unsupported)));
            if (_pendingRead == null)
            {
                if (!Begin()) return new(Unavailable());
                _pendingRead = ReadCoreAsync();
            }
            pending = _pendingRead;
        }
        HidInputResult read;
        try { read = await pending.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { return new(Record(new(HidIoStatus.Timeout))); }
        lock (_sync)
        {
            if (!ReferenceEquals(_pendingRead, pending)) return new(new(HidIoStatus.NoData));
            _pendingRead = null;
            return _disposed ? new(new(HidIoStatus.Disposed, 6)) : read;
        }
    }

    private async Task<HidInputResult> ReadCoreAsync()
    {
        try
        {
            var buffer = new byte[Info.InputReportLength];
            int count = await _io.ReadAsync(buffer).ConfigureAwait(false);
            if (count <= 0) return new(Record(new(HidIoStatus.Disconnected, 1167)));
            return new(Record(new(HidIoStatus.Success)), buffer.AsSpan(0, count).ToArray());
        }
        catch (Exception ex) { return new(Record(HidIoResult.FromException(ex))); }
        finally { End(); }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _lastResult = _fault ?? new(HidIoStatus.Disposed, 6);
            if (_active++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try { _io.Dispose(); End(); }
        catch (Exception ex) { Log.Error($"HID 句柄关闭失败，保持隔离: {Info.DevicePath}", ex); }
    }
}
