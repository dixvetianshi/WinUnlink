using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinUnlink
{
    public sealed class FileSnapshot
    {
        public string Path { get; private set; }
        public string Identity { get; private set; }
        public long Length { get; private set; }
        public bool IsReadOnly { get; private set; }

        internal FileSnapshot(string path, string identity, long length, bool readOnly)
        {
            Path = path;
            Identity = identity;
            Length = length;
            IsReadOnly = readOnly;
        }
    }

    public enum OperationStatus { Completed, Scheduled, Failed, Partial }

    public sealed class FileOperationResult
    {
        public OperationStatus Status { get; set; }
        public string Message { get; set; }
        public int ErrorCode { get; set; }
        public string RetainedPath { get; set; }
    }

    /// <summary>
    /// Single-file operations. A replacement is copied to an owned, exclusive
    /// staging file in the destination directory and committed by rename.
    /// No operation truncates the existing destination or changes its ACL.
    /// </summary>
    public static class FileOperations
    {
        private const uint ReadAttributes = 0x00000080;
        private const uint DeleteAccess = 0x00010000;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint ShareRead = 1;
        private const uint ShareWrite = 2;
        private const uint ShareDelete = 4;
        private const uint ShareAll = ShareRead | ShareWrite | ShareDelete;
        private const uint CreateNew = 1;
        private const uint OpenExisting = 3;
        private const uint NormalAttribute = 0x00000080;
        private const uint DirectoryAttribute = 0x00000010;
        private const uint ReadOnlyAttribute = 0x00000001;
        private const uint ReparsePointAttribute = 0x00000400;
        private const uint OpenReparsePoint = 0x00200000;
        private const uint BackupSemantics = 0x02000000;
        private const uint InvalidAttributes = 0xffffffff;
        private const uint MoveReplaceExisting = 1;
        private const uint MoveDelayUntilReboot = 4;
        private const int ErrorAccessDenied = 5;
        private const int ErrorSharingViolation = 32;
        private const int ErrorInvalidParameter = 87;
        private const int ErrorNotSupported = 50;
        private const int ErrorInvalidFunction = 1;
        private const int ErrorCallNotImplemented = 120;
        private const int ErrorFileExists = 80;
        private const int ErrorAlreadyExists = 183;
        private const int ErrorFilenameTooLong = 206;
        private const int FileRenameInfo = 3;
        private const int FileDispositionInfo = 4;
        private const int FileDispositionInfoEx = 21;
        private const int FileRenameInfoEx = 22;

        public static FileSnapshot Inspect(string path)
        {
            string normalized = NormalizePath(path);
            using (SafeFileHandle handle = Open(normalized, ReadAttributes, ShareAll, OpenExisting))
            {
                return Snapshot(handle, normalized);
            }
        }

        public static FileOperationResult Delete(FileSnapshot target)
        {
            try
            {
                RequireSnapshot(target);
                bool posix;
                int error;
                using (SafeFileHandle handle = Open(target.Path, DeleteAccess | ReadAttributes,
                    ShareAll, OpenExisting))
                {
                    MatchTarget(handle, target);
                    if (!SetDelete(handle, out posix, out error))
                        return Failure("删除失败", error);
                }

                // A legacy disposition can succeed while another handle keeps
                // the name delete-pending. That is not a reusable pathname.
                int probeError;
                FileSnapshot remaining = TryInspectNormalized(target.Path, out probeError);
                if (remaining == null && (probeError == 2 || probeError == 3))
                    return Success(OperationStatus.Completed, "文件名已删除。旧的共享删除句柄可继续访问原文件内容。", null);
                if (remaining != null && !String.Equals(remaining.Identity, target.Identity, StringComparison.Ordinal))
                    return Success(OperationStatus.Completed, "原文件已删除；该路径现在出现了另一个文件。", null);

                return new FileOperationResult
                {
                    Status = OperationStatus.Partial,
                    ErrorCode = probeError,
                    RetainedPath = target.Path,
                    Message = posix
                        ? "系统已接受删除，但无法确认文件名已消失。请刷新后确认；不要把它视为已完成。" + ErrorSuffix(probeError)
                        : "系统已标记删除，文件名仍可能等待其他句柄关闭后才能消失和复用。请关闭占用程序后刷新。" + ErrorSuffix(probeError)
                };
            }
            catch (Exception ex)
            {
                if (!Expected(ex)) throw;
                return ExceptionFailure("删除失败", ex);
            }
        }

        public static FileOperationResult Replace(FileSnapshot target, string replacementSource)
        {
            OwnedStage stage = null;
            bool committed = false;
            try
            {
                RequireSnapshot(target);
                VerifyTarget(target);
                stage = CopySource(target, replacementSource);
                VerifyTarget(target);
                string stagedIdentity = Snapshot(stage.Handle, stage.Path).Identity;
                int error;
                if (!RenameStage(stage, target.Path, out error))
                    return FailAndClean(stage, Failure("替换失败，原目标没有被覆盖", error));

                stage.Preserve = true;
                committed = true;
                return ConfirmReplacement(stage, target.Path, stagedIdentity);
            }
            catch (Exception ex)
            {
                if (!Expected(ex)) throw;
                if (committed)
                    return new FileOperationResult
                    {
                        Status = OperationStatus.Partial,
                        ErrorCode = ex is Win32Exception ? ((Win32Exception)ex).NativeErrorCode : 0,
                        RetainedPath = stage.ActualPath,
                        Message = "系统已接受替换，但后续检查未完成，无法确认目标结果。请刷新后确认。" + ex.Message
                    };
                return FailAndClean(stage, ExceptionFailure("替换失败，原目标没有被覆盖", ex));
            }
            finally
            {
                if (stage != null) stage.Dispose();
            }
        }

        public static FileOperationResult ScheduleDelete(FileSnapshot target)
        {
            try
            {
                RequireSnapshot(target);
                RequireRebootAuthority(target.Path);
                VerifyTarget(target);
                if (!Native.MoveFileEx(ToNativePath(target.Path), null, MoveDelayUntilReboot))
                    return Failure("登记重启删除失败", Marshal.GetLastWin32Error());
                return Success(OperationStatus.Scheduled,
                    "已登记在下次 Windows 重新启动时删除。此结果仅表示排队成功；重启前请勿移动或替换目标文件。", target.Path);
            }
            catch (Exception ex)
            {
                if (!Expected(ex)) throw;
                return ExceptionFailure("登记重启删除失败", ex);
            }
        }

        public static FileOperationResult ScheduleReplace(FileSnapshot target, string replacementSource)
        {
            OwnedStage stage = null;
            try
            {
                RequireSnapshot(target);
                RequireRebootAuthority(target.Path);
                VerifyTarget(target);
                stage = CopySource(target, replacementSource);
                VerifyTarget(target);
                if (!Native.MoveFileEx(ToNativePath(stage.Path), ToNativePath(target.Path),
                    MoveDelayUntilReboot | MoveReplaceExisting))
                    return FailAndClean(stage, Failure("登记重启替换失败", Marshal.GetLastWin32Error()));
                stage.Preserve = true;
                stage.Dispose();
                return Success(OperationStatus.Scheduled,
                    "已登记在下次 Windows 重新启动时替换。来源文件保留；排队成功不等于替换已完成。重启前请保留临时文件并勿移动或替换目标。", stage.Path);
            }
            catch (Exception ex)
            {
                if (!Expected(ex)) throw;
                return FailAndClean(stage, ExceptionFailure("登记重启替换失败", ex));
            }
            finally
            {
                if (stage != null) stage.Dispose();
            }
        }

        private static void RequireSnapshot(FileSnapshot target)
        {
            if (target == null || String.IsNullOrEmpty(target.Path) || String.IsNullOrEmpty(target.Identity))
                throw new ArgumentException("请先选择并检查一个现有的普通文件。");
        }

        internal static void VerifyTarget(FileSnapshot target)
        {
            using (SafeFileHandle handle = Open(target.Path, ReadAttributes, ShareAll, OpenExisting))
                MatchTarget(handle, target);
        }

        private static void MatchTarget(SafeFileHandle handle, FileSnapshot target)
        {
            FileSnapshot current = Snapshot(handle, target.Path);
            if (!String.Equals(current.Identity, target.Identity, StringComparison.Ordinal))
                throw new IOException("目标文件身份已改变。为避免操作另一个文件，请刷新目标后重新确认。");
        }

        private static SafeFileHandle Open(string normalizedPath, uint access, uint share, uint disposition)
        {
            SafeFileHandle handle = Native.CreateFile(ToNativePath(normalizedPath), access, share,
                IntPtr.Zero, disposition, NormalAttribute | OpenReparsePoint | BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "无法打开文件：" + normalizedPath + ErrorSuffix(error));
            }
            return handle;
        }

        private static FileSnapshot Snapshot(SafeFileHandle handle, string normalizedPath)
        {
            ByHandleFileInformation info;
            if (!Native.GetFileInformationByHandle(handle, out info))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, "无法确认文件身份：" + normalizedPath + ErrorSuffix(error));
            }
            if ((info.FileAttributes & DirectoryAttribute) != 0)
                throw new ArgumentException("仅支持单个文件，不支持目录：" + normalizedPath);
            if ((info.FileAttributes & ReparsePointAttribute) != 0)
                throw new ArgumentException("不支持把符号链接、重解析点或云占位文件作为目标：" + normalizedPath);
            ulong length = ((ulong)info.FileSizeHigh << 32) | info.FileSizeLow;
            if (length > Int64.MaxValue)
                throw new IOException("文件长度超过此项目支持的范围：" + normalizedPath);
            string identity = info.VolumeSerialNumber.ToString("X8") + ":" +
                info.FileIndexHigh.ToString("X8") + info.FileIndexLow.ToString("X8");
            return new FileSnapshot(normalizedPath, identity, (long)length,
                (info.FileAttributes & ReadOnlyAttribute) != 0);
        }

        private static FileSnapshot TryInspectNormalized(string normalizedPath, out int error)
        {
            SafeFileHandle handle = Native.CreateFile(ToNativePath(normalizedPath), ReadAttributes,
                ShareAll, IntPtr.Zero, OpenExisting, NormalAttribute | OpenReparsePoint | BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                error = Marshal.GetLastWin32Error();
                handle.Dispose();
                return null;
            }
            using (handle)
            {
                try
                {
                    FileSnapshot result = Snapshot(handle, normalizedPath);
                    error = 0;
                    return result;
                }
                catch (Win32Exception ex) { error = ex.NativeErrorCode; return null; }
                catch (ArgumentException) { error = ErrorInvalidParameter; return null; }
                catch (IOException) { error = ErrorInvalidParameter; return null; }
            }
        }

        private static bool SetDelete(SafeFileHandle handle, out bool posix, out int error)
        {
            // Ignore-readonly is optional on older systems. Never clear the
            // original file's readonly attribute as an attempted workaround.
            posix = true;
            if (SetFlags(handle, FileDispositionInfoEx, 0x13, out error)) return true;
            if (!Unsupported(error)) return false;
            if (SetFlags(handle, FileDispositionInfoEx, 0x03, out error)) return true;
            if (!Unsupported(error)) return false;
            posix = false;
            IntPtr buffer = Marshal.AllocHGlobal(1);
            try
            {
                Marshal.WriteByte(buffer, 1);
                bool ok = Native.SetFileInformationByHandle(handle, FileDispositionInfo, buffer, 1);
                error = ok ? 0 : Marshal.GetLastWin32Error();
                return ok;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static bool SetFlags(SafeFileHandle handle, int informationClass, uint flags, out int error)
        {
            IntPtr buffer = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(buffer, unchecked((int)flags));
                bool ok = Native.SetFileInformationByHandle(handle, informationClass, buffer, 4);
                error = ok ? 0 : Marshal.GetLastWin32Error();
                return ok;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static OwnedStage CopySource(FileSnapshot target, string replacementSource)
        {
            string sourcePath = NormalizePath(replacementSource);
            // Excluding write sharing refuses ordinary existing and future
            // writers while reading. It is not a lock on preexisting mapped views.
            using (SafeFileHandle source = Open(sourcePath, GenericRead | ReadAttributes,
                ShareRead | ShareDelete, OpenExisting))
            {
                FileSnapshot sourceInfo = Snapshot(source, sourcePath);
                if (String.Equals(sourceInfo.Identity, target.Identity, StringComparison.Ordinal))
                    throw new ArgumentException("替换来源与目标是同一个文件（包括硬链接别名），请另选来源文件。");
                OwnedStage stage = CreateStage(target.Path);
                try
                {
                    byte[] buffer = new byte[65536];
                    for (;;)
                    {
                        uint read;
                        if (!Native.ReadFile(source, buffer, (uint)buffer.Length, out read, IntPtr.Zero))
                        {
                            int error = Marshal.GetLastWin32Error();
                            throw new Win32Exception(error, "读取替换来源失败：" + sourcePath + ErrorSuffix(error));
                        }
                        if (read == 0) break;
                        uint written;
                        if (!Native.WriteFile(stage.Handle, buffer, read, out written, IntPtr.Zero))
                        {
                            int error = Marshal.GetLastWin32Error();
                            throw new Win32Exception(error, "写入临时替换文件失败：" + stage.Path + ErrorSuffix(error));
                        }
                        if (written != read)
                            throw new Win32Exception(29, "写入临时替换文件不完整，原目标没有被覆盖。");
                    }
                    if (!Native.FlushFileBuffers(stage.Handle))
                    {
                        int error = Marshal.GetLastWin32Error();
                        throw new Win32Exception(error, "刷新临时替换文件失败：" + stage.Path + ErrorSuffix(error));
                    }
                    return stage;
                }
                catch (Exception ex)
                {
                    int cleanupError;
                    if (!stage.Clean(out cleanupError))
                        throw new StagePreparationException(ex, stage.Path, cleanupError);
                    throw;
                }
            }
        }

        private static OwnedStage CreateStage(string targetPath)
        {
            int separator = targetPath.LastIndexOf('\\');
            if (separator < 0) throw new ArgumentException("目标文件没有有效的父目录。");
            string directory = targetPath.Substring(0, separator + 1);
            for (int attempt = 0; attempt < 4; ++attempt)
            {
                string stagePath = directory + ".winunlink-" + Guid.NewGuid().ToString("N") + ".tmp";
                SafeFileHandle handle = Native.CreateFile(ToNativePath(stagePath),
                    GenericRead | GenericWrite | DeleteAccess | ReadAttributes, 0, IntPtr.Zero,
                    CreateNew, NormalAttribute | OpenReparsePoint, IntPtr.Zero);
                if (!handle.IsInvalid) return new OwnedStage(stagePath, handle);
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (error != ErrorFileExists && error != ErrorAlreadyExists)
                    throw new Win32Exception(error, "无法在目标目录创建临时替换文件：" + directory + ErrorSuffix(error));
            }
            throw new IOException("无法取得唯一的临时替换文件名，请稍后重试。");
        }

        private static bool RenameStage(OwnedStage stage, string targetPath, out int error)
        {
            if (Rename(stage.Handle, targetPath, FileRenameInfoEx, 0x43, out error)) return true;
            if (!Unsupported(error)) return false;
            if (Rename(stage.Handle, targetPath, FileRenameInfoEx, 0x03, out error)) return true;
            if (!Unsupported(error)) return false;
            return Rename(stage.Handle, targetPath, FileRenameInfo, 1, out error);
        }

        private static FileOperationResult ConfirmReplacement(OwnedStage stage, string targetPath, string stagedIdentity)
        {
            int nameError;
            string actualPath = TryActualPath(stage.Handle, out nameError);
            if (actualPath != null)
            {
                stage.ActualPath = actualPath;
                stage.Path = actualPath;
            }

            int targetError;
            FileSnapshot finalTarget = TryInspectNormalized(targetPath, out targetError);
            if (finalTarget != null && String.Equals(finalTarget.Identity, stagedIdentity, StringComparison.Ordinal))
                return Success(OperationStatus.Completed,
                    "已用新文件替换目标，并核实目标文件身份。来源文件保留；仍持有旧共享删除句柄的程序继续访问旧内容，新打开的程序访问新内容。", null);

            string detail = finalTarget == null
                ? " 无法读取目标文件身份。" + ErrorSuffix(targetError)
                : " 当前目标文件身份与已提交的新文件不一致。";
            detail += actualPath != null
                ? " 已提交文件的实际路径：" + actualPath
                : " 也无法确认已提交文件的实际路径。" + ErrorSuffix(nameError);
            return new FileOperationResult
            {
                Status = OperationStatus.Partial,
                ErrorCode = targetError,
                RetainedPath = actualPath,
                Message = "系统已接受替换，但无法确认请求的目标已成为新文件。请刷新后检查；此结果不表示原目标保持不变。" + detail
            };
        }

        private static string TryActualPath(SafeFileHandle handle, out int error)
        {
            StringBuilder buffer = new StringBuilder(512);
            uint needed = Native.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (needed == 0)
            {
                error = Marshal.GetLastWin32Error();
                return null;
            }
            if (needed >= buffer.Capacity)
            {
                if (needed >= 32767) { error = ErrorFilenameTooLong; return null; }
                buffer = new StringBuilder(checked((int)needed + 1));
                needed = Native.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (needed == 0 || needed >= buffer.Capacity)
                {
                    error = needed == 0 ? Marshal.GetLastWin32Error() : ErrorFilenameTooLong;
                    return null;
                }
            }
            error = 0;
            string result = buffer.ToString();
            if (result.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
                return "\\\\" + result.Substring(8);
            if (result.StartsWith("\\\\?\\", StringComparison.Ordinal)) return result.Substring(4);
            return result;
        }

        private static bool Rename(SafeFileHandle handle, string destination, int informationClass,
            uint flags, out int error)
        {
            char[] name = ToNativePath(destination).ToCharArray();
            int nameBytes = checked(name.Length * 2);
            int rootOffset = IntPtr.Size == 8 ? 8 : 4;
            int lengthOffset = rootOffset + IntPtr.Size;
            int nameOffset = lengthOffset + 4;
            // The Win32 wrapper also needs a terminated pathname even though
            // FileNameLength excludes that terminator. Without it the wrapper
            // can consume adjacent memory and rename to a corrupted name.
            int size = checked(nameOffset + nameBytes + 2);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                // The header layout differs between x86 and x64. The legacy
                // BOOLEAN ReplaceIfExists shares the first byte with Flags.
                for (int i = 0; i < nameOffset; ++i) Marshal.WriteByte(buffer, i, 0);
                Marshal.WriteInt32(buffer, unchecked((int)flags));
                Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
                Marshal.WriteInt32(buffer, lengthOffset, nameBytes);
                Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
                Marshal.WriteInt16(buffer, nameOffset + nameBytes, 0);
                bool ok = Native.SetFileInformationByHandle(handle, informationClass, buffer, (uint)size);
                error = ok ? 0 : Marshal.GetLastWin32Error();
                return ok;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static FileOperationResult FailAndClean(OwnedStage stage, FileOperationResult result)
        {
            if (stage == null) return result;
            int cleanupError;
            if (!stage.Clean(out cleanupError))
            {
                result.RetainedPath = stage.Path;
                result.Message += " 临时文件清理失败，请手动处理：" + stage.Path + ErrorSuffix(cleanupError);
            }
            return result;
        }

        private static string NormalizePath(string input)
        {
            if (String.IsNullOrWhiteSpace(input)) throw new ArgumentException("请选择一个文件路径。");
            if (input.IndexOf('\0') >= 0) throw new ArgumentException("文件路径包含空字符。");
            string path = input.Replace('/', '\\');
            if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
                path = "\\\\" + path.Substring(8);
            else if (path.StartsWith("\\\\?\\", StringComparison.Ordinal))
            {
                path = path.Substring(4);
                if (path.Length < 3 || !IsDriveLetter(path[0]) || path[1] != ':' || path[2] != '\\')
                    throw new ArgumentException("仅支持普通盘符路径和 UNC 文件路径，不支持设备或卷 GUID 路径。");
            }
            if (path.StartsWith("\\\\.\\", StringComparison.Ordinal) || path.StartsWith("\\??\\", StringComparison.Ordinal) ||
                path.StartsWith("\\\\??\\", StringComparison.Ordinal) || path.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("不支持设备路径。");
            RejectStreams(path);
            StringBuilder buffer = new StringBuilder(512);
            uint needed = Native.GetFullPathName(path, (uint)buffer.Capacity, buffer, IntPtr.Zero);
            if (needed == 0)
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, "无法规范化文件路径。" + ErrorSuffix(error));
            }
            if (needed >= buffer.Capacity)
            {
                if (needed >= 32767) throw new Win32Exception(ErrorFilenameTooLong, "文件路径超出 Windows 支持的长度。");
                buffer = new StringBuilder(checked((int)needed + 1));
                needed = Native.GetFullPathName(path, (uint)buffer.Capacity, buffer, IntPtr.Zero);
                if (needed == 0 || needed >= buffer.Capacity)
                    throw new Win32Exception(needed == 0 ? Marshal.GetLastWin32Error() : ErrorFilenameTooLong,
                        "无法规范化文件路径。");
            }
            string normalized = buffer.ToString();
            RejectStreams(normalized);
            string[] components = normalized.Split('\\');
            foreach (string component in components)
            {
                if (component.Length == 0 || (component.Length == 2 && component[1] == ':')) continue;
                if (component.EndsWith(".", StringComparison.Ordinal) || component.EndsWith(" ", StringComparison.Ordinal))
                    throw new ArgumentException("不支持末尾为点或空格的路径组件。");
                string stem = component.Split('.')[0].ToUpperInvariant();
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CLOCK$" ||
                    (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                     "123456789¹²³".IndexOf(stem[3]) >= 0))
                    throw new ArgumentException("不支持保留设备名称：" + component);
            }
            ToNativePath(normalized);
            return normalized;
        }

        private static void RejectStreams(string path)
        {
            for (int i = 0; i < path.Length; ++i)
                if (path[i] == ':' && !(i == 1 && IsDriveLetter(path[0])))
                    throw new ArgumentException("不支持备用数据流（ADS）路径，请选择普通文件。");
        }

        private static bool IsDriveLetter(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
        }

        private static string ToNativePath(string normalized)
        {
            string native = normalized.StartsWith("\\\\", StringComparison.Ordinal)
                ? "\\\\?\\UNC\\" + normalized.Substring(2) : "\\\\?\\" + normalized;
            if (native.Length >= 32767)
                throw new Win32Exception(ErrorFilenameTooLong, "文件路径超出 Windows 支持的长度。");
            return native;
        }

        private static void RequireRebootAuthority(string path)
        {
            if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.Length < 3 || path[1] != ':')
                throw new ArgumentException("重启操作只支持本地盘符中的文件，不支持 UNC 或网络路径。");
            uint driveType = Native.GetDriveType(path.Substring(0, 3));
            if (driveType == 0 || driveType == 1 || driveType == 4)
                throw new ArgumentException("重启操作只支持可确认的本地磁盘，不支持网络映射盘。");
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new Win32Exception(ErrorAccessDenied, "登记重启操作需要管理员权限，请以管理员身份重新打开工具。");
        }

        private static bool Unsupported(int error)
        {
            return error == ErrorInvalidParameter || error == ErrorNotSupported ||
                error == ErrorInvalidFunction || error == ErrorCallNotImplemented;
        }

        private static FileOperationResult Success(OperationStatus status, string message, string retainedPath)
        {
            return new FileOperationResult { Status = status, Message = message, ErrorCode = 0, RetainedPath = retainedPath };
        }

        private static FileOperationResult Failure(string context, int error)
        {
            string recovery = error == ErrorSharingViolation
                ? " 文件占用句柄未允许共享删除。请先关闭占用程序，或明确选择结束占用进程后重试。"
                : error == ErrorAccessDenied
                    ? " 请检查文件权限；提升权限也不能绕过共享锁、受保护进程或某些映射限制。"
                    : Unsupported(error) ? " 此 Windows 版本或文件系统不支持本次操作。" : String.Empty;
            return new FileOperationResult
            {
                Status = OperationStatus.Failed,
                Message = context + ErrorSuffix(error) + recovery,
                ErrorCode = error
            };
        }

        private static FileOperationResult ExceptionFailure(string context, Exception ex)
        {
            StagePreparationException preparation = ex as StagePreparationException;
            if (preparation != null)
            {
                FileOperationResult result = ExceptionFailure(context, preparation.InnerException);
                result.RetainedPath = preparation.StagePath;
                result.Message += " 临时文件清理失败，请手动处理：" + preparation.StagePath + ErrorSuffix(preparation.CleanupError);
                return result;
            }
            Win32Exception native = ex as Win32Exception;
            if (native != null)
            {
                FileOperationResult result = Failure(context, native.NativeErrorCode);
                result.Message = context + "：" + native.Message +
                    (native.NativeErrorCode == ErrorSharingViolation
                        ? " 请关闭占用程序，或明确选择结束占用进程后重试。" : String.Empty);
                return result;
            }
            return new FileOperationResult { Status = OperationStatus.Failed, Message = context + "：" + ex.Message, ErrorCode = 0 };
        }

        private static bool Expected(Exception ex)
        {
            return ex is Win32Exception || ex is IOException || ex is ArgumentException ||
                ex is UnauthorizedAccessException || ex is SecurityException || ex is NotSupportedException;
        }

        private static string ErrorSuffix(int error)
        {
            return error == 0 ? String.Empty : "（Windows 错误 " + error + "：" + new Win32Exception(error).Message + "）";
        }

        private sealed class StagePreparationException : IOException
        {
            internal readonly string StagePath;
            internal readonly int CleanupError;
            internal StagePreparationException(Exception inner, string path, int cleanupError)
                : base("临时替换文件准备失败，且清理未完成。", inner)
            {
                StagePath = path;
                CleanupError = cleanupError;
            }
        }

        private sealed class OwnedStage : IDisposable
        {
            internal string Path;
            internal string ActualPath;
            internal readonly SafeFileHandle Handle;
            internal bool Preserve;
            private bool disposed;

            internal OwnedStage(string path, SafeFileHandle handle) { Path = path; Handle = handle; }

            internal bool Clean(out int error)
            {
                // A committed file is no longer an abandoned temporary file.
                // Never remove it as error recovery after native acceptance.
                if (Preserve) { Dispose(); error = 0; return true; }
                if (disposed) { error = 0; return Preserve; }
                bool posix;
                bool deleted = SetDelete(Handle, out posix, out error);
                Dispose();
                if (!deleted) return false;
                uint attributes = Native.GetFileAttributes(ToNativePath(Path));
                if (attributes == InvalidAttributes)
                {
                    int readError = Marshal.GetLastWin32Error();
                    if (readError == 2 || readError == 3) { error = 0; return true; }
                    error = readError;
                    return false;
                }
                error = 0;
                return false;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                Handle.Dispose();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileTime { public uint Low; public uint High; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public NativeFileTime CreationTime;
            public NativeFileTime LastAccessTime;
            public NativeFileTime LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        private static class Native
        {
            [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern SafeFileHandle CreateFile(string path, uint access, uint share,
                IntPtr security, uint disposition, uint flags, IntPtr template);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, IntPtr info, uint length);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, uint count, out uint read, IntPtr overlapped);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool WriteFile(SafeFileHandle handle, byte[] buffer, uint count, out uint written, IntPtr overlapped);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool FlushFileBuffers(SafeFileHandle handle);
            [DllImport("kernel32.dll", EntryPoint = "GetFullPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern uint GetFullPathName(string path, uint length, StringBuilder buffer, IntPtr filePart);
            [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder buffer, uint length, uint flags);
            [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesW", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern uint GetFileAttributes(string path);
            [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)]
            internal static extern uint GetDriveType(string rootPath);
            [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool MoveFileEx(string existingPath, string newPath, uint flags);
        }
    }
}
