using LettoreVideo.Engine.Interop;

namespace LettoreVideo.Engine.Gpu;

/// <summary>
/// The single D3D11 device shared by decoding, interpolation, the video processor
/// and the swapchain, so frames never leave the GPU or cross adapters.
/// Created explicitly on the NVIDIA adapter: the default adapter could be an
/// iGPU, where neither RTX VSR nor NvFRUC exist.
/// </summary>
internal sealed class GpuDevice : IDisposable
{
    public const uint NvidiaVendorId = 0x10DE;

    public DxgiFactory2 Factory { get; }
    public DxgiAdapter1 Adapter { get; }
    public string AdapterName { get; }
    public bool IsNvidia { get; }

    public D3D11Device Device { get; }
    public D3D11Device5 Device5 { get; }
    public D3D11DeviceContext Context { get; }
    public D3D11DeviceContext4 Context4 { get; }
    public D3D11VideoDevice VideoDevice { get; }
    public D3D11VideoContext VideoContext { get; }

    /// <summary>
    /// Shared fence for the NvFRUC protocol (the library waits on it and signals it
    /// from CUDA). Values only ever grow: take them from <see cref="NextFenceValue"/>.
    /// </summary>
    public D3D11Fence Fence { get; }
    private long _fenceValue;

    private GpuDevice(DxgiFactory2 factory, DxgiAdapter1 adapter, AdapterDesc1 desc,
        D3D11Device device, D3D11DeviceContext context)
    {
        Factory = factory;
        Adapter = adapter;
        AdapterName = desc.Description;
        IsNvidia = desc.VendorId == NvidiaVendorId;

        Device = device;
        Context = context;
        Device5 = new D3D11Device5(device.QueryInterface(D3D11.IidDevice5));
        Context4 = new D3D11DeviceContext4(context.QueryInterface(D3D11.IidDeviceContext4));
        VideoDevice = new D3D11VideoDevice(device.QueryInterface(D3D11.IidVideoDevice));
        VideoContext = new D3D11VideoContext(context.QueryInterface(D3D11.IidVideoContext1));

        // Decoder, interpolator and presenter all use the immediate context from
        // their own threads: let D3D11 serialize every call.
        using (var mt = new D3D11Multithread(context.QueryInterface(D3D11.IidMultithread)))
            mt.SetMultithreadProtected(true);

        Fence = Device5.CreateFence(0, D3D11.FenceFlagShared);
    }

    public static GpuDevice Create()
    {
        DxgiFactory2 factory = Dxgi.CreateFactory();

        DxgiAdapter1? chosen = null;
        AdapterDesc1 chosenDesc = default;
        for (uint i = 0; factory.EnumAdapters1(i) is { } adapter; i++)
        {
            AdapterDesc1 d = adapter.GetDesc1();
            bool software = (d.Flags & Dxgi.AdapterFlagSoftware) != 0;
            if (chosen is null && !software && d.VendorId == NvidiaVendorId)
            {
                chosen = adapter;
                chosenDesc = d;
                continue;
            }
            adapter.Dispose();
        }

        // No NVIDIA GPU: still play, on the default adapter.
        if (chosen is null)
        {
            chosen = factory.EnumAdapters1(0) ?? throw new InvalidOperationException("nessuna scheda video");
            chosenDesc = chosen.GetDesc1();
        }

        var (device, context) = D3D11.CreateDevice(chosen, D3D11.CreateBgraSupport | D3D11.CreateVideoSupport);
        return new GpuDevice(factory, chosen, chosenDesc, device, context);
    }

    /// <summary>Returns a fresh, strictly increasing fence value.</summary>
    public ulong NextFenceValue() => (ulong)Interlocked.Increment(ref _fenceValue);

    public void Dispose()
    {
        Fence.Dispose();
        VideoContext.Dispose();
        VideoDevice.Dispose();
        Context4.Dispose();
        Device5.Dispose();
        Context.Dispose();
        Device.Dispose();
        Adapter.Dispose();
        Factory.Dispose();
    }
}
