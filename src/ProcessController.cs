using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinUnlink
{
    public static class ProcessController
    {
        internal static void Describe(LockingProcess process)
        {
            process.CanTerminate = false;
            if (String.IsNullOrEmpty(process.Name))
                process.Name = "PID " + process.ProcessId;
            string restriction = SnapshotRestriction(process);
            try
            {
                using (ProcessNative.OwnedProcessHandle handle = ProcessNative.OpenProcess(
                    ProcessNative.QueryLimitedInformation, false, unchecked((uint)process.ProcessId)))
                {
                    if (handle.IsInvalid)
                    {
                        process.InspectionError = ProcessNative.Error("无法查看进程", Marshal.GetLastWin32Error());
                        process.TerminationRestriction = restriction ?? process.InspectionError;
                        return;
                    }

                    string identityError = VerifyIdentity(handle, process);
                    if (identityError != null)
                    {
                        process.InspectionError = identityError;
                        process.TerminationRestriction = identityError;
                        return;
                    }

                    string imageError;
                    string image = ProcessNative.ReadImagePath(handle, out imageError);
                    if (image != null)
                    {
                        process.ImagePath = image;
                        process.Name = Path.GetFileName(image);
                    }
                    else
                    {
                        process.InspectionError = imageError;
                    }

                    string liveRestriction = CheckLiveRestriction(handle, process.ProcessId);
                    process.TerminationRestriction = restriction ?? liveRestriction ?? imageError;
                    process.CanTerminate = process.TerminationRestriction == null;
                }
            }
            catch (EntryPointNotFoundException)
            {
                process.InspectionError = "当前系统不支持查询进程关键性；禁止终止。";
                process.TerminationRestriction = process.InspectionError;
            }
        }

        // The caller must confirm the displayed selection before invoking this method.
        // No global shutdown, privilege changes, or foreign handle closing is performed.
        public static string Terminate(LockingProcess selected)
        {
            if (selected == null)
                return "未选中需要结束的占用进程。";
            string restriction = SnapshotRestriction(selected);
            if (restriction != null)
                return restriction;

            try
            {
                using (ProcessNative.OwnedProcessHandle handle = ProcessNative.OpenProcess(
                    ProcessNative.QueryLimitedInformation | ProcessNative.Terminate | ProcessNative.Synchronize,
                    false, unchecked((uint)selected.ProcessId)))
                {
                    if (handle.IsInvalid)
                        return ProcessNative.Error("无法打开选中进程；进程可能已退出或权限不足",
                            Marshal.GetLastWin32Error());

                    string identityError = VerifyIdentity(handle, selected);
                    if (identityError != null)
                        return identityError;
                    string liveRestriction = CheckLiveRestriction(handle, selected.ProcessId);
                    if (liveRestriction != null)
                        return liveRestriction;

                    if (!ProcessNative.TerminateProcess(handle, 1))
                        return ProcessNative.Error("结束选中进程失败", Marshal.GetLastWin32Error());

                    uint wait = ProcessNative.WaitForSingleObject(handle, 3000);
                    if (wait == 0)
                        return null;
                    if (wait == 258)
                        return "已请求结束 PID " + selected.ProcessId +
                            "，但 3 秒内未观察到退出；请刷新占用列表后重试文件操作。";
                    return ProcessNative.Error("已请求结束进程，但等待退出失败", Marshal.GetLastWin32Error());
                }
            }
            catch (EntryPointNotFoundException)
            {
                return "当前系统不支持查询进程关键性；禁止终止。";
            }
        }

        private static string SnapshotRestriction(LockingProcess process)
        {
            if (process.ProcessId <= 4)
                return "禁止结束 Windows 系统进程。";
            if (unchecked((uint)process.ProcessId) == ProcessNative.GetCurrentProcessId())
                return "禁止结束 WinUnlink 自身进程。";
            if (process.StartTimeFileTime <= 0)
                return "进程创建时间未知，无法核对身份；请刷新占用列表。";
            if (!String.IsNullOrEmpty(process.ServiceName) || process.Kind == LockProcessKind.Service)
                return "Windows 服务需通过服务管理器停止，不能直接结束其宿主进程。";
            if (process.Kind == LockProcessKind.Critical)
                return "禁止结束系统关键进程。";
            return null;
        }

        private static string VerifyIdentity(ProcessNative.OwnedProcessHandle handle, LockingProcess process)
        {
            if (process.StartTimeFileTime <= 0)
                return "进程创建时间未知，无法核对身份；请刷新占用列表。";
            ProcessNative.FileTime created;
            ProcessNative.FileTime exited;
            ProcessNative.FileTime kernel;
            ProcessNative.FileTime user;
            if (!ProcessNative.GetProcessTimes(handle, out created, out exited, out kernel, out user))
                return ProcessNative.Error("无法核对进程创建时间", Marshal.GetLastWin32Error());
            if (created.ToInt64() != process.StartTimeFileTime)
                return "PID " + process.ProcessId + " 的进程身份已变化；请刷新占用列表，禁止操作旧记录。";
            return null;
        }

        private static string CheckLiveRestriction(ProcessNative.OwnedProcessHandle handle, int processId)
        {
            bool critical;
            if (!ProcessNative.IsProcessCritical(handle, out critical))
                return ProcessNative.Error("无法确认进程关键性；禁止终止", Marshal.GetLastWin32Error());
            if (critical)
                return "禁止结束系统关键进程。";

            uint selectedSession;
            uint currentSession;
            if (!ProcessNative.ProcessIdToSessionId(unchecked((uint)processId), out selectedSession) ||
                !ProcessNative.ProcessIdToSessionId(ProcessNative.GetCurrentProcessId(), out currentSession))
                return ProcessNative.Error("无法核对进程用户会话；禁止终止", Marshal.GetLastWin32Error());
            if (selectedSession == 0)
                return "会话 0 的服务或后台系统进程不能在此直接结束。";
            if (selectedSession != currentSession)
                return "不能直接结束其他用户会话中的进程。";
            return null;
        }
    }

    internal static class ProcessNative
    {
        internal const uint QueryLimitedInformation = 0x1000;
        internal const uint Terminate = 0x0001;
        internal const uint Synchronize = 0x00100000;

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileTime
        {
            public uint Low;
            public uint High;

            public long ToInt64()
            {
                return ((long)High << 32) | Low;
            }
        }

        internal sealed class OwnedProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public OwnedProcessHandle() : base(true) { }

            protected override bool ReleaseHandle()
            {
                return CloseHandle(handle);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern OwnedProcessHandle OpenProcess(uint access,
            [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(OwnedProcessHandle handle,
            out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsProcessCritical(OwnedProcessHandle handle,
            [MarshalAs(UnmanagedType.Bool)] out bool critical);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(OwnedProcessHandle process,
            uint flags, StringBuilder imagePath, ref uint characters);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateProcess(OwnedProcessHandle process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(OwnedProcessHandle handle, uint milliseconds);

        internal static string ReadImagePath(OwnedProcessHandle handle, out string error)
        {
            for (int capacity = 512; capacity <= 32768; capacity *= 2)
            {
                StringBuilder path = new StringBuilder(capacity);
                uint size = (uint)capacity;
                if (QueryFullProcessImageName(handle, 0, path, ref size))
                {
                    error = null;
                    return path.ToString();
                }
                int code = Marshal.GetLastWin32Error();
                if (code != 122 || capacity == 32768)
                {
                    error = Error("无法查看进程可执行文件路径", code);
                    return null;
                }
            }
            error = "进程可执行文件路径超出 Windows 支持的长度。";
            return null;
        }

        internal static string Error(string operation, int error)
        {
            return operation + "（Win32 " + error + "）：" + new Win32Exception(error).Message;
        }
    }
}
