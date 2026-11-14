using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinUnlink;

namespace WinUnlinkTests
{
    public static class IntegrationTests
    {
        private const string OldContent = "original payload / 原内容";
        private const string NewContent = "replacement payload / 新内容";
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private static string casesRoot;
        private static string testCaseId;
        private static string testRoot;
        private static int passed;
        private static int failed;
        private static int limits;

        public static int Main(string[] args)
        {
            Console.OutputEncoding = Utf8;
            if (args.Length > 0 && args[0] == "--hold-file")
                return HoldFile(args);
            if (args.Length != 0)
            {
                Console.Error.WriteLine("Unknown test arguments.");
                return 2;
            }

            casesRoot = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "cases"));
            testCaseId = Guid.NewGuid().ToString("N");
            testRoot = Path.Combine(casesRoot, testCaseId);
            Directory.CreateDirectory(testRoot);
            Console.WriteLine("Windows: " + Environment.OSVersion.VersionString +
                "; pointer size: " + IntPtr.Size + "; test root: " + testRoot);
            try
            {
                Run("ordinary deletion", OrdinaryDelete);
                Run("ordinary replacement and source preservation", OrdinaryReplace);
                Run("Chinese and spaced file paths", ChinesePaths);
                Run("share-delete deletion preserves the old live handle", SharedDelete);
                Run("share-delete replacement preserves the old live handle", SharedReplace);
                Run("deny-delete deletion and explicit process fallback", delegate { DeniedOperation(false); });
                Run("deny-delete replacement and explicit process fallback", delegate { DeniedOperation(true); });
                Run("one action stops an exclusive zip fixture and deletes it", StopAndDeleteExclusive);
                Run("stop-and-delete rejects a changed target before stopping", StopAndDeleteChangedTarget);
                Run("stop-and-delete records mixed process outcomes without deleting", StopAndDeleteMixed);
                Run("stop-and-delete with no occupying processes", StopAndDeleteEmpty);
                Run("PID creation mismatch is rejected", IdentityMismatch);
                Run("self termination is rejected", SelfTermination);
                Run("directories and ADS are rejected", InvalidInputs);
                Run("same-file and hard-link replacement are rejected", SameFileReplacement);
                Run("changed destination identity is rejected", ChangedTargetIdentity);
                Run("readonly deletion", ReadOnlyDelete);
                Run("readonly replacement preserves source", ReadOnlyReplace);
                Run("failed replacement preserves readonly target", FailedReadOnlyReplace);
            }
            finally
            {
                try
                {
                    string cleanupError = LockInspector.RetryCleanup();
                    if (cleanupError != null)
                        throw new InvalidOperationException(cleanupError);
                    CleanupTestRoot();
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine("FAIL owned fixture cleanup: " + ex.Message);
                    Console.WriteLine("Retained test directory: " + testRoot);
                }
            }
            Console.WriteLine("TOTAL: " + passed + " passed, " + failed +
                " failed, " + limits + " observed platform limits (fallbacks verified separately).");
            return failed == 0 ? 0 : 1;
        }

        private static void Run(string name, Action action)
        {
            try
            {
                action();
                passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("FAIL " + name + ": " + ex.Message);
            }
        }

        private static void OrdinaryDelete()
        {
            string path = MakeFile("ordinary-delete.txt", OldContent);
            Completed(FileOperations.Delete(FileOperations.Inspect(path)));
            Assert(!File.Exists(path), "A completed deletion left the pathname present.");
        }

        private static void OrdinaryReplace()
        {
            string target = MakeFile("ordinary-replace.txt", OldContent);
            string source = MakeFile("ordinary-source.txt", NewContent);
            FileSnapshot before = FileOperations.Inspect(target);
            Completed(FileOperations.Replace(before, source));
            Assert(Read(target) == NewContent, "The destination does not contain the replacement bytes.");
            Assert(Read(source) == NewContent, "The replacement source was modified or removed.");
            Assert(FileOperations.Inspect(target).Identity != before.Identity,
                "Replacement reused the old file object rather than publishing a new file.");
        }

        private static void ChinesePaths()
        {
            string target = MakeFile("中文 空格 目标.txt", OldContent);
            string source = MakeFile("中文 空格 来源.txt", NewContent);
            Completed(FileOperations.Replace(FileOperations.Inspect(target), source));
            Assert(Read(target) == NewContent && Read(source) == NewContent,
                "Chinese/spaced paths did not preserve the selected files.");
            Completed(FileOperations.Delete(FileOperations.Inspect(target)));
            Assert(!File.Exists(target), "Chinese/spaced deletion did not remove the selected pathname.");
        }

        private static void SharedDelete()
        {
            string target = MakeFile("shared-delete.txt", OldContent);
            FileSnapshot before = FileOperations.Inspect(target);
            using (FixtureChild child = FixtureChild.Start(target, true))
            {
                Completed(FileOperations.Delete(before));
                Assert(!File.Exists(target), "A shared-delete handle kept the old pathname visible.");
                Assert(child.IsAlive, "Deletion terminated the occupying process.");
                Assert(child.ReadOriginal() == OldContent, "The surviving handle lost the original content.");
                File.WriteAllText(target, NewContent, Utf8);
                Assert(Read(target) == NewContent, "The deleted pathname could not be reused immediately.");
                Assert(child.ReadOriginal() == OldContent, "Pathname reuse changed the old handle's content.");
            }
        }

        private static void SharedReplace()
        {
            string target = MakeFile("shared-replace.txt", OldContent);
            string source = MakeFile("shared-source.txt", NewContent);
            FileSnapshot before = FileOperations.Inspect(target);
            using (FixtureChild child = FixtureChild.Start(target, true))
            {
                Completed(FileOperations.Replace(before, source));
                Assert(child.IsAlive, "Replacement terminated the occupying process.");
                Assert(child.ReadOriginal() == OldContent, "The old handle did not retain the original bytes.");
                Assert(Read(target) == NewContent, "A new open of the target did not see the new bytes.");
                Assert(Read(source) == NewContent, "Replacement changed or deleted its source.");
            }
        }

        private static void DeniedOperation(bool replace)
        {
            string suffix = replace ? "replace" : "delete";
            string target = MakeFile("denied-" + suffix + ".txt", OldContent);
            string source = MakeFile("denied-source-" + suffix + ".txt", NewContent);
            FileSnapshot before = FileOperations.Inspect(target);
            using (FixtureChild child = FixtureChild.Start(target, false))
            {
                FileOperationResult direct = replace
                    ? FileOperations.Replace(before, source) : FileOperations.Delete(before);
                if (direct.Status == OperationStatus.Completed)
                {
                    Assert(child.IsAlive && child.ReadOriginal() == OldContent,
                        "Direct locked operation did not preserve the old live handle.");
                    if (replace)
                        Assert(Read(target) == NewContent && Read(source) == NewContent,
                            "Direct locked replacement did not publish the requested bytes.");
                    else
                        Assert(!File.Exists(target), "Direct locked deletion left the pathname present.");
                    return;
                }

                Failed(direct);
                Assert(child.IsAlive && child.ReadOriginal() == OldContent,
                    "The refused operation damaged the original handle or terminated its process.");
                Assert(File.Exists(target), "The refused operation removed the original pathname.");
                Assert(FileOperations.Inspect(target).Identity == before.Identity,
                    "The refused operation changed the original file identity.");
                Assert(Read(source) == NewContent, "The refused operation changed the replacement source.");
                limits++;
                Console.WriteLine("LIMIT deny-delete " + suffix + ": Win32 " + direct.ErrorCode +
                    "; original preserved; testing the explicit process fallback.");

                LockingProcess selected = FindChild(target, child);
                Assert(selected.CanTerminate, "The ordinary fixture process was not eligible for explicit termination: " +
                    selected.TerminationRestriction);
                string terminationError = ProcessController.Terminate(selected);
                Assert(terminationError == null, "Explicit fixture termination failed: " + terminationError);
                Assert(child.WaitForExit(3000), "The selected fixture process remained alive after termination.");
                Assert(Read(target) == OldContent, "The failed first attempt changed the target content.");
                FileOperationResult retry = replace
                    ? FileOperations.Replace(before, source) : FileOperations.Delete(before);
                Completed(retry);
                if (replace)
                    Assert(Read(target) == NewContent && Read(source) == NewContent,
                        "Fallback replacement did not complete or preserve its source.");
                else
                    Assert(!File.Exists(target), "Fallback deletion did not remove the selected pathname.");
            }
        }

        private static void StopAndDeleteExclusive()
        {
            string target = MakeFile("stop-and-delete.zip", OldContent);
            FileSnapshot snapshot = FileOperations.Inspect(target);
            using (FixtureChild child = FixtureChild.Start(target, false))
            {
                FileOperationResult direct = FileOperations.Delete(snapshot);
                Failed(direct);
                Assert(direct.ErrorCode == 32 && File.Exists(target) && child.IsAlive,
                    "The exclusive fixture did not reproduce a sharing-violation deletion failure.");
                LockingProcess selected = FindChild(target, child);
                Assert(selected.CanTerminate, "The owned holder was not eligible for termination: " +
                    selected.TerminationRestriction);

                StopAndDeleteResult result = StopAndDelete.Execute(snapshot,
                    new List<LockingProcess> { selected });
                Assert(result != null, "Stop-and-delete did not return a result.");
                Completed(result.FileResult);
                Assert(result.Processes != null && result.Processes.Count == 1 &&
                    result.Processes[0].Process.ProcessId == child.Id && result.Processes[0].Stopped &&
                    result.Processes[0].Error == null, "The single-action result did not record the stopped holder.");
                Assert(child.WaitForExit(3000) && !File.Exists(target),
                    "One stop-and-delete action did not both stop the holder and remove the pathname.");
            }
        }

        private static void StopAndDeleteChangedTarget()
        {
            string target = MakeFile("stop-and-delete-changed.txt", OldContent);
            FileSnapshot snapshot = FileOperations.Inspect(target);
            using (FixtureChild child = FixtureChild.Start(target, true))
            {
                LockingProcess selected = FindChild(target, child);
                Assert(selected.CanTerminate, "The owned holder was not eligible for termination: " +
                    selected.TerminationRestriction);
                string movedOriginal = Path.Combine(testRoot, "stop-and-delete-original.txt");
                File.Move(target, movedOriginal);
                File.WriteAllText(target, "independent current target", Utf8);
                Assert(FileOperations.Inspect(target).Identity != snapshot.Identity,
                    "The changed-target fixture did not establish a different identity.");

                StopAndDeleteResult result = StopAndDelete.Execute(snapshot,
                    new List<LockingProcess> { selected });
                Assert(result != null && result.FileResult != null &&
                    result.FileResult.Status == OperationStatus.Failed,
                    "Stop-and-delete accepted an outdated target snapshot.");
                Assert(result.Processes != null, "Target refusal omitted the process outcomes collection.");
                foreach (ProcessStopResult process in result.Processes)
                    Assert(!process.Stopped, "A process was stopped before checking the destination identity.");
                Assert(child.IsAlive && child.ReadOriginal() == OldContent &&
                    Read(target) == "independent current target" && Read(movedOriginal) == OldContent,
                    "The stale-target refusal stopped the holder or changed either file.");
            }
        }

        private static void StopAndDeleteMixed()
        {
            string target = MakeFile("stop-and-delete-mixed.txt", OldContent);
            FileSnapshot snapshot = FileOperations.Inspect(target);
            using (FixtureChild first = FixtureChild.Start(target, "deny-delete-shared"))
            using (FixtureChild second = FixtureChild.Start(target, "deny-delete-shared"))
            {
                LockingProcess firstRecord = FindChild(target, first);
                LockingProcess secondRecord = FindChild(target, second);
                Assert(firstRecord.CanTerminate && secondRecord.CanTerminate,
                    "The ordinary holder fixtures were not eligible for termination.");
                LockingProcess wrongSecond = new LockingProcess
                {
                    ProcessId = secondRecord.ProcessId,
                    StartTimeFileTime = secondRecord.StartTimeFileTime + 1,
                    Name = secondRecord.Name,
                    ApplicationName = secondRecord.ApplicationName,
                    ImagePath = secondRecord.ImagePath,
                    ServiceName = secondRecord.ServiceName,
                    Kind = secondRecord.Kind,
                    Type = secondRecord.Type,
                    SessionId = secondRecord.SessionId,
                    Restartable = secondRecord.Restartable,
                    CanTerminate = true
                };

                StopAndDeleteResult result = StopAndDelete.Execute(snapshot,
                    new List<LockingProcess> { firstRecord, wrongSecond });
                Assert(result != null && result.FileResult != null &&
                    result.FileResult.Status == OperationStatus.Failed,
                    "Stop-and-delete attempted deletion after a process identity failure.");
                Assert(result.Processes != null && result.Processes.Count == 2,
                    "The mixed result did not record both attempted process outcomes.");
                Assert(result.Processes[0].Process.ProcessId == first.Id && result.Processes[0].Stopped &&
                    result.Processes[0].Error == null, "The mixed result lost the first successful stop.");
                Assert(result.Processes[1].Process.ProcessId == second.Id && !result.Processes[1].Stopped &&
                    !String.IsNullOrWhiteSpace(result.Processes[1].Error) &&
                    result.Processes[1].Error.IndexOf("身份", StringComparison.Ordinal) >= 0,
                    "The mixed result did not report the second identity refusal.");
                Assert(first.WaitForExit(3000) && second.IsAlive && second.ReadOriginal() == OldContent,
                    "The mixed action did not preserve the distinct stopped and surviving processes.");
                Assert(File.Exists(target) && FileOperations.Inspect(target).Identity == snapshot.Identity &&
                    Read(target) == OldContent, "The mixed failure removed or modified the original target.");
            }
        }

        private static void StopAndDeleteEmpty()
        {
            string target = MakeFile("stop-and-delete-empty.txt", OldContent);
            StopAndDeleteResult result = StopAndDelete.Execute(FileOperations.Inspect(target),
                new List<LockingProcess>());
            Assert(result != null, "Stop-and-delete did not return a result for an empty process selection.");
            Completed(result.FileResult);
            Assert(result.Processes != null && result.Processes.Count == 0 && !File.Exists(target),
                "An empty process selection did not directly delete the ordinary target.");
        }

        private static void IdentityMismatch()
        {
            string target = MakeFile("pid-identity.txt", OldContent);
            using (FixtureChild child = FixtureChild.Start(target, false))
            {
                LockingProcess actual = FindChild(target, child);
                LockingProcess wrongIdentity = new LockingProcess
                {
                    ProcessId = actual.ProcessId,
                    StartTimeFileTime = actual.StartTimeFileTime + 1,
                    Name = actual.Name,
                    ImagePath = actual.ImagePath,
                    Kind = actual.Kind,
                    ServiceName = actual.ServiceName
                };
                string error = ProcessController.Terminate(wrongIdentity);
                Assert(error != null && error.IndexOf("身份", StringComparison.Ordinal) >= 0,
                    "A mismatched creation time was not rejected as an identity failure.");
                Assert(child.IsAlive && child.ReadOriginal() == OldContent,
                    "Identity refusal terminated or changed the fixture process.");
            }
        }

        private static void SelfTermination()
        {
            using (Process current = Process.GetCurrentProcess())
            {
                string error = ProcessController.Terminate(new LockingProcess
                {
                    ProcessId = current.Id,
                    StartTimeFileTime = current.StartTime.ToFileTimeUtc(),
                    Kind = LockProcessKind.Console,
                    Name = current.ProcessName
                });
                Assert(error != null && error.IndexOf("自身", StringComparison.Ordinal) >= 0,
                    "The running utility did not explicitly reject its own PID.");
            }
        }

        private static void InvalidInputs()
        {
            string directory = Path.Combine(testRoot, "not-a-file");
            Directory.CreateDirectory(directory);
            InspectionRejected(directory);
            string ordinary = MakeFile("no-ads.txt", OldContent);
            InspectionRejected(ordinary + ":alternate");
            Assert(Directory.Exists(directory) && Read(ordinary) == OldContent,
                "Rejected input modified the existing file or directory.");
        }

        private static void SameFileReplacement()
        {
            string target = MakeFile("same-file.txt", OldContent);
            FileSnapshot snapshot = FileOperations.Inspect(target);
            Failed(FileOperations.Replace(snapshot, target));
            Assert(Read(target) == OldContent, "Same-path replacement changed the original content.");

            string alias = Path.Combine(testRoot, "same-file-hardlink.txt");
            if (!CreateHardLink(alias, target, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the owned hard-link fixture.");
            Assert(FileOperations.Inspect(alias).Identity == snapshot.Identity,
                "The hard-link fixture did not refer to the same file ID.");
            Failed(FileOperations.Replace(snapshot, alias));
            Assert(Read(target) == OldContent && Read(alias) == OldContent,
                "Hard-link alias replacement changed the shared original content.");
        }

        private static void ChangedTargetIdentity()
        {
            string target = MakeFile("changed-target.txt", OldContent);
            string source = MakeFile("changed-source.txt", NewContent);
            FileSnapshot oldSnapshot = FileOperations.Inspect(target);
            string originalMoved = Path.Combine(testRoot, "changed-original-moved.txt");
            File.Move(target, originalMoved);
            File.WriteAllText(target, "independent new target", Utf8);
            Assert(FileOperations.Inspect(target).Identity != oldSnapshot.Identity,
                "The replacement fixture did not have a distinct identity.");
            Failed(FileOperations.Delete(oldSnapshot));
            Failed(FileOperations.Replace(oldSnapshot, source));
            Assert(Read(target) == "independent new target" && Read(originalMoved) == OldContent &&
                Read(source) == NewContent, "An outdated snapshot modified another file.");
        }

        private static void ReadOnlyDelete()
        {
            string target = MakeFile("readonly-delete.txt", OldContent);
            File.SetAttributes(target, FileAttributes.ReadOnly);
            FileSnapshot snapshot = FileOperations.Inspect(target);
            Assert(snapshot.IsReadOnly, "Inspection lost the readonly attribute.");
            Completed(FileOperations.Delete(snapshot));
            Assert(!File.Exists(target), "Readonly deletion did not remove the pathname.");
        }

        private static void ReadOnlyReplace()
        {
            string target = MakeFile("readonly-replace.txt", OldContent);
            string source = MakeFile("readonly-source.txt", NewContent);
            File.SetAttributes(target, FileAttributes.ReadOnly);
            File.SetAttributes(source, FileAttributes.ReadOnly);
            Completed(FileOperations.Replace(FileOperations.Inspect(target), source));
            Assert(Read(target) == NewContent && Read(source) == NewContent,
                "Readonly replacement did not publish new bytes or preserve its source.");
            Assert((File.GetAttributes(source) & FileAttributes.ReadOnly) != 0,
                "Readonly replacement changed its source attributes.");
        }

        private static void FailedReadOnlyReplace()
        {
            string target = MakeFile("readonly-failed-target.txt", OldContent);
            File.SetAttributes(target, FileAttributes.ReadOnly);
            Failed(FileOperations.Replace(FileOperations.Inspect(target),
                Path.Combine(testRoot, "source-does-not-exist.txt")));
            Assert(Read(target) == OldContent && (File.GetAttributes(target) & FileAttributes.ReadOnly) != 0,
                "A refused replacement changed the original bytes or readonly attribute.");
        }

        private static LockingProcess FindChild(string path, FixtureChild child)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                LockScanResult result = LockInspector.Scan(path);
                Assert(result.IsComplete, "Restart Manager query failed: " + result.Error);
                foreach (LockingProcess process in result.Processes)
                {
                    if (process.ProcessId != child.Id) continue;
                    Assert(process.StartTimeFileTime == child.StartTimeFileTime,
                        "Restart Manager did not preserve the fixture process creation time.");
                    Assert(!String.IsNullOrEmpty(process.Name), "The fixture PID did not have a display name.");
                    Assert(!String.IsNullOrEmpty(process.ImagePath), "The fixture executable path was not queried.");
                    return process;
                }
                Thread.Sleep(100);
            }
            throw new InvalidOperationException("Restart Manager did not report the owned holder PID " + child.Id + ".");
        }

        private static string MakeFile(string name, string contents)
        {
            string path = Path.Combine(testRoot, name);
            File.WriteAllText(path, contents, Utf8);
            return path;
        }

        private static string Read(string path)
        {
            return File.ReadAllText(path, Utf8);
        }

        private static void Completed(FileOperationResult result)
        {
            Assert(result != null && result.Status == OperationStatus.Completed,
                "Expected Completed: " + Describe(result));
        }

        private static void Failed(FileOperationResult result)
        {
            Assert(result != null && result.Status == OperationStatus.Failed,
                "Expected Failed without partial mutation: " + Describe(result));
            Assert(!String.IsNullOrWhiteSpace(result.Message), "A failed operation omitted its explanation.");
            Assert(String.IsNullOrEmpty(result.RetainedPath), "The operation failed to clean its owned stage: " +
                result.RetainedPath);
        }

        private static string Describe(FileOperationResult result)
        {
            return result == null ? "null result" : result.Status + " / Win32 " + result.ErrorCode +
                " / " + result.Message + " / retained=" + result.RetainedPath;
        }

        private static void InspectionRejected(string path)
        {
            try
            {
                FileOperations.Inspect(path);
            }
            catch (ArgumentException ex)
            {
                Assert(!String.IsNullOrWhiteSpace(ex.Message), "Rejected input omitted its explanation.");
                return;
            }
            throw new InvalidOperationException("Inspection accepted a directory or ADS path: " + path);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void CleanupTestRoot()
        {
            string expectedParent = Path.GetFullPath(casesRoot).TrimEnd(Path.DirectorySeparatorChar);
            string expectedRoot = Path.GetFullPath(Path.Combine(casesRoot, testCaseId)).TrimEnd(Path.DirectorySeparatorChar);
            string resolved = Path.GetFullPath(testRoot).TrimEnd(Path.DirectorySeparatorChar);
            Guid ownedId;
            Assert(String.Equals(resolved, expectedRoot, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(Path.GetDirectoryName(resolved), expectedParent, StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(Path.GetFileName(resolved), "N", out ownedId),
                "Refused to clean an unverified fixture directory.");
            foreach (string file in Directory.GetFiles(resolved))
            {
                Assert(String.Equals(Path.GetDirectoryName(Path.GetFullPath(file)), resolved,
                    StringComparison.OrdinalIgnoreCase), "Refused to clean a file outside the owned fixture directory.");
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            foreach (string directory in Directory.GetDirectories(resolved))
                Directory.Delete(directory, false);
            Directory.Delete(resolved, false);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);

        private static int HoldFile(string[] args)
        {
            if (args.Length != 3 || (args[2] != "share-delete" && args[2] != "deny-delete" &&
                args[2] != "deny-delete-shared"))
                return 2;
            try
            {
                FileShare share = args[2] == "share-delete" ? FileShare.ReadWrite | FileShare.Delete :
                    args[2] == "deny-delete-shared" ? FileShare.ReadWrite : FileShare.None;
                using (FileStream file = new FileStream(args[1], FileMode.Open, FileAccess.Read, share))
                {
                    Console.WriteLine("READY|" + ReadHeld(file));
                    Console.Out.Flush();
                    Stopwatch lifetime = Stopwatch.StartNew();
                    for (;;)
                    {
                        int remaining = 120000 - (int)lifetime.ElapsedMilliseconds;
                        if (remaining <= 0) return 3;
                        // Console.In may implement ReadLineAsync synchronously;
                        // keep the holder lifetime bounded even with no input.
                        Task<string> input = Task.Run(delegate { return Console.In.ReadLine(); });
                        if (!input.Wait(remaining)) return 3;
                        string command = input.Result;
                        if (command == null || command == "exit") return 0;
                        if (command == "read")
                        {
                            Console.WriteLine("READ|" + ReadHeld(file));
                            Console.Out.Flush();
                        }
                        else
                            return 2;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        private static string ReadHeld(FileStream file)
        {
            file.Position = 0;
            byte[] bytes = new byte[checked((int)file.Length)];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int count = file.Read(bytes, offset, bytes.Length - offset);
                if (count == 0) throw new IOException("The held fixture file was truncated.");
                offset += count;
            }
            return Convert.ToBase64String(bytes);
        }

        private sealed class FixtureChild : IDisposable
        {
            private readonly Process process;
            private readonly long startTimeFileTime;

            private FixtureChild(Process process)
            {
                this.process = process;
                startTimeFileTime = process.StartTime.ToFileTimeUtc();
            }

            public int Id { get { return process.Id; } }
            public long StartTimeFileTime { get { return startTimeFileTime; } }
            public bool IsAlive { get { return !process.HasExited; } }

            public static FixtureChild Start(string path, bool allowDelete)
            {
                return Start(path, allowDelete ? "share-delete" : "deny-delete");
            }

            public static FixtureChild Start(string path, string shareMode)
            {
                string executable = Assembly.GetExecutingAssembly().Location;
                ProcessStartInfo start = new ProcessStartInfo(executable,
                    "--hold-file \"" + path + "\" " + shareMode);
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.RedirectStandardInput = true;
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.StandardOutputEncoding = Utf8;
                start.StandardErrorEncoding = Utf8;
                Process process = Process.Start(start);
                Assert(process != null, "Could not start the owned fixture process.");
                FixtureChild child = new FixtureChild(process);
                try
                {
                    string ready = child.ReadLine();
                    Assert(ready == "READY|" + Convert.ToBase64String(Utf8.GetBytes(OldContent)),
                        "The owned process did not acquire its file handle: " + ready);
                    return child;
                }
                catch
                {
                    child.Dispose();
                    throw;
                }
            }

            public string ReadOriginal()
            {
                Assert(IsAlive, "The owned holder process exited unexpectedly.");
                process.StandardInput.WriteLine("read");
                process.StandardInput.Flush();
                string response = ReadLine();
                Assert(response != null && response.StartsWith("READ|", StringComparison.Ordinal),
                    "The held file read did not return content: " + response);
                return Utf8.GetString(Convert.FromBase64String(response.Substring(5)));
            }

            public bool WaitForExit(int milliseconds)
            {
                return process.WaitForExit(milliseconds);
            }

            private string ReadLine()
            {
                Task<string> line = process.StandardOutput.ReadLineAsync();
                Assert(line.Wait(5000), "The owned fixture process did not answer within 5 seconds.");
                string value = line.Result;
                if (value == null && process.HasExited)
                    throw new InvalidOperationException("Owned fixture exited with " + process.ExitCode +
                        ": " + process.StandardError.ReadToEnd());
                return value;
            }

            public void Dispose()
            {
                try
                {
                    if (!process.HasExited)
                    {
                        try
                        {
                            process.StandardInput.WriteLine("exit");
                            process.StandardInput.Flush();
                        }
                        catch (IOException) { }
                        if (!process.WaitForExit(3000))
                        {
                            Assert(process.StartTime.ToFileTimeUtc() == startTimeFileTime,
                                "Refused to kill a fixture process whose identity changed.");
                            process.Kill();
                            Assert(process.WaitForExit(3000), "The owned fixture process did not exit during cleanup.");
                        }
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }
}
