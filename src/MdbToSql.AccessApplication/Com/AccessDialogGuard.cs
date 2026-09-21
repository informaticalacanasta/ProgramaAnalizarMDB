using System.Runtime.InteropServices;
using System.Text;
using MdbToSql.Core.Exceptions;

namespace MdbToSql.AccessApplication.Com;

internal sealed class AccessDialogGuard : IDisposable
{
    private const int WmClose = 0x0010;
    private readonly int _accessPid;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly List<string> _titles = [];
    private bool _disposed;

    public AccessDialogGuard(int accessPid)
    {
        _accessPid = accessPid;
        _thread = new Thread(Watch)
        {
            IsBackground = true,
            Name = "AccessDialogGuard"
        };
        _thread.Start();
    }

    public bool Detected
    {
        get
        {
            lock (_titles)
            {
                return _titles.Count > 0;
            }
        }
    }

    public IReadOnlyList<string> Titles
    {
        get
        {
            lock (_titles)
            {
                return [.. _titles];
            }
        }
    }

    public IReadOnlyList<string> ConsumeDetected()
    {
        lock (_titles)
        {
            if (_titles.Count == 0)
            {
                return [];
            }

            var copy = _titles.ToList();
            _titles.Clear();
            return copy;
        }
    }

    public void ThrowIfDetected()
    {
        var titles = ConsumeDetected();
        if (titles.Count == 0)
        {
            return;
        }

        throw new UnsafeAccessStartupException(
            "Diálogo modal durante el análisis. No se pulsa ningún botón. " +
            string.Join("; ", titles));
    }

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
                // Best-effort.
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
            || title.Contains("Seguridad", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Security", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Advertencia", StringComparison.OrdinalIgnoreCase);

        if (!looksLikeDialog)
        {
            return true;
        }

        var label = string.IsNullOrWhiteSpace(title) ? $"(class={className})" : title;
        lock (_titles)
        {
            if (!_titles.Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                _titles.Add(label);
            }
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
