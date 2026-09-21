using System.Runtime.InteropServices;

namespace MdbToSql.AccessProbe;

internal sealed class ShiftHold : IDisposable
{
    private const uint InputKeyboard = 1;
    private const ushort VkShift = 0x10;
    private const ushort VkLShift = 0xA0;
    private const uint KeyeventfKeyup = 0x0002;
    private bool _down;
    private bool _disposed;

    public void Press()
    {
        Send(down: true);
        _down = true;
        Thread.Sleep(80);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_down)
        {
            Send(down: false);
            _down = false;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static void Send(bool down)
    {
        SendOne(VkShift, down);
        SendOne(VkLShift, down);
    }

    private static void SendOne(ushort vk, bool down)
    {
        var input = new Input
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KeybdInput
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = down ? 0 : KeyeventfKeyup,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        var sent = Native.SendInput(1, [input], Marshal.SizeOf<Input>());
        if (sent != 1)
        {
            throw new InvalidOperationException(
                $"SendInput SHIFT {(down ? "down" : "up")} falló. Win32={Marshal.GetLastWin32Error()}.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput mi;
        [FieldOffset(0)] public KeybdInput ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private static class Native
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);
    }
}
