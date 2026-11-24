using System.Diagnostics;
using System.Runtime.InteropServices;

namespace QUANGLNBrowser.Network;

/// <summary>
/// Quản lý Win32 Job Object với cờ JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE để chống zombie process.
/// </summary>
public sealed class JobObjectManager : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        int infoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private IntPtr _jobHandle;
    private bool _disposed;

    public JobObjectManager()
    {
        _jobHandle = CreateJobObject(IntPtr.Zero, null);
        if (_jobHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Không thể tạo Win32 Job Object (Error: {Marshal.GetLastWin32Error()}).");
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };

        int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        if (!SetInformationJobObject(_jobHandle, JobObjectExtendedLimitInformation, ref info, (uint)length))
        {
            CloseHandle(_jobHandle);
            _jobHandle = IntPtr.Zero;
            throw new InvalidOperationException($"Không thể cấu hình Job Object (Error: {Marshal.GetLastWin32Error()}).");
        }
    }

    public void AssignProcess(Process process)
    {
        if (_disposed || _jobHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                AssignProcessToJobObject(_jobHandle, process.Handle);
            }
        }
        catch
        {
            // Tiến trình có thể đã thoát hoặc handle bị đóng trong quá trình hủy
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_jobHandle != IntPtr.Zero)
        {
            CloseHandle(_jobHandle);
            _jobHandle = IntPtr.Zero;
        }
    }
}
