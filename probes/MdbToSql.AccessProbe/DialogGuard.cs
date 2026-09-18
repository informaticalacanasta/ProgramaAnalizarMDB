using System.Runtime.InteropServices;
using System.Text;

namespace MdbToSql.AccessProbe;

internal sealed class DialogCapture
{
    public bool Detected { get; set; }
    public string? Title { get; set; }
}

internal sealed class DialogGuard : IDisposable
{
    private const int WmClose = 0x0010;
    private readonly int _accessPid;
    private readonly Action<string> _onDetected;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private bool _disposed;
    private bool _signaled;

    public DialogGuard(int accessPid, ProbeReport report)
        : this(accessPid, title =>
        {
            if (report.DialogDetected)
            {
                return;
            }

            report.DialogDetected = true;
            report.DialogTitle = title;
            report.Warnings.Add(
                $"Diálogo modal detectado en Access: '{title}'. " +
                "No se pulsa ningún botón; se envía WM_CLOSE para abortar.");
        })
    {
    }

    public DialogGuard(int accessPid, DialogCapture capture)
        : this(accessPid, title =>
        {
            if (capture.Detected)
            {
                return;
            }

            capture.Detected = true;
            capture.Title = title;
        })
    {
    }

    private DialogGuard(int accessPid, Action<string> onDetected)
    {
        _accessPid = accessPid;
        _onDetected = onDetected;
        _thread = new Thread(Watch)
        {
            IsBackground = true,
            Name = "AccessDialogGuard"
        };
        _thread.Start();
    }

    public bool Detected => _signaled;

    private void Watch()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                Native.EnumWindows(OnWindow, IntPtr.Zero);
            }
            catch
            {
                // Best-effort; el probe abortará por timeout si hace falta.
            }

            if (_cts.Token.WaitHandle.WaitOne(250))
            {
                return;
            }
        }
    }

    private bool OnWindow(IntPtr hWnd, IntPtr lParam)
    {
        Native.GetWindowThreadProcessId(hWnd, out var pid);
        if ((int)pid != _accessPid || !Native.IsWindowVisible(hWnd))
        {
            return true;
        }

        var className = ReadClass(hWnd);
        var title = ReadTitle(hWnd);
        var looksLikeDialog = string.Equals(className, "#32770", StringComparison.Ordinal)
            || title.Contains("Convertir", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Convert", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Seguridad", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Security", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Advertencia", StringComparison.OrdinalIgnoreCase);

        if (!looksLikeDialog)
        {
            return true;
        }

        var label = string.IsNullOrWhiteSpace(title)
            ? $"(sin título, class={className})"
            : title;
        if (!_signaled)
        {
            _signaled = true;
            _onDetected(label);
        }

        Native.PostMessage(hWnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    private static string ReadTitle(IntPtr hWnd)
    {
        var buffer = new StringBuilder(512);
        Native.GetWindowText(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string ReadClass(IntPtr hWnd)
    {
        var buffer = new StringBuilder(256);
        Native.GetClassName(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _cts.Cancel();
        _thread.Join(1000);
        _cts.Dispose();
        _disposed = true;
    }

    private static class Native
    {
        public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
