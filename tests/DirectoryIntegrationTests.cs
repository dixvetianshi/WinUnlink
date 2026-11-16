using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using WinUnlink;

namespace WinUnlinkTests
{
    public static class DirectoryIntegrationTests
    {
        private const string OldContent = "original folder payload / 原内容";
        private const string NewContent = "replacement folder payload / 新内容";
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private static string casesRoot;
        private static string testRoot;
        private static string testId;
        private static int passed;
        private static int failed;

        public static int Main(string[] args)
        {
            Console.OutputEncoding = Utf8;
            if (args.Length != 0 && args[0] == "--hold") return HoldFiles(args);
            if (args.Length != 0) return 2;
            casesRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "cases"));
            testId = Guid.NewGuid().ToString("N");
            testRoot = Path.Combine(casesRoot, "directories-" + testId);
            Directory.CreateDirectory(testRoot);
            Console.WriteLine("Windows: " + Environment.OSVersion.VersionString + "; pointer size: " + IntPtr.Size +
                "; owned directory cases: " + testRoot);
            try
            {
                Run("nested inventory and complete deletion", NestedDelete);
                Run("empty directory deletion", EmptyDelete);
                Run("readonly nested files and directories", ReadonlyDelete);
                Run("whole replacement preserves source and removes old-only entries", WholeReplacement);
                Run("empty source replaces a nonempty directory", EmptyReplacement);
                Run("changed root identity rejects deletion and replacement", ChangedRoot);
                Run("changed descendant identity rejects deletion and replacement", ChangedDescendant);
                Run("same and overlapping source directories are rejected", OverlappingSources);
                Run("file source is rejected before directory replacement", FileSource);
                Run("locked descendant reports actual deletion coverage and allows retry", LockedDelete);
                Run("locked replacement preserves source and reports any retained old tree", LockedReplacement);
                Run("nested Restart Manager deduplicates a holder and stop-and-delete completes", StopDirectory);
                Run("junction roots and descendants do not traverse external data", Junctions);
                Run("canonical parent-junction aliases cannot bypass source overlap", ParentJunctionAliases);
                Run("long Chinese directory replacement and recursive deletion", LongDirectories);
                Run("recycle preflight refuses a stale directory without shell deletion", StaleDirectoryRecycleGuard);
            }
            finally
            {
                try
                {
                    string error = LockInspector.RetryCleanup();
                    Check(error == null, "Restart Manager cleanup failed: " + error);
                    Cleanup();
                }
                catch (Exception error)
                {
                    failed++;
                    Console.Error.WriteLine("FAIL owned directory fixture cleanup: " + error.Message);
                    Console.Error.WriteLine("Retained owned directory: " + testRoot);
                }
            }
            Console.WriteLine("RESULT directory tests passed=" + passed + " failed=" + failed);
            return failed == 0 ? 0 : 1;
        }

        private static void Run(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
        }

        private static void NestedDelete()
        {
            string target = MakeDirectory("删除 中文 文件夹");
            MakeDirectory(target, "nested");
            MakeDirectory(target, "empty");
            MakeFile(target, "root.txt", OldContent);
            MakeFile(target, "nested/child.txt", NewContent);
            DirectorySnapshot tree = DirectoryOperations.Inspect(target);
            Check(tree.Root.IsDirectory && tree.FileCount == 2 && tree.DirectoryCount == 3,
                "Directory inventory did not preserve files and the empty subdirectory.");
            Check(tree.TotalBytes == Utf8.GetByteCount(OldContent) + Utf8.GetByteCount(NewContent),
                "Directory inventory did not preserve the completed byte count.");
            Check(tree.Entries.Count == 4 && tree.Entries.All(item => item.Path != tree.Root.Path),
                "Directory descendants duplicated or omitted the root.");
            Completed(DirectoryOperations.Delete(tree));
            Check(!Directory.Exists(target), "A completed recursive deletion left its root directory.");
        }

        private static void EmptyDelete()
        {
            string target = MakeDirectory("empty-delete");
            TargetSnapshot selected = TargetOperations.Inspect(target);
            Check(selected.Root.IsDirectory && selected.Directory != null, "Target selection lost the directory kind.");
            Completed(TargetOperations.Delete(selected, DeleteMode.Permanent));
            Check(!Directory.Exists(target), "An empty target directory remained after deletion.");
        }

        private static void ReadonlyDelete()
        {
            string target = MakeDirectory("readonly-tree");
            string child = MakeDirectory(target, "child");
            string file = MakeFile(child, "readonly.txt", OldContent);
            File.SetAttributes(file, FileAttributes.ReadOnly);
            File.SetAttributes(child, FileAttributes.Directory | FileAttributes.ReadOnly);
            File.SetAttributes(target, FileAttributes.Directory | FileAttributes.ReadOnly);
            Completed(DirectoryOperations.Delete(DirectoryOperations.Inspect(target)));
            Check(!Directory.Exists(target), "Readonly descendants prevented the advertised directory deletion.");
        }

        private static void WholeReplacement()
        {
            string target = MakeDirectory("replace-target");
            MakeFile(target, "old-only.txt", OldContent);
            MakeFile(target, "same.txt", OldContent);
            MakeDirectory(target, "old-empty");
            string source = MakeDirectory("replacement 中文 来源");
            MakeFile(source, "same.txt", NewContent);
            MakeFile(source, "nested/inside.txt", NewContent);
            MakeDirectory(source, "empty-preserved");
            string beforeSource = Manifest(source);
            TargetSnapshot before = TargetOperations.Inspect(target);
            Completed(TargetOperations.Replace(before, source));
            Check(Manifest(source) == beforeSource && Manifest(target) == beforeSource,
                "Whole directory replacement changed the source or merged old-only target entries.");
            Check(!File.Exists(Path.Combine(target, "old-only.txt")) && !Directory.Exists(Path.Combine(target, "old-empty")),
                "Whole replacement left entries that only existed in the old destination.");
            Check(TargetOperations.Inspect(target).Root.Identity != before.Root.Identity,
                "Whole replacement did not publish the staged directory object.");
        }

        private static void EmptyReplacement()
        {
            string target = MakeDirectory("empty-source-target");
            MakeFile(target, "old/file.txt", OldContent);
            string source = MakeDirectory("empty-source");
            Completed(DirectoryOperations.Replace(DirectoryOperations.Inspect(target), source));
            Check(Directory.Exists(target) && Directory.GetFileSystemEntries(target).Length == 0 &&
                Directory.Exists(source) && Directory.GetFileSystemEntries(source).Length == 0,
                "An empty source failed to replace the old destination with an empty directory.");
        }

        private static void ChangedRoot()
        {
            string target = MakeDirectory("changed-root-target");
            MakeFile(target, "old.txt", OldContent);
            string source = MakeDirectory("changed-root-source");
            MakeFile(source, "new.txt", NewContent);
            DirectorySnapshot original = DirectoryOperations.Inspect(target);
            string moved = Path.Combine(testRoot, "changed-root-original");
            Directory.Move(target, moved);
            Directory.CreateDirectory(target);
            MakeFile(target, "independent.txt", "independent current root");
            string sourceBefore = Manifest(source);
            string currentBefore = Manifest(target);
            Check(DirectoryOperations.Inspect(target).Root.Identity != original.Root.Identity,
                "The changed-root fixture did not establish a distinct directory identity.");
            Failed(DirectoryOperations.Delete(original));
            Failed(DirectoryOperations.Replace(original, source));
            Check(Manifest(target) == currentBefore && Manifest(source) == sourceBefore &&
                File.ReadAllText(Path.Combine(moved, "old.txt"), Utf8) == OldContent,
                "A stale root snapshot changed the replacement root, original tree, or source.");
        }

        private static void ChangedDescendant()
        {
            string target = MakeDirectory("changed-descendant-target");
            string file = MakeFile(target, "child.txt", OldContent);
            string source = MakeDirectory("changed-descendant-source");
            MakeFile(source, "new.txt", NewContent);
            DirectorySnapshot original = DirectoryOperations.Inspect(target);
            string moved = Path.Combine(testRoot, "changed-descendant-original.txt");
            File.Move(file, moved);
            File.WriteAllText(file, "independent child", Utf8);
            string currentBefore = Manifest(target);
            string sourceBefore = Manifest(source);
            Failed(DirectoryOperations.Delete(original));
            Failed(DirectoryOperations.Replace(original, source));
            Check(Manifest(target) == currentBefore && Manifest(source) == sourceBefore &&
                File.ReadAllText(moved, Utf8) == OldContent,
                "A stale descendant snapshot changed the current tree or its source.");
        }

        private static void OverlappingSources()
        {
            string ancestor = MakeDirectory("overlap-ancestor");
            string target = MakeDirectory(ancestor, "target");
            MakeFile(target, "root.txt", OldContent);
            string inside = MakeDirectory(target, "source-inside");
            MakeFile(inside, "nested.txt", NewContent);
            string before = Manifest(ancestor);
            Failed(DirectoryOperations.Replace(DirectoryOperations.Inspect(target), target));
            Failed(DirectoryOperations.Replace(DirectoryOperations.Inspect(target), inside));
            Failed(DirectoryOperations.Replace(DirectoryOperations.Inspect(target), ancestor));
            Check(Manifest(ancestor) == before, "Same or mutually nested source/target directories were modified.");
        }

        private static void FileSource()
        {
            string target = MakeDirectory("file-source-target");
            MakeFile(target, "old.txt", OldContent);
            string source = MakeFile(testRoot, "not-a-directory.txt", NewContent);
            string before = Manifest(target);
            Failed(DirectoryOperations.Replace(DirectoryOperations.Inspect(target), source));
            Check(Manifest(target) == before && File.ReadAllText(source, Utf8) == NewContent,
                "A file replacement source changed a directory target before being rejected.");
        }

        private static void LockedDelete()
        {
            string target = MakeDirectory("locked-delete-target");
            MakeFile(target, "first-unlocked.txt", NewContent);
            string locked = MakeFile(target, "nested/locked.txt", OldContent);
            MakeDirectory(target, "empty");
            DirectorySnapshot tree = DirectoryOperations.Inspect(target);
            using (FixtureChild child = FixtureChild.Start(locked))
            {
                FileOperationResult result = DirectoryOperations.Delete(tree);
                Check(result.Status == OperationStatus.Failed || result.Status == OperationStatus.Partial,
                    "A locked descendant was reported as a complete recursive deletion: " + Describe(result));
                bool changed = tree.Entries.Any(item => item.IsDirectory ? !Directory.Exists(item.Path) : !File.Exists(item.Path));
                Check(result.Status == (changed ? OperationStatus.Partial : OperationStatus.Failed),
                    "Deletion status did not reflect the actually removed descendants: " + Describe(result));
                Check(Directory.Exists(target) && File.Exists(locked) && child.IsAlive && child.ReadOriginal() == OldContent,
                    "Failed/partial recursive deletion removed the locked child or terminated its holder.");
                Check(!String.IsNullOrWhiteSpace(result.Message), "Locked recursive deletion omitted the failure reason.");
                if (result.Status == OperationStatus.Partial)
                    Check(!String.IsNullOrWhiteSpace(result.RetainedPath), "Partial recursive deletion omitted its retained path.");
                Console.WriteLine("OBS locked directory delete: " + Describe(result));
            }
            Completed(DirectoryOperations.Delete(DirectoryOperations.Inspect(target)));
            Check(!Directory.Exists(target), "Recursive deletion did not complete after the owned holder released its file.");
        }

        private static void LockedReplacement()
        {
            string target = MakeDirectory("locked-replace-target");
            string locked = MakeFile(target, "nested/locked.txt", OldContent);
            MakeFile(target, "old-only.txt", OldContent);
            string source = MakeDirectory("locked-replace-source");
            MakeFile(source, "new.txt", NewContent);
            MakeDirectory(source, "empty");
            string before = Manifest(target);
            string sourceBefore = Manifest(source);
            DirectorySnapshot original = DirectoryOperations.Inspect(target);
            string retained = null;
            using (FixtureChild child = FixtureChild.Start(locked))
            {
                FileOperationResult result = DirectoryOperations.Replace(original, source);
                Check(result.Status == OperationStatus.Failed || result.Status == OperationStatus.Partial,
                    "A locked old descendant was reported as fully cleaned replacement: " + Describe(result));
                Check(child.IsAlive && child.ReadOriginal() == OldContent && Manifest(source) == sourceBefore,
                    "Locked replacement terminated its holder, damaged old contents, or changed the source.");
                if (result.Status == OperationStatus.Failed)
                {
                    // The fixture intentionally denies every competing data
                    // read. Check identities now, then read the complete tree
                    // after its owned holder exits; do not weaken its sharing.
                    DirectoryOperations.Verify(original);
                    Check(Directory.Exists(target) && File.Exists(locked),
                        "Failed replacement removed the original directory or locked pathname.");
                }
                else
                {
                    Check(Manifest(target) == sourceBefore, "A partially cleaned replacement did not publish the complete source tree.");
                    retained = result.RetainedPath;
                    Check(!String.IsNullOrWhiteSpace(retained) && Directory.Exists(retained) &&
                        File.Exists(Path.Combine(retained, "nested/locked.txt")),
                        "Partial replacement did not retain the locked old tree at its reported recovery path.");
                }
                Console.WriteLine("OBS locked directory replacement: " + Describe(result));
            }
            if (retained != null) Completed(DirectoryOperations.Delete(DirectoryOperations.Inspect(retained)));
            else
            {
                Check(Manifest(target) == before, "Failed replacement did not preserve the original destination contents.");
                Completed(DirectoryOperations.Replace(DirectoryOperations.Inspect(target), source));
            }
            Check(Manifest(target) == sourceBefore && Manifest(source) == sourceBefore,
                "The replacement did not complete or preserve its source after releasing the owned holder.");
        }

        private static void StopDirectory()
        {
            string target = MakeDirectory("stop-directory-target");
            string first = MakeFile(target, "one/locked.txt", OldContent);
            string second = MakeFile(target, "two/locked.txt", OldContent);
            using (FixtureChild child = FixtureChild.Start(first, second))
            {
                TargetSnapshot selected = TargetOperations.Inspect(target);
                LockScanResult scan = null;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    scan = TargetOperations.Scan(selected);
                    Check(scan.IsComplete, "Recursive Restart Manager query failed: " + scan.Error);
                    if (scan.Processes.Any(process => process.ProcessId == child.Id)) break;
                    Thread.Sleep(100);
                }
                List<LockingProcess> matching = scan.Processes.Where(process => process.ProcessId == child.Id).ToList();
                Check(matching.Count == 1 && matching[0].StartTimeFileTime == child.StartTimeFileTime && matching[0].CanTerminate,
                    "Recursive resource registration omitted or duplicated the owned holder's exact process identity.");
                StopAndDeleteResult result = StopAndDelete.Execute(selected, matching, DeleteMode.Permanent);
                Completed(result.FileResult);
                Check(result.Processes.Count == 1 && result.Processes[0].Stopped && child.WaitForExit(3000) && !Directory.Exists(target),
                    "One stop-and-delete action failed to stop the nested holder and delete its entire directory.");
            }
        }

        private static void Junctions()
        {
            string outside = MakeDirectory("junction-external-data");
            MakeFile(outside, "keep.txt", OldContent);
            string outsideBefore = Manifest(outside);
            string target = MakeDirectory("junction-child-target");
            MakeFile(target, "ordinary.txt", OldContent);
            string childLink = Path.Combine(target, "linked-outside");
            CreateJunction(childLink, outside);
            string source = MakeDirectory("junction-clean-source");
            MakeFile(source, "new.txt", NewContent);
            InspectionRejected(delegate { DirectoryOperations.Inspect(target); });
            InspectionRejected(delegate { TargetOperations.Inspect(target); });
            Check(File.ReadAllText(Path.Combine(target, "ordinary.txt"), Utf8) == OldContent && Manifest(outside) == outsideBefore,
                "Inspecting a child junction mutated the ordinary or outside data.");

            string rootLink = Path.Combine(testRoot, "junction-root-target");
            CreateJunction(rootLink, outside);
            InspectionRejected(delegate { DirectoryOperations.Inspect(rootLink); });
            InspectionRejected(delegate { TargetOperations.Inspect(rootLink); });
            string cleanTarget = MakeDirectory("junction-source-target");
            MakeFile(cleanTarget, "keep.txt", OldContent);
            string cleanBefore = Manifest(cleanTarget);
            Failed(DirectoryOperations.Replace(DirectoryOperations.Inspect(cleanTarget), rootLink));
            Check(Manifest(cleanTarget) == cleanBefore && Manifest(outside) == outsideBefore && Manifest(source) != String.Empty,
                "A junction replacement source changed the destination or outside data.");
        }

        private static void ParentJunctionAliases()
        {
            string realParent = MakeDirectory("canonical-real-parent");
            MakeFile(realParent, "outside-target.txt", NewContent);
            string realTarget = MakeDirectory(realParent, "subtarget");
            MakeFile(realTarget, "keep.txt", OldContent);
            string aliasParent = Path.Combine(testRoot, "canonical-parent-alias");
            CreateJunction(aliasParent, realParent);
            string aliasTarget = Path.Combine(aliasParent, "subtarget");
            string before = Manifest(realParent);
            TargetSnapshot selected = TargetOperations.Inspect(aliasTarget);
            Check(selected.Root.IsDirectory &&
                String.Equals(selected.Root.CanonicalPath, Path.GetFullPath(realTarget), StringComparison.OrdinalIgnoreCase),
                "The shared item boundary did not resolve the directory behind a parent junction.");
            Failed(DirectoryOperations.Replace(selected.Directory, realParent));
            Failed(DirectoryOperations.Replace(DirectoryOperations.Inspect(realTarget), aliasTarget));
            Failed(DirectoryOperations.Replace(DirectoryOperations.Inspect(realParent), aliasTarget));
            Check(Manifest(realParent) == before && Directory.Exists(aliasTarget),
                "A parent-junction alias bypassed same-directory or nested-source rejection.");
        }

        private static void LongDirectories()
        {
            string parent = MakeDirectory("long-directory-boundary");
            for (int index = 0; index < 5; index++)
                parent = MakeDirectory(parent, "层级_" + new string('目', 38) + "_" + index);
            string target = MakeDirectory(parent, "中文 空格 目标文件夹");
            MakeFile(target, "old-only.txt", OldContent);
            string source = MakeDirectory(parent, "中文 空格 来源文件夹");
            MakeFile(source, "嵌套 文件夹/new.txt", NewContent);
            MakeDirectory(source, "空 文件夹");
            Check(target.Length > 260 && source.Length > 260,
                "The long-directory fixture did not cross the legacy MAX_PATH boundary.");
            string sourceBefore = Manifest(source);
            Completed(DirectoryOperations.Replace(DirectoryOperations.Inspect(target), source));
            Check(Manifest(target) == sourceBefore && Manifest(source) == sourceBefore,
                "Long-directory replacement failed to preserve the source or publish its whole tree.");
            Completed(DirectoryOperations.Delete(DirectoryOperations.Inspect(target)));
            Check(!Directory.Exists(target) && Manifest(source) == sourceBefore,
                "Long-directory recursive deletion failed or changed the source.");
        }

        private static void StaleDirectoryRecycleGuard()
        {
            string target = MakeDirectory("recycle-stale-target");
            MakeFile(target, "old.txt", OldContent);
            string originalBefore = Manifest(target);
            TargetSnapshot stale = TargetOperations.Inspect(target);
            string moved = Path.Combine(testRoot, "recycle-stale-original");
            Directory.Move(target, moved);
            Directory.CreateDirectory(target);
            MakeFile(target, "current.txt", NewContent);
            string currentBefore = Manifest(target);
            Check(TargetOperations.Inspect(target).Root.Identity != stale.Root.Identity,
                "The recycle refusal fixture did not establish a different target identity.");
            // This is deliberately the only default-mode delete call in this
            // suite. The stale identity must fail before a shell operation;
            // valid fixture deletions always select DeleteMode.Permanent.
            Failed(TargetOperations.Delete(stale));
            Check(Directory.Exists(target) && Directory.Exists(moved) &&
                Manifest(target) == currentBefore && Manifest(moved) == originalBefore,
                "Recycle preflight changed the current directory or its moved original.");
        }

        private static string MakeDirectory(string name) { return MakeDirectory(testRoot, name); }
        private static string MakeDirectory(string parent, string name)
        {
            string path = Path.Combine(parent, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(path); return path;
        }
        private static string MakeFile(string parent, string name, string text)
        {
            string path = Path.Combine(parent, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, Utf8); return path;
        }
        private static string Manifest(string root)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            List<string> rows = new List<string>();
            foreach (string directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
                rows.Add("D " + Path.GetFullPath(directory).Substring(prefix.Length).Replace('\\', '/'));
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                rows.Add("F " + Path.GetFullPath(file).Substring(prefix.Length).Replace('\\', '/') + " " +
                    Convert.ToBase64String(File.ReadAllBytes(file)));
            rows.Sort(StringComparer.Ordinal); return String.Join("\n", rows);
        }
        private static void Completed(FileOperationResult result)
        {
            Check(result != null && result.Status == OperationStatus.Completed, "Expected Completed: " + Describe(result));
        }
        private static void Failed(FileOperationResult result)
        {
            Check(result != null && result.Status == OperationStatus.Failed, "Expected Failed without target mutation: " + Describe(result));
            Check(!String.IsNullOrWhiteSpace(result.Message), "A refused directory operation omitted its reason.");
        }
        private static string Describe(FileOperationResult result)
        {
            return result == null ? "null" : result.Status + " / Win32 " + result.ErrorCode + " / " + result.Message +
                " / retained=" + result.RetainedPath;
        }
        private static void InspectionRejected(Action action)
        {
            try { action(); }
            catch (ArgumentException error) { Check(!String.IsNullOrWhiteSpace(error.Message), "Input refusal omitted its reason."); return; }
            catch (IOException error) { Check(!String.IsNullOrWhiteSpace(error.Message), "Input refusal omitted its reason."); return; }
            throw new InvalidOperationException("A root or descendant junction was accepted as an ordinary directory tree.");
        }
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        private static int HoldFiles(string[] args)
        {
            if (args.Length != 2 && args.Length != 3) return 2;
            List<FileStream> streams = new List<FileStream>();
            try
            {
                for (int index = 1; index < args.Length; index++)
                    streams.Add(new FileStream(args[index], FileMode.Open, FileAccess.Read, FileShare.None));
                Console.WriteLine("READY|" + Convert.ToBase64String(Utf8.GetBytes(OldContent))); Console.Out.Flush();
                Stopwatch life = Stopwatch.StartNew();
                for (;;)
                {
                    int remaining = 120000 - (int)life.ElapsedMilliseconds;
                    if (remaining <= 0) return 3;
                    Task<string> input = Task.Run(delegate { return Console.In.ReadLine(); });
                    if (!input.Wait(remaining)) return 3;
                    string command = input.Result;
                    if (command == null || command == "exit") return 0;
                    if (command != "read") return 2;
                    FileStream file = streams[0]; file.Position = 0;
                    byte[] bytes = new byte[checked((int)file.Length)];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = file.Read(bytes, offset, bytes.Length - offset);
                        if (read == 0) throw new IOException("Owned held file was truncated."); offset += read;
                    }
                    Console.WriteLine("READ|" + Convert.ToBase64String(bytes)); Console.Out.Flush();
                }
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { foreach (FileStream stream in streams) stream.Dispose(); }
        }

        private sealed class FixtureChild : IDisposable
        {
            private readonly Process process;
            private readonly IntPtr pinnedHandle;
            private FixtureChild(Process process)
            {
                this.process = process; pinnedHandle = process.Handle; StartTimeFileTime = process.StartTime.ToFileTimeUtc();
            }
            public int Id { get { return process.Id; } }
            public long StartTimeFileTime { get; private set; }
            public bool IsAlive { get { return !process.HasExited; } }
            public static FixtureChild Start(params string[] paths)
            {
                ProcessStartInfo start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                    "--hold " + String.Join(" ", paths.Select(path => "\"" + path + "\"")));
                start.UseShellExecute = false; start.CreateNoWindow = true;
                start.RedirectStandardInput = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
                start.StandardOutputEncoding = Utf8; start.StandardErrorEncoding = Utf8;
                Process process = Process.Start(start); Check(process != null, "Could not start owned folder holder.");
                FixtureChild child = new FixtureChild(process);
                try
                {
                    Check(child.ReadLine() == "READY|" + Convert.ToBase64String(Utf8.GetBytes(OldContent)),
                        "The owned directory fixture did not acquire all of its file handles."); return child;
                }
                catch { child.Dispose(); throw; }
            }
            public string ReadOriginal()
            {
                Check(IsAlive, "The owned folder holder exited unexpectedly.");
                process.StandardInput.WriteLine("read"); process.StandardInput.Flush();
                string line = ReadLine(); Check(line != null && line.StartsWith("READ|", StringComparison.Ordinal), "Missing held file contents.");
                return Utf8.GetString(Convert.FromBase64String(line.Substring(5)));
            }
            public bool WaitForExit(int milliseconds) { return process.WaitForExit(milliseconds); }
            private string ReadLine()
            {
                Task<string> line = process.StandardOutput.ReadLineAsync();
                Check(line.Wait(5000), "The owned folder holder did not respond within five seconds.");
                string value = line.Result;
                if (value == null && process.HasExited)
                    throw new InvalidOperationException("Owned holder exited with " + process.ExitCode + ": " + process.StandardError.ReadToEnd());
                return value;
            }
            public void Dispose()
            {
                try
                {
                    if (!process.HasExited)
                    {
                        try { process.StandardInput.WriteLine("exit"); process.StandardInput.Flush(); } catch (IOException) { }
                        if (!process.WaitForExit(3000))
                        {
                            Check(pinnedHandle != IntPtr.Zero && process.StartTime.ToFileTimeUtc() == StartTimeFileTime,
                                "Owned holder identity changed during cleanup.");
                            process.Kill(); Check(process.WaitForExit(3000), "Owned folder holder did not exit during cleanup.");
                        }
                    }
                }
                finally { process.Dispose(); }
            }
        }

        private static void CreateJunction(string junction, string target)
        {
            Directory.CreateDirectory(junction);
            byte[] substitute = Encoding.Unicode.GetBytes("\\??\\" + Path.GetFullPath(target));
            byte[] display = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
            int pathBytes = checked(substitute.Length + 2 + display.Length + 2);
            byte[] buffer = new byte[checked(16 + pathBytes)];
            Put(buffer, 0, 0xA0000003U);
            Put(buffer, 4, checked((ushort)(8 + pathBytes)));
            Put(buffer, 8, (ushort)0);
            Put(buffer, 10, checked((ushort)substitute.Length));
            Put(buffer, 12, checked((ushort)(substitute.Length + 2)));
            Put(buffer, 14, checked((ushort)display.Length));
            Buffer.BlockCopy(substitute, 0, buffer, 16, substitute.Length);
            Buffer.BlockCopy(display, 0, buffer, 16 + substitute.Length + 2, display.Length);
            using (SafeFileHandle handle = CreateFile(junction, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the owned junction fixture.");
                uint returned;
                if (!DeviceIoControl(handle, 0x000900A4, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out returned, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the owned junction fixture.");
            }
            Check((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0, "The owned junction fixture was not a reparse point.");
        }
        private static void Put(byte[] bytes, int offset, uint value) { Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 4); }
        private static void Put(byte[] bytes, int offset, ushort value) { Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 2); }

        private static void Cleanup()
        {
            string expected = Path.GetFullPath(Path.Combine(casesRoot, "directories-" + testId));
            Guid id;
            Check(Path.GetFullPath(testRoot) == expected && Guid.TryParseExact(testId, "N", out id), "Refused unowned directory case cleanup.");
            RemoveOwnedPath(expected, expected);
        }
        private static void RemoveOwnedPath(string path, string root)
        {
            string resolved = Path.GetFullPath(path);
            Check(resolved == root || resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Refused cleanup outside the owned directory case root.");
            FileAttributes attributes = File.GetAttributes(resolved);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(resolved, false);
                    return;
                }
                foreach (string child in Directory.GetFileSystemEntries(resolved)) RemoveOwnedPath(child, root);
                File.SetAttributes(resolved, FileAttributes.Directory);
                Directory.Delete(resolved, false);
            }
            else { File.SetAttributes(resolved, FileAttributes.Normal); File.Delete(resolved); }
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, uint inputLength,
            IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);
    }
}
