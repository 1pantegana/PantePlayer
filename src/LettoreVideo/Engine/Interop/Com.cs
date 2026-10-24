using System.Runtime.InteropServices;

namespace LettoreVideo.Engine.Interop;

/// <summary>
/// Base for our hand-written COM wrappers: owns one reference to an interface
/// pointer and calls its methods through the vtable with function pointers.
/// Slot numbers come from the C vtables of the Windows SDK headers.
/// </summary>
internal unsafe class ComObject : IDisposable
{
    public ComObject(IntPtr pointer) => Ptr = pointer;

    public IntPtr Ptr { get; private set; }

    /// <summary>Method slot <paramref name="index"/> of the vtable.</summary>
    protected void* Slot(int index) => (*(void***)Ptr)[index];

    /// <summary>Queries another interface of the same object (new reference).</summary>
    public IntPtr QueryInterface(in Guid iid)
    {
        Guid g = iid;
        Com.Check(Marshal.QueryInterface(Ptr, in g, out IntPtr result), "QueryInterface");
        return result;
    }

    public bool TryQueryInterface(in Guid iid, out IntPtr result)
    {
        Guid g = iid;
        return Marshal.QueryInterface(Ptr, in g, out result) >= 0;
    }

    public void Dispose()
    {
        IntPtr p = Ptr;
        Ptr = IntPtr.Zero;
        if (p != IntPtr.Zero) Marshal.Release(p);
        GC.SuppressFinalize(this);
    }
}

internal static class Com
{
    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new ComException(hr, what);
    }

    /// <summary>Wraps a pointer we do not own, taking a reference of our own.</summary>
    public static IntPtr AddRefed(IntPtr p)
    {
        if (p != IntPtr.Zero) Marshal.AddRef(p);
        return p;
    }
}

internal sealed class ComException(int hr, string what)
    : Exception($"{what} non riuscito (HRESULT 0x{hr:X8})")
{
    public int HResult32 { get; } = hr;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Rect(int left, int top, int right, int bottom)
{
    public int Left = left, Top = top, Right = right, Bottom = bottom;
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}
