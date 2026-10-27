using System.Text;
using Avalonia.Threading;

namespace CursorTranslator.Services;

/// <summary>Coalesces streamed text deltas into UI updates at most every 70 ms.</summary>
internal sealed class UiStreamBuffer : IDisposable
{
    private readonly object _sync = new();
    private readonly StringBuilder _content = new();
    private readonly Action<string> _onUpdate;
    private readonly Action<string>? _onComplete;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private bool _dirty;
    private int _disposed;

    public UiStreamBuffer(Action<string> onUpdate, Action<string>? onComplete = null)
    {
        _onUpdate = onUpdate;
        _onComplete = onComplete;
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    public void Append(string delta)
    {
        if (string.IsNullOrEmpty(delta) || Volatile.Read(ref _disposed) != 0) return;
        lock (_sync)
        {
            if (_disposed != 0) return;
            _content.Append(delta);
            _dirty = true;
        }
    }

    public void Complete(string finalContent)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_sync)
        {
            if (_disposed != 0) return;
            _content.Clear();
            _content.Append(finalContent);
            _dirty = false;
        }

        RunOnUiThread(() =>
        {
            _timer.Stop();
            try
            {
                if (_onComplete is not null) _onComplete(finalContent);
                else _onUpdate(finalContent);
            }
            catch (Exception ex)
            {
                AppLog.Error("Streaming output", "Could not render completed stream output.", ex);
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        RunOnUiThread(() =>
        {
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
        });
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        string snapshot;
        lock (_sync)
        {
            if (!_dirty || _disposed != 0) return;
            snapshot = _content.ToString();
            _dirty = false;
        }

        try
        {
            _onUpdate(snapshot);
        }
        catch (Exception ex)
        {
            AppLog.Error("Streaming output", "Could not render partial stream output.", ex);
        }
    }

    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action, DispatcherPriority.Render);
    }
}
