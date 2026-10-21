using System.Runtime.InteropServices;

namespace LettoreVideo.Mpv;

/// <summary>
/// Raw P/Invoke surface for libmpv's client API (see mpv/client.h).
/// Only the subset the player actually needs is declared here.
/// </summary>
internal static class MpvNative
{
    private const string Lib = "libmpv-2.dll";

    public const int MPV_FORMAT_NONE = 0;
    public const int MPV_FORMAT_STRING = 1;
    public const int MPV_FORMAT_FLAG = 3;
    public const int MPV_FORMAT_INT64 = 4;
    public const int MPV_FORMAT_DOUBLE = 5;

    public const int MPV_EVENT_NONE = 0;
    public const int MPV_EVENT_SHUTDOWN = 1;
    public const int MPV_EVENT_LOG_MESSAGE = 2;
    public const int MPV_EVENT_START_FILE = 6;
    public const int MPV_EVENT_END_FILE = 7;
    public const int MPV_EVENT_FILE_LOADED = 8;
    public const int MPV_EVENT_PROPERTY_CHANGE = 22;

    public const int MPV_END_FILE_REASON_EOF = 0;
    public const int MPV_END_FILE_REASON_STOP = 2;
    public const int MPV_END_FILE_REASON_QUIT = 3;
    public const int MPV_END_FILE_REASON_ERROR = 4;
    public const int MPV_END_FILE_REASON_REDIRECT = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEvent
    {
        public int EventId;
        public int Error;
        public ulong ReplyUserdata;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventProperty
    {
        public IntPtr Name;
        public int Format;
        private int _padding;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventEndFile
    {
        public int Reason;
        public int Error;
        public long PlaylistEntryId;
        public long PlaylistInsertId;
        public int PlaylistInsertNumEntries;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventLogMessage
    {
        public IntPtr Prefix;
        public IntPtr Level;
        public IntPtr Text;
        public int LogLevel;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_create();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_initialize(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_option_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, ref double data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, ref int data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out double data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out long data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_observe_property(IntPtr ctx, ulong replyUserdata,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format);

    /// <summary>
    /// Takes a NULL-terminated array of UTF-8 strings. Arguments are passed
    /// separately so paths never need shell-style escaping.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command(IntPtr ctx, IntPtr[] args);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_wakeup(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_request_log_messages(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string minLevel);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_error_string(int error);
}
