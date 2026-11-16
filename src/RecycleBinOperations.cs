using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace WinUnlink
{
    /// <summary>
    /// Requests a Shell recycle operation. It never retries through permanent
    /// deletion. All COM objects and callbacks belong to one joined STA worker.
    /// </summary>
    public static class RecycleBinOperations
    {
        private const int Ok = 0;
        private const int Failure = unchecked((int)0x80004005);
        private const int AccessDenied = unchecked((int)0x80070005);
        private const int Canceled = unchecked((int)0x800704c7);
        private const uint RecycleIfPossible = 0x80;
        private const uint FileSystemPath = 0x80058000;

        // Windows SDK ShObjIdl_core.h and IFileOperation::SetOperationFlags:
        // RECYCLEONDELETE directs recycling instead of permanent deletion.
        // EARLYFAILURE is paired with NOERRORUI so errors are not ignored.
        // WANTNUKEWARNING overrides silent confirmation for any Shell branch
        // that cannot recycle, so it cannot silently choose permanent deletion.
        // NO_CONNECTED_ELEMENTS keeps an HTML file's companion folder out of
        // a single-file operation. Copy hooks remain enabled.
        private const uint OperationFlags =
            0x00080000 | // FOFX_RECYCLEONDELETE
            0x00100000 | // FOFX_EARLYFAILURE
            0x20000000 | // FOFX_ADDUNDORECORD
            0x00000040 | // FOF_ALLOWUNDO
            0x00000010 | // FOF_NOCONFIRMATION
            0x00000400 | // FOF_NOERRORUI
            0x00000004 | // FOF_SILENT
            0x00004000 | // FOF_WANTNUKEWARNING
            0x00002000;  // FOF_NO_CONNECTED_ELEMENTS

        public static FileOperationResult Delete(TargetSnapshot target)
        {
            if (target == null || target.Root == null)
                return Failed("请先选择并查询目标文件或文件夹。", 0, null);

            FileOperationResult result = null;
            Thread worker = new Thread(delegate()
            {
                try { result = DeleteOnSta(target); }
                catch (Exception error)
                {
                    // An exception escaping COM cleanup can occur after the
                    // operation; it must not be reported as unchanged failure.
                    result = new FileOperationResult
                    {
                        Status = OperationStatus.Partial,
                        ErrorCode = NativeCode(error),
                        RetainedPath = target.Root.Path,
                        Message = "回收操作的线程或资源收尾发生错误，无法确认最终结果。请检查原位置及回收站。" + error.Message
                    };
                }
            });
            worker.Name = "WinUnlink-RecycleBin";
            worker.IsBackground = false;
            try
            {
                worker.SetApartmentState(ApartmentState.STA);
                worker.Start();
            }
            catch (Exception error)
            {
                return Failed("无法启动回收站操作线程：" + error.Message, NativeCode(error), target.Root.Path);
            }
            // The caller is the application's async worker. No COM reference
            // crosses this boundary, and mutation cannot outlive its owner.
            worker.Join();
            return result ?? Failed("回收站操作没有返回可确认的结果。", 0, target.Root.Path);
        }

        private static FileOperationResult DeleteOnSta(TargetSnapshot target)
        {
            object operationObject = null;
            IShellItem source = null;
            RecycleProgressSink sink = null;
            bool performed = false;
            try
            {
                TargetOperations.Verify(target);
                sink = new RecycleProgressSink(target);
                Type operationType = Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"), true);
                operationObject = Activator.CreateInstance(operationType);
                IFileOperation operation = (IFileOperation)operationObject;
                Check(operation.SetOperationFlags(OperationFlags), "设置回收站操作参数失败");

                Guid shellItemId = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
                Check(SHCreateItemFromParsingName(target.Root.Path, IntPtr.Zero, ref shellItemId, out source),
                    "Shell 无法解析所选文件或文件夹");
                if (source == null) throw new COMException("Shell 未返回所选目标对象。", Failure);
                Check(operation.DeleteItem(source, sink), "登记回收操作失败");

                performed = true;
                int performResult = operation.PerformOperations();
                bool aborted;
                // Required even when PerformOperations returns an error.
                int abortedResult = operation.GetAnyOperationsAborted(out aborted);
                return Assess(target, sink, performResult, abortedResult, aborted);
            }
            catch (Exception error)
            {
                if (!performed)
                    return Failed("回收未开始：" + error.Message, NativeCode(error), target.Root.Path);
                if (sink != null) sink.RecordFailure(HResult(error), error.Message);
                return Assess(target, sink, HResult(error), Failure, true);
            }
            finally
            {
                // Callback item pointers are borrowed only for the callback;
                // they create no RCWs and need no caller-side release.
                try
                {
                    if (operationObject != null && Marshal.IsComObject(operationObject))
                        Marshal.FinalReleaseComObject(operationObject);
                }
                finally
                {
                    if (source != null && Marshal.IsComObject(source)) Marshal.FinalReleaseComObject(source);
                    GC.KeepAlive(sink);
                }
            }
        }

        private static FileOperationResult Assess(TargetSnapshot target, RecycleProgressSink sink,
            int performResult, int abortedResult, bool aborted)
        {
            bool originalGone = false;
            bool originalMatches = false;
            string verificationDetail = null;
            try
            {
                ItemSnapshot current = FileOperations.InspectEntry(target.Root.Path);
                originalGone = current.Identity != target.Root.Identity || current.IsDirectory != target.IsDirectory;
                if (!originalGone)
                {
                    TargetOperations.Verify(target);
                    originalMatches = true;
                }
            }
            catch (Win32Exception error)
            {
                if (error.NativeErrorCode == 2 || error.NativeErrorCode == 3) originalGone = true;
                else verificationDetail = error.Message;
            }
            catch (Exception error)
            {
                verificationDetail = error.Message;
            }

            bool recycled = sink != null && sink.RootRecycled;
            bool allSucceeded = performResult == Ok && abortedResult == Ok && !aborted &&
                sink != null && sink.FailureHResult == Ok;
            if (allSucceeded && recycled && originalGone)
                return new FileOperationResult
                {
                    Status = OperationStatus.Completed,
                    ErrorCode = 0,
                    RetainedPath = sink.RecycledPath,
                    Message = "已移到 Windows 回收站，并核实原目标已离开原位置。可在回收站中还原。"
                };

            int errorCode = sink != null && sink.FailureHResult < 0 ? sink.FailureHResult :
                performResult < 0 ? performResult : abortedResult < 0 ? abortedResult : aborted ? Canceled : Failure;
            string detail = sink == null ? null : sink.FailureMessage;
            if (String.IsNullOrEmpty(detail))
                detail = aborted ? "系统停止了回收操作。" :
                    !recycled ? "系统没有返回可确认的目标回收站项目。" : "无法核实原目标已离开原位置。";
            if (!String.IsNullOrEmpty(verificationDetail)) detail += " 原位置检查：" + verificationDetail;

            bool possibleEffects = recycled || (sink != null && sink.UnrecycledSuccessfulDelete) || !originalMatches;
            return new FileOperationResult
            {
                Status = possibleEffects ? OperationStatus.Partial : OperationStatus.Failed,
                ErrorCode = CodeFromHResult(errorCode),
                RetainedPath = recycled && sink.RecycledPath != null ? sink.RecycledPath : target.Root.Path,
                Message = possibleEffects
                    ? "回收未完全核实，可能已处理部分项目。请检查原位置和回收站后重新查询。" + detail + HResultText(errorCode)
                    : "回收未完成，目标原身份与确认清单仍匹配。" + detail + HResultText(errorCode)
            };
        }

        private static FileOperationResult Failed(string message, int error, string path)
        {
            return new FileOperationResult { Status = OperationStatus.Failed, Message = message,
                ErrorCode = error, RetainedPath = path };
        }

        private static void Check(int result, string message)
        {
            if (result < 0) throw new COMException(message + HResultText(result), result);
        }

        private static int HResult(Exception error)
        {
            if (error is System.Reflection.TargetInvocationException && error.InnerException != null)
                return HResult(error.InnerException);
            Win32Exception native = error as Win32Exception;
            if (native != null) return native.NativeErrorCode <= 0 ? Failure :
                unchecked((int)(0x80070000u | ((uint)native.NativeErrorCode & 0xffff)));
            return error.HResult < 0 ? error.HResult : Failure;
        }

        private static int NativeCode(Exception error) { return CodeFromHResult(HResult(error)); }

        private static int CodeFromHResult(int result)
        {
            return ((uint)result & 0xffff0000u) == 0x80070000u ? result & 0xffff : result;
        }

        private static string HResultText(int result)
        {
            return result < 0 ? "（HRESULT 0x" + unchecked((uint)result).ToString("X8") + "）" : String.Empty;
        }

        private static string ItemPath(IntPtr item, uint kind, out int result)
        {
            if (item == IntPtr.Zero) { result = unchecked((int)0x80004003); return null; }
            IntPtr allocated = IntPtr.Zero;
            try
            {
                // IShellItem inherits IUnknown: BindToHandler=3, GetParent=4,
                // GetDisplayName=5. Read the borrowed interface without making
                // an RCW whose release could invalidate the owned source RCW.
                IntPtr table = Marshal.ReadIntPtr(item);
                IntPtr method = Marshal.ReadIntPtr(table, 5 * IntPtr.Size);
                GetDisplayNameDelegate getName = (GetDisplayNameDelegate)Marshal.GetDelegateForFunctionPointer(
                    method, typeof(GetDisplayNameDelegate));
                result = getName(item, kind, out allocated);
                return result < 0 || allocated == IntPtr.Zero ? null : Marshal.PtrToStringUni(allocated);
            }
            finally
            {
                if (allocated != IntPtr.Zero) Marshal.FreeCoTaskMem(allocated);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDisplayNameDelegate(IntPtr item, uint kind, out IntPtr name);

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        public sealed class RecycleProgressSink : IFileOperationProgressSink
        {
            private readonly TargetSnapshot target;
            private readonly Dictionary<string, ItemSnapshot> allowed = new Dictionary<string, ItemSnapshot>(StringComparer.Ordinal);
            private readonly Dictionary<string, ItemSnapshot> begun = new Dictionary<string, ItemSnapshot>(StringComparer.Ordinal);
            internal int FailureHResult { get; private set; }
            internal string FailureMessage { get; private set; }
            internal bool RootRecycled { get; private set; }
            internal bool UnrecycledSuccessfulDelete { get; private set; }
            internal string RecycledPath { get; private set; }

            internal RecycleProgressSink(TargetSnapshot selected)
            {
                target = selected;
                AddAllowed(selected.Root);
                if (selected.IsDirectory)
                    foreach (ItemSnapshot entry in selected.Directory.Entries) AddAllowed(entry);
            }

            private void AddAllowed(ItemSnapshot item)
            {
                AddAddress(item.Path, item);
                if (!String.IsNullOrEmpty(item.CanonicalPath)) AddAddress(item.CanonicalPath, item);
            }

            private void AddAddress(string address, ItemSnapshot item)
            {
                ItemSnapshot old;
                if (allowed.TryGetValue(address, out old) && (old.Identity != item.Identity || old.IsDirectory != item.IsDirectory))
                    throw new IOException("目标清单存在地址与身份冲突，请重新查询。");
                allowed[address] = item;
            }

            internal void RecordFailure(int result, string message)
            {
                if (FailureHResult != Ok) return;
                FailureHResult = result < 0 ? result : Failure;
                FailureMessage = message;
            }

            public int PreDeleteItem(uint flags, IntPtr item)
            {
                try
                {
                    if ((flags & RecycleIfPossible) == 0)
                    {
                        RecordFailure(AccessDenied, "Shell 本次转移未标记回收，已取消该项。请另行选择删除方式。");
                        return AccessDenied;
                    }
                    int nameResult;
                    string path = ItemPath(item, FileSystemPath, out nameResult);
                    if (path == null) throw new COMException("无法确认 Shell 目标路径。", nameResult < 0 ? nameResult : Failure);
                    ItemSnapshot current = FileOperations.InspectEntry(path);
                    ItemSnapshot expected;
                    bool found = allowed.TryGetValue(current.Path, out expected) ||
                        (current.CanonicalPath != null && allowed.TryGetValue(current.CanonicalPath, out expected));
                    if (!found || current.Identity != expected.Identity || current.IsDirectory != expected.IsDirectory)
                    {
                        RecordFailure(AccessDenied, "Shell 目标不属于已确认的文件或文件夹清单，已取消该项。");
                        return AccessDenied;
                    }
                    if (Object.ReferenceEquals(expected, target.Root)) TargetOperations.Verify(target);
                    begun[current.Path] = expected;
                    return Ok;
                }
                catch (Exception error)
                {
                    int result = HResult(error);
                    RecordFailure(result, "回收前目标检查失败：" + error.Message);
                    return result;
                }
            }

            public int PostDeleteItem(uint flags, IntPtr item, int deleteResult, IntPtr newlyCreated)
            {
                try
                {
                    if (deleteResult < 0)
                    {
                        RecordFailure(deleteResult, "Shell 的实际回收操作失败。" + HResultText(deleteResult));
                        return deleteResult;
                    }
                    if (newlyCreated == IntPtr.Zero)
                    {
                        // Microsoft documents NULL here as fully deleted,
                        // unlike the non-NULL item representing the Recycle Bin.
                        UnrecycledSuccessfulDelete = true;
                        RecordFailure(Failure, "Shell 报告删除成功但没有返回回收站项目，无法确认可还原。请检查原位置与回收站。");
                        return Failure;
                    }
                    int nameResult;
                    string sourcePath = ItemPath(item, FileSystemPath, out nameResult);
                    if (sourcePath == null) throw new COMException("无法关联回收后的原目标。", nameResult < 0 ? nameResult : Failure);
                    string normalized = FileOperations.NormalizePath(sourcePath);
                    ItemSnapshot expected;
                    if (!begun.TryGetValue(normalized, out expected))
                        throw new COMException("未能把回收结果关联到已检查的目标。", Failure);
                    if (Object.ReferenceEquals(expected, target.Root))
                    {
                        RootRecycled = true;
                        string location = ItemPath(newlyCreated, FileSystemPath, out nameResult);
                        // RetainedPath is a filesystem path, not a Shell parsing
                        // name. The non-NULL new item remains recycle proof even
                        // when its filesystem locator is unavailable.
                        RecycledPath = String.IsNullOrEmpty(location) ? null : location;
                    }
                    return Ok;
                }
                catch (Exception error)
                {
                    RecordFailure(HResult(error), "回收结果核验失败：" + error.Message);
                    return HResult(error);
                }
            }

            public int StartOperations() { return Ok; }
            public int FinishOperations(int result) { if (result < 0) RecordFailure(result, "Shell 回收操作未完成。"); return Ok; }
            public int UpdateProgress(uint total, uint soFar) { return Ok; }
            public int ResetTimer() { return Ok; }
            public int PauseTimer() { return Ok; }
            public int ResumeTimer() { return Ok; }
            public int PreRenameItem(uint flags, IntPtr item, string name) { return AccessDenied; }
            public int PostRenameItem(uint flags, IntPtr item, string name, int result, IntPtr created) { return AccessDenied; }
            public int PreMoveItem(uint flags, IntPtr item, IntPtr folder, string name) { return AccessDenied; }
            public int PostMoveItem(uint flags, IntPtr item, IntPtr folder, string name, int result, IntPtr created) { return AccessDenied; }
            public int PreCopyItem(uint flags, IntPtr item, IntPtr folder, string name) { return AccessDenied; }
            public int PostCopyItem(uint flags, IntPtr item, IntPtr folder, string name, int result, IntPtr created) { return AccessDenied; }
            public int PreNewItem(uint flags, IntPtr folder, string name) { return AccessDenied; }
            public int PostNewItem(uint flags, IntPtr folder, string name, string template, uint attributes, int result, IntPtr created) { return AccessDenied; }
        }

        [ComVisible(true)]
        [Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IFileOperationProgressSink
        {
            [PreserveSig] int StartOperations();
            [PreserveSig] int FinishOperations(int result);
            [PreserveSig] int PreRenameItem(uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int PostRenameItem(uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr created);
            [PreserveSig] int PreMoveItem(uint flags, IntPtr item, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int PostMoveItem(uint flags, IntPtr item, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr created);
            [PreserveSig] int PreCopyItem(uint flags, IntPtr item, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int PostCopyItem(uint flags, IntPtr item, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr created);
            [PreserveSig] int PreDeleteItem(uint flags, IntPtr item);
            [PreserveSig] int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created);
            [PreserveSig] int PreNewItem(uint flags, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int PostNewItem(uint flags, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name,
                [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int result, IntPtr created);
            [PreserveSig] int UpdateProgress(uint total, uint soFar);
            [PreserveSig] int ResetTimer();
            [PreserveSig] int PauseTimer();
            [PreserveSig] int ResumeTimer();
        }

        [ComImport]
        [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr context, ref Guid handler, ref Guid interfaceId, out IntPtr result);
            [PreserveSig] int GetParent(out IShellItem parent);
            [PreserveSig] int GetDisplayName(uint kind, out IntPtr name);
            [PreserveSig] int GetAttributes(uint mask, out uint attributes);
            [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
        }

        // Full native order is required even though only SetOperationFlags,
        // DeleteItem, PerformOperations and GetAnyOperationsAborted are used.
        [ComImport]
        [Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOperation
        {
            [PreserveSig] int Advise([MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink sink, out uint cookie);
            [PreserveSig] int Unadvise(uint cookie);
            [PreserveSig] int SetOperationFlags(uint flags);
            [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
            [PreserveSig] int SetProgressDialog(IntPtr dialog);
            [PreserveSig] int SetProperties(IntPtr changes);
            [PreserveSig] int SetOwnerWindow(IntPtr window);
            [PreserveSig] int ApplyPropertiesToItem(IntPtr item);
            [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
            [PreserveSig] int RenameItem(IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
            [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int MoveItem(IntPtr item, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
            [PreserveSig] int MoveItems(IntPtr items, IntPtr folder);
            [PreserveSig] int CopyItem(IntPtr item, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
            [PreserveSig] int CopyItems(IntPtr items, IntPtr folder);
            [PreserveSig] int DeleteItem([MarshalAs(UnmanagedType.Interface)] IShellItem item,
                [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink sink);
            [PreserveSig] int DeleteItems(IntPtr items);
            [PreserveSig] int NewItem(IntPtr folder, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
                [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
            [PreserveSig] int PerformOperations();
            [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
        }

        [DllImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName", ExactSpelling = true,
            CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path,
            IntPtr context, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    }
}
