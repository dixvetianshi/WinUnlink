using System;
using System.Collections.Generic;
using System.Linq;

namespace WinUnlink
{
    public enum DeleteMode { RecycleBin, Permanent }

    public sealed class TargetSnapshot
    {
        public ItemSnapshot Root { get; private set; }
        public DirectorySnapshot Directory { get; private set; }
        public bool IsDirectory { get { return Root.IsDirectory; } }

        internal TargetSnapshot(ItemSnapshot root, DirectorySnapshot directory)
        {
            Root = root;
            Directory = directory;
        }
    }

    public static class TargetOperations
    {
        public static TargetSnapshot Inspect(string path)
        {
            ItemSnapshot root = FileOperations.InspectEntry(path);
            DirectorySnapshot directory = root.IsDirectory ? DirectoryOperations.Inspect(root) : null;
            return new TargetSnapshot(root, directory);
        }

        public static void Verify(TargetSnapshot target)
        {
            if (target == null) throw new ArgumentException("请先选择目标文件或文件夹。");
            if (target.IsDirectory) DirectoryOperations.Verify(target.Directory);
            else FileOperations.VerifyTarget(target.Root);
        }

        public static FileOperationResult Delete(TargetSnapshot target, DeleteMode mode = DeleteMode.RecycleBin)
        {
            if (target == null) return MissingSelection();
            switch (mode)
            {
                case DeleteMode.RecycleBin: return RecycleBinOperations.Delete(target);
                case DeleteMode.Permanent:
                    return target.IsDirectory ? DirectoryOperations.Delete(target.Directory) : FileOperations.Delete(target.Root);
                default: return new FileOperationResult { Status = OperationStatus.Failed, Message = "删除方式无效，请选择回收站或直接删除。" };
            }
        }

        public static FileOperationResult Replace(TargetSnapshot target, string source)
        {
            if (target == null) return MissingSelection();
            return target.IsDirectory ? DirectoryOperations.Replace(target.Directory, source) : FileOperations.Replace(target.Root, source);
        }

        public static LockScanResult Scan(TargetSnapshot target)
        {
            Verify(target);
            if (!target.IsDirectory) return LockInspector.Scan(target.Root.Path);
            string[] paths = target.Directory.Entries.Where(entry => !entry.IsDirectory).Select(entry => entry.Path).ToArray();
            return LockInspector.ScanFiles(paths);
        }

        public static FileOperationResult ScheduleDelete(TargetSnapshot target, DeleteMode mode = DeleteMode.RecycleBin)
        {
            if (target == null) return MissingSelection();
            if (mode != DeleteMode.Permanent)
                return new FileOperationResult { Status = OperationStatus.Failed,
                    Message = "重启删除只支持用户明确选择的直接删除模式。没有登记回收站或删除任务。" };
            return target.IsDirectory ? UnsupportedDirectoryReboot() : FileOperations.ScheduleDelete(target.Root);
        }

        public static FileOperationResult ScheduleReplace(TargetSnapshot target, string source)
        {
            if (target == null) return MissingSelection();
            return target.IsDirectory ? UnsupportedDirectoryReboot() : FileOperations.ScheduleReplace(target.Root, source);
        }

        private static FileOperationResult MissingSelection()
        {
            return new FileOperationResult { Status = OperationStatus.Failed, Message = "请先选择并查询目标文件或文件夹。" };
        }

        private static FileOperationResult UnsupportedDirectoryReboot()
        {
            return new FileOperationResult { Status = OperationStatus.Failed,
                Message = "文件夹不支持重启排队。请关闭占用程序或使用“停止并删除”后重试；未登记任何重启任务。" };
        }
    }
}
