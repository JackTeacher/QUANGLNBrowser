using System.Runtime.InteropServices;
using System.Text;

namespace QUANGLNBrowser.Media;

/// <summary>
/// Lớp P/Invoke kết nối libmpv-2.dll (x64) tại AppContext.BaseDirectory.
/// </summary>
public static class MpvInterop
{
    public const string DllName = "libmpv-2.dll";
    public const int MpvFormatInt64 = 4;
    public const int MpvFormatDouble = 5;

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_create();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_initialize(IntPtr mpvHandle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_terminate_destroy(IntPtr mpvHandle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command(IntPtr mpvHandle, IntPtr args);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_option(IntPtr mpvHandle, byte[] name, int format, ref long data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_option_string(IntPtr mpvHandle, byte[] name, byte[] data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property_string(IntPtr mpvHandle, byte[] name, byte[] data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property(IntPtr mpvHandle, byte[] name, int format, ref double data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(IntPtr mpvHandle, byte[] name, int format, out double data);

    public static byte[] ToUtf8NullTerminated(string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        byte[] buffer = new byte[byteCount + 1];
        Encoding.UTF8.GetBytes(value, 0, value.Length, buffer, 0);
        buffer[byteCount] = 0;
        return buffer;
    }

    public static int SetOptionString(IntPtr handle, string name, string value)
    {
        if (handle == IntPtr.Zero) return -1;
        return mpv_set_option_string(handle, ToUtf8NullTerminated(name), ToUtf8NullTerminated(value));
    }

    public static int SetPropertyString(IntPtr handle, string name, string value)
    {
        if (handle == IntPtr.Zero) return -1;
        return mpv_set_property_string(handle, ToUtf8NullTerminated(name), ToUtf8NullTerminated(value));
    }

    public static int SetPropertyDouble(IntPtr handle, string name, double value)
    {
        if (handle == IntPtr.Zero) return -1;
        double val = value;
        return mpv_set_property(handle, ToUtf8NullTerminated(name), MpvFormatDouble, ref val);
    }

    public static double GetPropertyDouble(IntPtr handle, string name, double fallback = 0.0)
    {
        if (handle == IntPtr.Zero) return fallback;
        int rc = mpv_get_property(handle, ToUtf8NullTerminated(name), MpvFormatDouble, out double val);
        return rc >= 0 ? val : fallback;
    }

    public static int ExecuteCommand(IntPtr handle, params string[] args)
    {
        if (handle == IntPtr.Zero || args.Length == 0)
        {
            return -1;
        }

        IntPtr[] pointers = new IntPtr[args.Length + 1];
        IntPtr rootArrayPtr = Marshal.AllocHGlobal(IntPtr.Size * pointers.Length);
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                byte[] utf8 = ToUtf8NullTerminated(args[i]);
                IntPtr strPtr = Marshal.AllocHGlobal(utf8.Length);
                Marshal.Copy(utf8, 0, strPtr, utf8.Length);
                pointers[i] = strPtr;
            }
            pointers[args.Length] = IntPtr.Zero;
            Marshal.Copy(pointers, 0, rootArrayPtr, pointers.Length);

            return mpv_command(handle, rootArrayPtr);
        }
        finally
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (pointers[i] != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(pointers[i]);
                }
            }
            Marshal.FreeHGlobal(rootArrayPtr);
        }
    }
}
