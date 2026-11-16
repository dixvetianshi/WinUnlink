using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security;

namespace WinUnlink
{
    public sealed class DirectorySnapshot
    {
        public ItemSnapshot Root { get; private set; }
        public string Path { get { return Root.Path; } }
        // Entries excludes Root. DirectoryCount includes the selected root.
        public ReadOnlyCollection<ItemSnapshot> Entries { get; private set; }
        public int FileCount { get; private set; }
        public int DirectoryCount { get; private set; }
        public long TotalBytes { get; private set; }

        internal DirectorySnapshot(ItemSnapshot root, IList<ItemSnapshot> entries)
        {
            Root = root;
            Entries = new ReadOnlyCollection<ItemSnapshot>(new List<ItemSnapshot>(entries));
            DirectoryCount = 1;
            foreach (ItemSnapshot entry in entries)
            {
                if (entry.IsDirectory) DirectoryCount = checked(DirectoryCount + 1);
                else
                {
                    FileCount = checked(FileCount + 1);
                    TotalBytes = checked(TotalBytes + entry.Length);
                }
            }
        }
    }

    public static class DirectoryOperations
    {
        public static DirectorySnapshot Inspect(string path)
        {
            return Inspect(FileOperations.InspectEntry(path));
        }

        internal static DirectorySnapshot Inspect(ItemSnapshot root)
        {
            RequireDirectory(root);
            FileOperations.VerifyEntry(root);
            List<ItemSnapshot> entries = new List<ItemSnapshot>();
            Stack<ItemSnapshot> pending = new Stack<ItemSnapshot>();
            pending.Push(root);
            while (pending.Count != 0)
            {
                ItemSnapshot directory = pending.Pop();
                FileOperations.VerifyEntry(directory);
                foreach (string path in Directory.EnumerateFileSystemEntries(FileOperations.ToNativePath(directory.Path)))
                {
                    // InspectEntry opens the entry without following its final
                    // reparse point and owns all identity/path interpretation.
                    ItemSnapshot entry = FileOperations.InspectEntry(path);
                    Relative(root, entry);
                    entries.Add(entry);
                    if (entry.IsDirectory) pending.Push(entry);
                }
            }
            FileOperations.VerifyEntry(root);
            entries.Sort(delegate(ItemSnapshot left, ItemSnapshot right)
                { return StringComparer.Ordinal.Compare(left.Path, right.Path); });
            return new DirectorySnapshot(root, entries);
        }

        public static void Verify(DirectorySnapshot expected)
        {
            RequireSnapshot(expected);
            CompareTrees(expected, Inspect(expected.Root));
        }

        public static FileOperationResult Delete(DirectorySnapshot target)
        {
            int removed = 0;
            try
            {
                RequireSnapshot(target);
                Verify(target);
                IEnumerable<ItemSnapshot> ordered = target.Entries.Where(entry => !entry.IsDirectory)
                    .Concat(target.Entries.Where(entry => entry.IsDirectory)
                        .OrderByDescending(entry => Depth(Relative(target.Root, entry)))
                        .ThenBy(entry => entry.Path, StringComparer.Ordinal))
                    .Concat(new ItemSnapshot[] { target.Root });
                foreach (ItemSnapshot entry in ordered)
                {
                    FileOperations.VerifyEntry(target.Root);
                    FileOperationResult deletion = FileOperations.DeleteEntry(entry);
                    if (deletion.Status != OperationStatus.Completed)
                    {
                        return new FileOperationResult
                        {
                            Status = removed == 0 && deletion.Status == OperationStatus.Failed
                                ? OperationStatus.Failed : OperationStatus.Partial,
                            ErrorCode = deletion.ErrorCode,
                            RetainedPath = target.Path,
                            Message = "文件夹删除未完成，已移除 " + removed + " 个项目。失败位置：" + entry.Path +
                                "\r\n" + deletion.Message + "\r\n请处理剩余占用或权限后重新查询该文件夹。"
                        };
                    }
                    removed++;
                }
                return Completed("已删除整个文件夹及其内容，共 " + target.FileCount + " 个文件、" +
                    target.DirectoryCount + " 个文件夹。", null);
            }
            catch (Exception error)
            {
                if (!Expected(error)) throw;
                return new FileOperationResult
                {
                    Status = removed == 0 ? OperationStatus.Failed : OperationStatus.Partial,
                    ErrorCode = NativeCode(error),
                    RetainedPath = target == null ? null : target.Path,
                    Message = "文件夹删除未完成，已移除 " + removed + " 个项目：" + error.Message
                };
            }
        }

        public static FileOperationResult Replace(DirectorySnapshot target, string replacementSource)
        {
            OwnedDirectory stage = null;
            ItemSnapshot backup = null;
            bool committed = false;
            try
            {
                RequireSnapshot(target);
                DirectorySnapshot source = Inspect(replacementSource);
                RejectOverlap(target.Root, source.Root);
                Verify(target);
                stage = CopyTree(source, target.Root);
                Verify(source);
                Verify(target);
                DirectorySnapshot staged = stage.Snapshot();
                Verify(staged);

                string backupPath = NewSibling(target.Root, "backup");
                backup = FileOperations.MoveEntry(target.Root, backupPath);
                try
                {
                    stage.Root = FileOperations.MoveEntry(stage.Root, target.Path);
                    committed = true;
                    stage.Preserve = true;
                }
                catch (Exception commitError)
                {
                    if (!Expected(commitError)) throw;
                    return RestoreAfterCommitFailure(target, backup, stage, commitError);
                }

                // Both renames have succeeded. Verify the published tree before
                // deleting the rollback copy; uncertainty retains that copy.
                DirectorySnapshot published;
                try
                {
                    published = Inspect(stage.Root);
                    CompareTrees(staged, published);
                }
                catch (Exception verificationError)
                {
                    if (!Expected(verificationError)) throw;
                    return Partial("系统已接受文件夹替换，但新目标的检查未完成。原目录保留在备份位置：" +
                        backup.Path + "\r\n" + verificationError.Message, backup.Path, NativeCode(verificationError));
                }

                DirectorySnapshot oldTree;
                try
                {
                    oldTree = Inspect(backup);
                    CompareTrees(target, oldTree);
                }
                catch (Exception backupError)
                {
                    if (!Expected(backupError)) throw;
                    return Partial("新文件夹已替换并核实；原目录备份的状态发生变化，未清理备份：" + backup.Path +
                        "\r\n" + backupError.Message, backup.Path, NativeCode(backupError));
                }
                FileOperationResult cleanup = Delete(oldTree);
                if (cleanup.Status != OperationStatus.Completed)
                    return Partial("新文件夹已替换并核实，来源保持保留。原目录备份未完全清理，剩余内容位于：" +
                        backup.Path + "\r\n" + cleanup.Message, backup.Path, cleanup.ErrorCode);
                return Completed("已用来源文件夹整体替换目标，来源文件夹保留；原目标中未出现在来源的文件也已移除。", null);
            }
            catch (Exception error)
            {
                if (!Expected(error)) throw;
                if (committed)
                    return Partial("文件夹替换已提交，但后续处理未完成。请检查目标及保留的备份：" +
                        (backup == null ? String.Empty : backup.Path) + "\r\n" + error.Message,
                        backup == null ? null : backup.Path, NativeCode(error));
                if (backup != null)
                    return RestoreAfterCommitFailure(target, backup, stage, error);
                FileOperationResult failure = Failed("文件夹替换失败，原目标未被替换：" + error.Message, NativeCode(error));
                DirectoryPreparationException preparation = error as DirectoryPreparationException;
                if (preparation != null) failure.RetainedPath = preparation.RetainedPath;
                EntryCreationException creation = error as EntryCreationException;
                if (creation != null) failure.RetainedPath = creation.CreatedPath;
                return CleanStage(stage, failure);
            }
        }

        private static OwnedDirectory CopyTree(DirectorySnapshot source, ItemSnapshot target)
        {
            ItemSnapshot stageRoot = FileOperations.CreateNewDirectory(NewSibling(target, "stage"));
            OwnedDirectory stage = new OwnedDirectory(stageRoot);
            try
            {
                foreach (ItemSnapshot directory in source.Entries.Where(entry => entry.IsDirectory)
                    .OrderBy(entry => Depth(Relative(source.Root, entry))).ThenBy(entry => entry.Path, StringComparer.Ordinal))
                {
                    FileOperations.VerifyEntry(directory);
                    stage.Created.Add(FileOperations.CreateNewDirectory(StagePath(stage.Root, Relative(source.Root, directory))));
                }
                foreach (ItemSnapshot file in source.Entries.Where(entry => !entry.IsDirectory))
                    stage.Created.Add(FileOperations.CopyNewFile(file, StagePath(stage.Root, Relative(source.Root, file))));
                return stage;
            }
            catch (Exception error)
            {
                if (!Expected(error)) throw;
                FileOperationResult failure = CleanStage(stage, Failed("复制来源文件夹失败：" + error.Message, NativeCode(error)));
                throw new DirectoryPreparationException(error, failure.RetainedPath, failure.Message, failure.ErrorCode);
            }
        }

        private static FileOperationResult RestoreAfterCommitFailure(DirectorySnapshot original, ItemSnapshot backup,
            OwnedDirectory stage, Exception error)
        {
            ItemSnapshot restored = null;
            try
            {
                restored = FileOperations.MoveEntry(backup, original.Path);
                CompareTrees(original, Inspect(restored));
            }
            catch (Exception restoreError)
            {
                if (!Expected(restoreError)) throw;
                string stagePath = stage == null ? null : stage.Root.Path;
                string retainedOriginal = restored == null ? backup.Path : restored.Path;
                return Partial((restored == null ? "新文件夹没有完成提交，原目录恢复也未完成。原内容保留在：" :
                    "新文件夹没有完成提交，原目录已移回目标，但无法确认恢复后的完整状态。原内容位于：") + retainedOriginal +
                    (stagePath == null ? String.Empty : "\r\n准备的新目录：" + stagePath) +
                    "\r\n提交错误：" + error.Message + "\r\n恢复错误：" + restoreError.Message,
                    retainedOriginal, NativeCode(restoreError));
            }
            return CleanStage(stage, Failed("文件夹替换失败，已恢复原目录：" + error.Message, NativeCode(error)));
        }

        private static FileOperationResult CleanStage(OwnedDirectory stage, FileOperationResult result)
        {
            if (stage == null || stage.Preserve) return result;
            try
            {
                DirectorySnapshot owned = stage.Snapshot();
                Verify(owned);
                FileOperationResult cleanup = Delete(owned);
                if (cleanup.Status == OperationStatus.Completed) return result;
                result.RetainedPath = stage.Root.Path;
                result.Message += "\r\n本工具创建的临时目录未完全清理：" + stage.Root.Path + "\r\n" + cleanup.Message;
            }
            catch (Exception cleanupError)
            {
                if (!Expected(cleanupError)) throw;
                result.RetainedPath = stage.Root.Path;
                result.Message += "\r\n本工具创建的临时目录无法确认或清理，已保留：" + stage.Root.Path +
                    "\r\n" + cleanupError.Message;
            }
            return result;
        }

        private static void RequireSnapshot(DirectorySnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentException("请先查询目标文件夹。");
            RequireDirectory(snapshot.Root);
        }

        private static void RequireDirectory(ItemSnapshot root)
        {
            if (root == null || !root.IsDirectory) throw new ArgumentException("此操作需要普通文件夹。");
            if (String.IsNullOrEmpty(root.CanonicalPath))
                throw new IOException("无法确认文件夹的实际路径，不能安全判断目录范围。请重新查询。");
            string pathRoot = Path.GetPathRoot(root.CanonicalPath);
            if (String.IsNullOrEmpty(pathRoot) || String.Equals(root.CanonicalPath.TrimEnd('\\'),
                pathRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("不支持把磁盘根目录或 UNC 共享根目录作为文件夹目标或来源。");
        }

        private static void RejectOverlap(ItemSnapshot target, ItemSnapshot source)
        {
            if (String.Equals(target.Identity, source.Identity, StringComparison.Ordinal) ||
                IsWithin(target.CanonicalPath, source.CanonicalPath) || IsWithin(source.CanonicalPath, target.CanonicalPath))
                throw new ArgumentException("目标与来源不能是同一个文件夹，也不能互相包含。请选择目标之外的独立来源文件夹。");
        }

        private static bool IsWithin(string parent, string child)
        {
            string prefix = parent.TrimEnd('\\') + "\\";
            return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string Relative(ItemSnapshot root, ItemSnapshot entry)
        {
            string prefix = root.Path.TrimEnd('\\') + "\\";
            if (!entry.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("目录枚举返回了目标范围之外的路径：" + entry.Path);
            return entry.Path.Substring(prefix.Length);
        }

        private static int Depth(string relative)
        {
            int depth = 0;
            foreach (char character in relative) if (character == '\\') depth++;
            return depth;
        }

        private static string StagePath(ItemSnapshot stage, string relative)
        {
            return stage.Path.TrimEnd('\\') + "\\" + relative;
        }

        private static string NewSibling(ItemSnapshot target, string kind)
        {
            string parent = Path.GetDirectoryName(target.Path.TrimEnd('\\'));
            if (String.IsNullOrEmpty(parent)) throw new ArgumentException("目标文件夹没有有效父目录。");
            return Path.Combine(parent, ".winunlink-" + kind + "-" + Guid.NewGuid().ToString("N"));
        }

        private static void CompareTrees(DirectorySnapshot expected, DirectorySnapshot actual)
        {
            if (!SameItem(expected.Root, actual.Root) || expected.Entries.Count != actual.Entries.Count)
                throw new IOException("文件夹身份或内容列表已经变化，请重新查询后确认。");
            Dictionary<string, ItemSnapshot> actualEntries = new Dictionary<string, ItemSnapshot>(StringComparer.Ordinal);
            foreach (ItemSnapshot entry in actual.Entries) actualEntries.Add(Relative(actual.Root, entry), entry);
            foreach (ItemSnapshot entry in expected.Entries)
            {
                ItemSnapshot current;
                if (!actualEntries.TryGetValue(Relative(expected.Root, entry), out current) || !SameItem(entry, current))
                    throw new IOException("文件夹内项目身份或状态已经变化，请重新查询：" + entry.Path);
            }
        }

        private static bool SameItem(ItemSnapshot expected, ItemSnapshot actual)
        {
            return String.Equals(expected.Identity, actual.Identity, StringComparison.Ordinal) &&
                expected.IsDirectory == actual.IsDirectory && expected.IsReadOnly == actual.IsReadOnly &&
                (expected.IsDirectory || expected.Length == actual.Length);
        }

        private static int NativeCode(Exception error)
        {
            EntryCreationException creation = error as EntryCreationException;
            if (creation != null) return creation.ErrorCode;
            DirectoryPreparationException preparation = error as DirectoryPreparationException;
            if (preparation != null) return preparation.ErrorCode;
            Win32Exception native = error as Win32Exception;
            return native == null ? 0 : native.NativeErrorCode;
        }

        private static bool Expected(Exception error)
        {
            return error is Win32Exception || error is IOException || error is ArgumentException ||
                error is UnauthorizedAccessException || error is SecurityException || error is NotSupportedException ||
                error is OverflowException;
        }

        private static FileOperationResult Completed(string message, string retained)
        {
            return new FileOperationResult { Status = OperationStatus.Completed, Message = message, RetainedPath = retained };
        }

        private static FileOperationResult Failed(string message, int error)
        {
            return new FileOperationResult { Status = OperationStatus.Failed, Message = message, ErrorCode = error };
        }

        private static FileOperationResult Partial(string message, string retained, int error)
        {
            return new FileOperationResult { Status = OperationStatus.Partial, Message = message, RetainedPath = retained, ErrorCode = error };
        }

        private sealed class OwnedDirectory
        {
            internal ItemSnapshot Root;
            internal readonly List<ItemSnapshot> Created = new List<ItemSnapshot>();
            internal bool Preserve;
            internal OwnedDirectory(ItemSnapshot root) { Root = root; }
            internal DirectorySnapshot Snapshot() { return new DirectorySnapshot(Root, Created); }
        }

        private sealed class DirectoryPreparationException : IOException
        {
            internal readonly string RetainedPath;
            internal readonly int ErrorCode;
            internal DirectoryPreparationException(Exception inner, string retained, string message, int error)
                : base(message, inner) { RetainedPath = retained; ErrorCode = error; }
        }
    }
}
