using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WinUnlink
{
    public enum LockProcessKind
    {
        Unknown = 0,
        MainWindow = 1,
        OtherWindow = 2,
        Service = 3,
        Explorer = 4,
        Console = 5,
        Critical = 1000
    }

    public sealed class LockingProcess
    {
        public int ProcessId { get; set; }
        public long StartTimeFileTime { get; set; }
        public string Name { get; set; }
        public string ApplicationName { get; set; }
        public string ImagePath { get; set; }
        public string ServiceName { get; set; }
        public LockProcessKind Kind { get; set; }
        public string Type { get; set; }
        public uint SessionId { get; set; }
        public bool Restartable { get; set; }
        public bool CanTerminate { get; set; }
        public string TerminationRestriction { get; set; }
        public string InspectionError { get; set; }
    }

    public sealed class LockScanResult
    {
        public LockScanResult()
        {
            Processes = new List<LockingProcess>();
        }

        public List<LockingProcess> Processes { get; private set; }
        // This describes the Restart Manager query, not a complete handle inventory.
        public bool IsComplete { get; set; }
        public string Error { get; set; }
        public uint RebootReasons { get; set; }
    }

    public static class LockInspector
    {
        private const uint ErrorMoreData = 234;
        private const int SessionKeyLength = 33;
        private const int ListAttempts = 5;
        private static readonly object CleanupGate = new object();
        private static readonly List<uint> PendingCleanup = new List<uint>();

        [StructLayout(LayoutKind.Sequential)]
        private struct UniqueProcess
        {
            public uint ProcessId;
            public ProcessNative.FileTime StartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessInfo
        {
            public UniqueProcess Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string ApplicationName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string ServiceName;
            public int ApplicationType;
            public uint ApplicationStatus;
            public uint SessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool Restartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern uint RmStartSession(out uint session, uint flags,
            StringBuilder sessionKey);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern uint RmRegisterResources(uint session, uint fileCount,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] files,
            uint applicationCount, IntPtr applications, uint serviceCount, IntPtr services);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern uint RmGetList(uint session, out uint needed,
            ref uint count, [In, Out] ProcessInfo[] processes, ref uint rebootReasons);

        [DllImport("rstrtmgr.dll")]
        private static extern uint RmEndSession(uint session);

        public static LockScanResult Scan(string absoluteFilePath)
        {
            return ScanFiles(new string[] { absoluteFilePath });
        }

        internal static LockScanResult ScanFiles(IList<string> absoluteFilePaths)
        {
            LockScanResult result = new LockScanResult();
            uint session = 0;
            bool started = false;
            try
            {
                if (absoluteFilePaths == null)
                {
                    result.Error = "占用查询需要文件路径列表。";
                    return result;
                }
                foreach (string path in absoluteFilePaths)
                {
                    if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                    { result.Error = "占用查询需要绝对文件路径。"; return result; }
                }

                string earlierCleanup = RetryCleanup();
                if (earlierCleanup != null)
                {
                    result.Error = earlierCleanup;
                    return result;
                }

                if (absoluteFilePaths.Count == 0) { result.IsComplete = true; return result; }

                uint error = RmStartSession(out session, 0, new StringBuilder(SessionKeyLength));
                if (error != 0)
                {
                    result.Error = NativeError("无法启动 Restart Manager 占用查询", error);
                    return result;
                }
                started = true;

                string[] resources = new string[absoluteFilePaths.Count];
                absoluteFilePaths.CopyTo(resources, 0);
                error = RmRegisterResources(session, checked((uint)resources.Length), resources,
                    0, IntPtr.Zero, 0, IntPtr.Zero);
                if (error != 0)
                {
                    result.Error = NativeError("无法注册待查询文件", error);
                    return result;
                }

                uint count = 0;
                uint needed;
                uint rebootReasons = 0;
                ProcessInfo[] buffer = null;
                for (int attempt = 0; attempt < ListAttempts; attempt++)
                {
                    count = buffer == null ? 0 : (uint)buffer.Length;
                    error = RmGetList(session, out needed, ref count, buffer, ref rebootReasons);
                    result.RebootReasons = rebootReasons;
                    if (error == 0)
                    {
                        if ((buffer == null && count != 0) ||
                            (buffer != null && count > buffer.Length))
                        {
                            result.Error = "Restart Manager 返回的进程数量超出查询缓冲区。";
                            return result;
                        }
                        HashSet<string> processIdentities = new HashSet<string>();
                        for (int index = 0; index < count; index++)
                        {
                            ProcessInfo info = buffer[index];
                            string identity = info.Process.ProcessId + ":" + info.Process.StartTime.ToInt64();
                            if (!processIdentities.Add(identity)) continue;
                            LockingProcess process = new LockingProcess
                            {
                                ProcessId = checked((int)info.Process.ProcessId),
                                StartTimeFileTime = info.Process.StartTime.ToInt64(),
                                ApplicationName = info.ApplicationName ?? String.Empty,
                                Name = info.ApplicationName ?? String.Empty,
                                ImagePath = null,
                                ServiceName = info.ServiceName ?? String.Empty,
                                Kind = (LockProcessKind)info.ApplicationType,
                                Type = TypeName(info.ApplicationType),
                                SessionId = info.SessionId,
                                Restartable = info.Restartable
                            };
                            ProcessController.Describe(process);
                            result.Processes.Add(process);
                        }
                        result.IsComplete = true;
                        return result;
                    }
                    if (error != ErrorMoreData)
                    {
                        result.Error = NativeError("无法取得占用进程列表", error);
                        return result;
                    }
                    if (needed == 0)
                    {
                        result.Error = "Restart Manager 在进程列表变化时未返回所需容量；请刷新查询。";
                        return result;
                    }
                    buffer = new ProcessInfo[checked((int)needed)];
                }
                result.Error = "占用进程列表持续变化，查询未能完成；请刷新后重试。";
            }
            catch (DllNotFoundException)
            {
                result.Error = "当前系统缺少 Windows Restart Manager，无法查询占用进程。";
            }
            catch (EntryPointNotFoundException)
            {
                result.Error = "当前系统未提供所需的 Restart Manager 接口。";
            }
            catch (OutOfMemoryException)
            {
                result.Error = "没有足够内存保存 Restart Manager 返回的进程列表。";
            }
            catch (OverflowException)
            {
                result.Error = "Restart Manager 返回的数量或进程标识超出当前接口支持的范围。";
            }
            finally
            {
                if (started)
                {
                    uint cleanupError = RmEndSession(session);
                    if (cleanupError != 0)
                    {
                        lock (CleanupGate)
                        {
                            PendingCleanup.Add(session);
                        }
                        result.IsComplete = false;
                        string message = NativeError("占用查询会话尚未释放；下次查询将重试清理", cleanupError);
                        result.Error = result.Error == null ? message : result.Error + "\r\n" + message;
                    }
                }
            }
            return result;
        }

        // Retain ownership when RmEndSession fails, so a later query can retry it.
        public static string RetryCleanup()
        {
            lock (CleanupGate)
            {
                string failure = null;
                for (int index = PendingCleanup.Count - 1; index >= 0; index--)
                {
                    uint error = RmEndSession(PendingCleanup[index]);
                    if (error == 0)
                        PendingCleanup.RemoveAt(index);
                    else
                        failure = NativeError("之前的占用查询会话尚未释放", error);
                }
                return failure;
            }
        }

        private static string NativeError(string operation, uint error)
        {
            return operation + "（Win32 " + error + "）：" +
                new Win32Exception(unchecked((int)error)).Message;
        }

        private static string TypeName(int type)
        {
            switch (type)
            {
                case 0: return "未知类型";
                case 1: return "窗口程序";
                case 2: return "其他窗口程序";
                case 3: return "Windows 服务";
                case 4: return "Windows 资源管理器";
                case 5: return "控制台程序";
                case 1000: return "系统关键进程";
                default: return "未知类型（" + type + "）";
            }
        }
    }
}
