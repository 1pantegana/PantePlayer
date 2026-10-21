using System.Runtime.InteropServices;

namespace LettoreVideo.Engine.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct Texture2DDesc
{
    public uint Width, Height, MipLevels, ArraySize;
    public DxgiFormat Format;
    public uint SampleCount, SampleQuality;
    public uint Usage;
    public uint BindFlags, CpuAccessFlags, MiscFlags;

    public static Texture2DDesc Create(DxgiFormat format, int width, int height, uint bind,
        uint usage = D3D11.UsageDefault, uint cpuAccess = 0, uint misc = 0) => new()
    {
        Width = (uint)width,
        Height = (uint)height,
        MipLevels = 1,
        ArraySize = 1,
        Format = format,
        SampleCount = 1,
        Usage = usage,
        BindFlags = bind,
        CpuAccessFlags = cpuAccess,
        MiscFlags = misc,
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct Box(uint left, uint top, uint front, uint right, uint bottom, uint back)
{
    public uint Left = left, Top = top, Front = front, Right = right, Bottom = bottom, Back = back;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MappedSubresource
{
    public byte* Data;
    public uint RowPitch, DepthPitch;
}

internal static class D3D11
{
    public const uint SdkVersion = 7;
    public const uint CreateBgraSupport = 0x20;
    public const uint CreateVideoSupport = 0x800;
    public const uint FeatureLevel11_0 = 0xb000;
    public const uint FeatureLevel11_1 = 0xb100;

    public const uint UsageDefault = 0;
    public const uint UsageStaging = 3;
    public const uint BindShaderResource = 0x8;
    public const uint BindRenderTarget = 0x20;
    public const uint CpuAccessWrite = 0x10000;
    public const uint CpuAccessRead = 0x20000;
    public const uint MiscShared = 0x2;
    public const uint MiscSharedNtHandle = 0x800;
    public const uint MapRead = 1;
    public const uint MapWrite = 2;
    public const uint FenceFlagShared = 0x2;

    public static readonly Guid IidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid IidDevice5 = new("8ffde202-a0e7-45df-9e01-e837801b5ea0");
    public static readonly Guid IidDeviceContext4 = new("917600da-f58c-4c33-98d8-3e15b390fa24");
    public static readonly Guid IidVideoDevice = new("10EC4D5B-975A-4689-B9E4-D0AAC30FE333");
    public static readonly Guid IidVideoContext1 = new("A7F026DA-A5F8-4487-A564-15E34357651E");
    public static readonly Guid IidMultithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");
    public static readonly Guid IidFence = new("affde9d1-1df7-4bb7-8a34-0f46251dab80");

    [DllImport("d3d11.dll")]
    private static extern unsafe int D3D11CreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags,
        uint* featureLevels, uint numLevels, uint sdkVersion, out IntPtr device, out uint featureLevel, out IntPtr context);

    public static unsafe (D3D11Device Device, D3D11DeviceContext Context) CreateDevice(DxgiAdapter1 adapter, uint flags)
    {
        uint* levels = stackalloc uint[] { FeatureLevel11_1, FeatureLevel11_0 };
        // D3D_DRIVER_TYPE_UNKNOWN (0) is mandatory when an adapter is given.
        Com.Check(D3D11CreateDevice(adapter.Ptr, 0, IntPtr.Zero, flags, levels, 2, SdkVersion,
            out IntPtr device, out _, out IntPtr context), "D3D11CreateDevice");
        return (new D3D11Device(device), new D3D11DeviceContext(context));
    }
}

internal sealed unsafe class D3D11Device(IntPtr p) : ComObject(p)
{
    public D3D11Texture2D CreateTexture2D(in Texture2DDesc desc)
    {
        IntPtr tex;
        fixed (Texture2DDesc* d = &desc)
            Com.Check(((delegate* unmanaged<IntPtr, Texture2DDesc*, void*, IntPtr*, int>)Slot(5))(Ptr, d, null, &tex),
                "CreateTexture2D");
        return new D3D11Texture2D(tex);
    }

    /// <summary>Render target view with the resource's own format (null desc).</summary>
    public ComObject CreateRenderTargetView(ComObject resource)
    {
        IntPtr view;
        Com.Check(((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Slot(9))(Ptr, resource.Ptr, null, &view),
            "CreateRenderTargetView");
        return new ComObject(view);
    }

    public int GetDeviceRemovedReason() => ((delegate* unmanaged<IntPtr, int>)Slot(39))(Ptr);
}

internal sealed unsafe class D3D11Device5(IntPtr p) : ComObject(p)
{
    public D3D11Fence CreateFence(ulong initialValue, uint flags)
    {
        IntPtr fence;
        Guid iid = D3D11.IidFence;
        Com.Check(((delegate* unmanaged<IntPtr, ulong, uint, Guid*, IntPtr*, int>)Slot(68))(
            Ptr, initialValue, flags, &iid, &fence), "CreateFence");
        return new D3D11Fence(fence);
    }
}

internal sealed unsafe class D3D11DeviceContext(IntPtr p) : ComObject(p)
{
    public MappedSubresource Map(ComObject resource, uint subresource, uint mapType)
    {
        MappedSubresource mapped;
        Com.Check(((delegate* unmanaged<IntPtr, IntPtr, uint, uint, uint, MappedSubresource*, int>)Slot(14))(
            Ptr, resource.Ptr, subresource, mapType, 0, &mapped), "Map");
        return mapped;
    }

    public void Unmap(ComObject resource, uint subresource) =>
        ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Slot(15))(Ptr, resource.Ptr, subresource);

    public void CopySubresourceRegion(IntPtr dst, uint dstSub, uint x, uint y, IntPtr src, uint srcSub, Box* box) =>
        ((delegate* unmanaged<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, Box*, void>)Slot(46))(
            Ptr, dst, dstSub, x, y, 0, src, srcSub, box);

    public void CopyResource(ComObject dst, ComObject src) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)Slot(47))(Ptr, dst.Ptr, src.Ptr);

    public void ClearRenderTargetView(ComObject view, float r, float g, float b, float a)
    {
        float* color = stackalloc float[] { r, g, b, a };
        ((delegate* unmanaged<IntPtr, IntPtr, float*, void>)Slot(50))(Ptr, view.Ptr, color);
    }

    public void Flush() => ((delegate* unmanaged<IntPtr, void>)Slot(111))(Ptr);
}

internal sealed unsafe class D3D11DeviceContext4(IntPtr p) : ComObject(p)
{
    public void Signal(D3D11Fence fence, ulong value) =>
        Com.Check(((delegate* unmanaged<IntPtr, IntPtr, ulong, int>)Slot(147))(Ptr, fence.Ptr, value), "Signal");

    public void Wait(D3D11Fence fence, ulong value) =>
        Com.Check(((delegate* unmanaged<IntPtr, IntPtr, ulong, int>)Slot(148))(Ptr, fence.Ptr, value), "Wait");
}

internal sealed unsafe class D3D11Multithread(IntPtr p) : ComObject(p)
{
    public void SetMultithreadProtected(bool enable) =>
        ((delegate* unmanaged<IntPtr, int, int>)Slot(5))(Ptr, enable ? 1 : 0);
}

internal sealed unsafe class D3D11Fence(IntPtr p) : ComObject(p)
{
    public ulong CompletedValue => ((delegate* unmanaged<IntPtr, ulong>)Slot(8))(Ptr);

    public void SetEventOnCompletion(ulong value, IntPtr eventHandle) =>
        Com.Check(((delegate* unmanaged<IntPtr, ulong, IntPtr, int>)Slot(9))(Ptr, value, eventHandle), "SetEventOnCompletion");
}

internal sealed unsafe class D3D11Texture2D(IntPtr p) : ComObject(p)
{
    public Texture2DDesc GetDesc()
    {
        Texture2DDesc desc;
        ((delegate* unmanaged<IntPtr, Texture2DDesc*, void>)Slot(10))(Ptr, &desc);
        return desc;
    }
}
