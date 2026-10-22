using LettoreVideo.Engine.Interop;

namespace LettoreVideo.Engine.Media;

/// <summary>
/// The Vulkan device, created by FFmpeg so Vulkan Video decoding and our own
/// rendering share one VkDevice. The surface and swapchain extensions are asked
/// for up front: FFmpeg only enables what it is told to.
/// </summary>
internal sealed unsafe class VulkanHwDevice : IDisposable
{
    private AVBufferRef* _ref;

    /// <summary>AVHWDeviceType of Vulkan, looked up by name instead of hard-coding the enum.</summary>
    public static int DeviceType { get; private set; } = -1;

    /// <summary>AV_PIX_FMT_VULKAN, looked up by name for the same reason.</summary>
    public static int PixelFormat { get; private set; } = Av.PixFmtNone;

    public AVBufferRef* Ref => _ref;

    /// <summary>AVVulkanDeviceContext* (instance, device, queues, get_proc_addr).</summary>
    public void* VulkanContext => ((AVHWDeviceContext*)_ref->data)->hwctx;

    public VulkanHwDevice()
    {
        DeviceType = AvUtil.av_hwdevice_find_type_by_name("vulkan");
        PixelFormat = AvUtil.av_get_pix_fmt("vulkan");
        if (DeviceType < 0 || PixelFormat < 0)
            throw new NotSupportedException("questa build di FFmpeg non supporta Vulkan");

        void* opts = null;
        try
        {
            AvUtil.av_dict_set(&opts, "instance_extensions", "VK_KHR_surface+VK_KHR_win32_surface", 0);
            AvUtil.av_dict_set(&opts, "device_extensions", "VK_KHR_swapchain", 0);

            AVBufferRef* device = null;
            FFmpegLoader.Check(AvUtil.av_hwdevice_ctx_create(&device, DeviceType, null, opts, 0),
                "creazione del dispositivo Vulkan");
            _ref = device;
        }
        finally
        {
            AvUtil.av_dict_free(&opts);
        }
    }

    public void Dispose()
    {
        if (_ref == null) return;
        AVBufferRef* r = _ref;
        AvUtil.av_buffer_unref(&r);
        _ref = null;
    }
}
