using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace LettoreVideo;

/// <summary>
/// Hosts a bare Win32 child window inside the WPF tree. The engine attaches a
/// flip-model DXGI swapchain to its HWND and presents into it from its own
/// thread, so video never goes through WPF's composition.
/// </summary>
public sealed class VideoSurfaceHost : HwndHost
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;

    private IntPtr _hwnd;

    /// <summary>Handle the swapchain is created on. Valid once the control is loaded.</summary>
    public IntPtr VideoHandle => _hwnd;

    /// <summary>Raised once the native surface exists and the engine can be attached to it.</summary>
    public event EventHandler? SurfaceCreated;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = CreateWindowEx(
            0,
            "static",              // A plain STATIC control is enough: the swapchain covers it.
            string.Empty,
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, 1, 1,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed (error {Marshal.GetLastWin32Error()}).");

        SurfaceCreated?.Invoke(this, EventArgs.Empty);
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
