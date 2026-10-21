using System.Runtime.InteropServices;

namespace LettoreVideo.Engine.Interop;

internal enum DxgiFormat : uint
{
    Unknown = 0,
    R8G8B8A8Unorm = 28,
    B8G8R8A8Unorm = 87,
    NV12 = 103,
    P010 = 104,
}

internal enum ColorSpace : uint
{
    RgbFullG22NoneP709 = 0,
    YcbcrStudioG22LeftP601 = 6,
    YcbcrFullG22LeftP601 = 7,
    YcbcrStudioG22LeftP709 = 8,
    YcbcrFullG22LeftP709 = 9,
    YcbcrStudioG22LeftP2020 = 10,
    YcbcrFullG22LeftP2020 = 11,
    YcbcrStudioG2084LeftP2020 = 13,
    YcbcrStudioGhlgTopLeftP2020 = 18,
}

[StructLayout(LayoutKind.Sequential)]
internal struct DxgiRational(uint numerator, uint denominator)
{
    public uint Numerator = numerator, Denominator = denominator;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct AdapterDesc1
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string Description;
    public uint VendorId, DeviceId, SubSysId, Revision;
    public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
    public long AdapterLuid;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SwapChainDesc1
{
    public uint Width, Height;
    public DxgiFormat Format;
    public int Stereo;
    public uint SampleCount, SampleQuality;
    public uint BufferUsage;
    public uint BufferCount;
    public uint Scaling;
    public uint SwapEffect;
    public uint AlphaMode;
    public uint Flags;
}

internal static class Dxgi
{
    public const uint AdapterFlagSoftware = 2;
    public const uint UsageRenderTargetOutput = 0x20;
    public const uint ScalingStretch = 0;
    public const uint SwapEffectFlipDiscard = 4;
    public const uint AlphaModeIgnore = 3;
    public const uint SwapChainFlagFrameLatencyWaitable = 64;
    public const uint MwaNoAltEnter = 2;
    public const int ErrorNotFound = unchecked((int)0x887A0002);

    public static readonly Guid IidFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
    public static readonly Guid IidAdapter1 = new("29038f61-3839-4626-91fd-086879011a05");
    public static readonly Guid IidSwapChain2 = new("a8be2ac4-199f-4946-b331-79599fb98de7");

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory2(uint flags, in Guid riid, out IntPtr factory);

    public static DxgiFactory2 CreateFactory()
    {
        Com.Check(CreateDXGIFactory2(0, IidFactory2, out IntPtr f), "CreateDXGIFactory2");
        return new DxgiFactory2(f);
    }
}

internal sealed unsafe class DxgiFactory2(IntPtr p) : ComObject(p)
{
    /// <summary>Null when <paramref name="index"/> is past the last adapter.</summary>
    public DxgiAdapter1? EnumAdapters1(uint index)
    {
        IntPtr a;
        int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Slot(12))(Ptr, index, &a);
        if (hr == Dxgi.ErrorNotFound) return null;
        Com.Check(hr, "EnumAdapters1");
        return new DxgiAdapter1(a);
    }

    public void MakeWindowAssociation(IntPtr hwnd, uint flags) =>
        ((delegate* unmanaged<IntPtr, IntPtr, uint, int>)Slot(8))(Ptr, hwnd, flags);

    public DxgiSwapChain2 CreateSwapChainForHwnd(ComObject device, IntPtr hwnd, in SwapChainDesc1 desc)
    {
        IntPtr sc;
        fixed (SwapChainDesc1* d = &desc)
            Com.Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr, SwapChainDesc1*, void*, IntPtr, IntPtr*, int>)Slot(15))(
                Ptr, device.Ptr, hwnd, d, null, IntPtr.Zero, &sc), "CreateSwapChainForHwnd");

        using var sc1 = new ComObject(sc);
        return new DxgiSwapChain2(sc1.QueryInterface(Dxgi.IidSwapChain2));
    }
}

internal sealed unsafe class DxgiAdapter1(IntPtr p) : ComObject(p)
{
    public AdapterDesc1 GetDesc1()
    {
        // 312 bytes on x64; marshalled because of the inline UTF-16 string.
        IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<AdapterDesc1>());
        try
        {
            Com.Check(((delegate* unmanaged<IntPtr, IntPtr, int>)Slot(10))(Ptr, buffer), "GetDesc1");
            return Marshal.PtrToStructure<AdapterDesc1>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

internal sealed unsafe class DxgiSwapChain2(IntPtr p) : ComObject(p)
{
    /// <summary>Returns the raw HRESULT: occlusion and device loss are not exceptions here.</summary>
    public int Present(uint syncInterval, uint flags) =>
        ((delegate* unmanaged<IntPtr, uint, uint, int>)Slot(8))(Ptr, syncInterval, flags);

    public D3D11Texture2D GetBuffer(uint index)
    {
        IntPtr tex;
        Guid iid = D3D11.IidTexture2D;
        Com.Check(((delegate* unmanaged<IntPtr, uint, Guid*, IntPtr*, int>)Slot(9))(Ptr, index, &iid, &tex), "GetBuffer");
        return new D3D11Texture2D(tex);
    }

    public void ResizeBuffers(uint count, uint width, uint height, DxgiFormat format, uint flags) =>
        Com.Check(((delegate* unmanaged<IntPtr, uint, uint, uint, DxgiFormat, uint, int>)Slot(13))(
            Ptr, count, width, height, format, flags), "ResizeBuffers");

    public void SetMaximumFrameLatency(uint latency) =>
        Com.Check(((delegate* unmanaged<IntPtr, uint, int>)Slot(31))(Ptr, latency), "SetMaximumFrameLatency");

    /// <summary>Waitable handle owned by the caller (CloseHandle when done).</summary>
    public IntPtr GetFrameLatencyWaitableObject() =>
        ((delegate* unmanaged<IntPtr, IntPtr>)Slot(33))(Ptr);
}
