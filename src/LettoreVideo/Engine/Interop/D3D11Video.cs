using System.Runtime.InteropServices;

namespace LettoreVideo.Engine.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct VideoProcessorContentDesc
{
    public uint InputFrameFormat;   // D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE = 0
    public DxgiRational InputFrameRate;
    public uint InputWidth, InputHeight;
    public DxgiRational OutputFrameRate;
    public uint OutputWidth, OutputHeight;
    public uint Usage;              // D3D11_VIDEO_USAGE
}

[StructLayout(LayoutKind.Sequential)]
internal struct VideoProcessorInputViewDesc
{
    public uint FourCC;
    public uint ViewDimension;      // D3D11_VPIV_DIMENSION_TEXTURE2D = 1
    public uint MipSlice, ArraySlice;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VideoProcessorOutputViewDesc
{
    public uint ViewDimension;      // D3D11_VPOV_DIMENSION_TEXTURE2D = 1
    public uint MipSlice, FirstArraySlice, ArraySize;   // union with the array variant
}

[StructLayout(LayoutKind.Sequential)]
internal struct VideoProcessorStream
{
    public int Enable;
    public uint OutputIndex, InputFrameOrField, PastFrames, FutureFrames;
    public IntPtr PastSurfaces;
    public IntPtr InputSurface;
    public IntPtr FutureSurfaces;
    public IntPtr PastSurfacesRight;
    public IntPtr InputSurfaceRight;
    public IntPtr FutureSurfacesRight;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VideoColor(float r, float g, float b, float a)
{
    public float R = r, G = g, B = b, A = a;
}

internal static class D3D11Video
{
    public const uint UsagePlaybackNormal = 0;
    public const uint UsageOptimalQuality = 2;
}

internal sealed unsafe class D3D11VideoDevice(IntPtr p) : ComObject(p)
{
    public ComObject CreateVideoProcessorEnumerator(in VideoProcessorContentDesc desc)
    {
        IntPtr e;
        fixed (VideoProcessorContentDesc* d = &desc)
            Com.Check(((delegate* unmanaged<IntPtr, VideoProcessorContentDesc*, IntPtr*, int>)Slot(10))(Ptr, d, &e),
                "CreateVideoProcessorEnumerator");
        return new ComObject(e);
    }

    public ComObject CreateVideoProcessor(ComObject enumerator)
    {
        IntPtr vp;
        Com.Check(((delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr*, int>)Slot(4))(Ptr, enumerator.Ptr, 0, &vp),
            "CreateVideoProcessor");
        return new ComObject(vp);
    }

    public ComObject CreateInputView(ComObject texture, ComObject enumerator)
    {
        var desc = new VideoProcessorInputViewDesc { ViewDimension = 1 };
        IntPtr view;
        Com.Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr, VideoProcessorInputViewDesc*, IntPtr*, int>)Slot(8))(
            Ptr, texture.Ptr, enumerator.Ptr, &desc, &view), "CreateVideoProcessorInputView");
        return new ComObject(view);
    }

    public ComObject CreateOutputView(ComObject texture, ComObject enumerator)
    {
        var desc = new VideoProcessorOutputViewDesc { ViewDimension = 1 };
        IntPtr view;
        Com.Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr, VideoProcessorOutputViewDesc*, IntPtr*, int>)Slot(9))(
            Ptr, texture.Ptr, enumerator.Ptr, &desc, &view), "CreateVideoProcessorOutputView");
        return new ComObject(view);
    }
}

/// <summary>ID3D11VideoContext1 (slots of ID3D11VideoContext included).</summary>
internal sealed unsafe class D3D11VideoContext(IntPtr p) : ComObject(p)
{
    public void SetOutputTargetRect(ComObject vp, Rect rect) =>
        ((delegate* unmanaged<IntPtr, IntPtr, int, Rect*, void>)Slot(13))(Ptr, vp.Ptr, 1, &rect);

    public void SetOutputBackgroundColor(ComObject vp, VideoColor color) =>
        ((delegate* unmanaged<IntPtr, IntPtr, int, VideoColor*, void>)Slot(14))(Ptr, vp.Ptr, 0, &color);

    public void SetStreamFrameFormat(ComObject vp, uint stream, uint format) =>
        ((delegate* unmanaged<IntPtr, IntPtr, uint, uint, void>)Slot(27))(Ptr, vp.Ptr, stream, format);

    public void SetStreamSourceRect(ComObject vp, uint stream, Rect rect) =>
        ((delegate* unmanaged<IntPtr, IntPtr, uint, int, Rect*, void>)Slot(30))(Ptr, vp.Ptr, stream, 1, &rect);

    public void SetStreamDestRect(ComObject vp, uint stream, Rect rect) =>
        ((delegate* unmanaged<IntPtr, IntPtr, uint, int, Rect*, void>)Slot(31))(Ptr, vp.Ptr, stream, 1, &rect);

    public void SetStreamAutoProcessingMode(ComObject vp, uint stream, bool enable) =>
        ((delegate* unmanaged<IntPtr, IntPtr, uint, int, void>)Slot(37))(Ptr, vp.Ptr, stream, enable ? 1 : 0);

    /// <summary>Driver-specific stream extension (NVIDIA RTX VSR lives here).</summary>
    public int SetStreamExtension(ComObject vp, uint stream, in Guid guid, uint size, void* data)
    {
        Guid g = guid;
        return ((delegate* unmanaged<IntPtr, IntPtr, uint, Guid*, uint, void*, int>)Slot(39))(
            Ptr, vp.Ptr, stream, &g, size, data);
    }

    public void Blt(ComObject vp, ComObject outputView, VideoProcessorStream* streams, uint count) =>
        Com.Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr, uint, uint, VideoProcessorStream*, int>)Slot(53))(
            Ptr, vp.Ptr, outputView.Ptr, 0, count, streams), "VideoProcessorBlt");

    public void SetOutputColorSpace1(ComObject vp, ColorSpace cs) =>
        ((delegate* unmanaged<IntPtr, IntPtr, ColorSpace, void>)Slot(70))(Ptr, vp.Ptr, cs);

    public void SetStreamColorSpace1(ComObject vp, uint stream, ColorSpace cs) =>
        ((delegate* unmanaged<IntPtr, IntPtr, uint, ColorSpace, void>)Slot(74))(Ptr, vp.Ptr, stream, cs);
}
