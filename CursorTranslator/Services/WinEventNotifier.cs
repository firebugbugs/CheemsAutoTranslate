using System.Runtime.InteropServices;

namespace CursorTranslator.Services;

/// <summary>
/// Wakes the input monitor when the foreground app reports a focus or accessible
/// object change. WinEvents do not contain control text; the monitor must reread it.
/// </summary>
internal sealed class WinEventNotifier : IDisposable
{
    private const uint EventObjectNameChange = 0x800C;
    private const uint EventObjectFocus = 0x8005;
    private const uint EventObjectValueChange = 0x800E;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const uint WmQuit = 0x0012;
    private const uint GaRoot = 2;
    private const int ObjIdWindow = 0;
    private const int ObjIdClient = -4;
    private const int ObjIdCaret = -8;

    private readonly Action _notify;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _threadReady = new(false);
    private Thread? _thread;
    private WinEventProc? _callback;
    private IntPtr _focusHook;
    private IntPtr _valueHook;
    private IntPtr _nameHook;
    private uint _threadId;
    private bool _disposed;

    public WinEventNotifier(Action notify) => _notify = notify;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is { IsAlive: true }) return;

            _threadReady.Reset();
            _thread = new Thread(HookThreadMain)
            {
                IsBackground = true,
                Name = "CursorTranslator WinEvent listener"
            };
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate) thread = _thread;
        if (thread is not { IsAlive: true } || thread == Thread.CurrentThread) return;

        _threadReady.Wait(TimeSpan.FromMilliseconds(500));
        var threadId = Volatile.Read(ref _threadId);
        if (threadId != 0)
            PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);

        thread.Join(TimeSpan.FromSeconds(1));
        lock (_gate)
        {
            if (ReferenceEquals(_thread, thread) && !thread.IsAlive)
                _thread = null;
        }
    }

    private void HookThreadMain()
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        _callback = OnWinEvent;
        var flags = WineventOutOfContext | WineventSkipOwnProcess;
        _focusHook = SetWinEventHook(EventObjectFocus, EventObjectFocus, IntPtr.Zero,
            _callback, 0, 0, flags);
        _valueHook = SetWinEventHook(EventObjectValueChange, EventObjectValueChange, IntPtr.Zero,
            _callback, 0, 0, flags);
        _nameHook = SetWinEventHook(EventObjectNameChange, EventObjectNameChange, IntPtr.Zero,
            _callback, 0, 0, flags);
        _threadReady.Set();

        try
        {
            if (_focusHook == IntPtr.Zero && _valueHook == IntPtr.Zero && _nameHook == IntPtr.Zero)
                return;

            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            if (_focusHook != IntPtr.Zero) UnhookWinEvent(_focusHook);
            if (_valueHook != IntPtr.Zero) UnhookWinEvent(_valueHook);
            if (_nameHook != IntPtr.Zero) UnhookWinEvent(_nameHook);
            _focusHook = IntPtr.Zero;
            _valueHook = IntPtr.Zero;
            _nameHook = IntPtr.Zero;
            _callback = null;
            _threadId = 0;
        }
    }

    private void OnWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (hwnd == IntPtr.Zero
            || objectId is not (ObjIdWindow or ObjIdClient or ObjIdCaret))
            return;

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return;
        var foregroundRoot = GetAncestor(foreground, GaRoot);
        var eventRoot = GetAncestor(hwnd, GaRoot);
        if (foregroundRoot == IntPtr.Zero) foregroundRoot = foreground;
        if (eventRoot == IntPtr.Zero) eventRoot = hwnd;
        if (eventRoot != foregroundRoot) return;

        try { _notify(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Stop();
        _threadReady.Dispose();
    }

    private delegate void WinEventProc(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr moduleHandle,
        WinEventProc callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage message, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr hwnd, uint min, uint max, uint removeMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
