using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

// Independent, real-Windows probe. All fixtures are confined to a unique owned
// child of artifacts/probe/cases beside the probe executable, never system Temp.
internal static class PosixProbe
{
    private const uint Delete = 0x00010000;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint CreateNew = 1;
    private const uint NormalAttribute = 0x80;
    private const int RenameEx = 22;
    private const int DispositionEx = 21;
    private static int failures;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint disposition, uint attributes, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        IntPtr information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle handle, byte[] bytes, uint length,
        out uint written, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteFileW(string path);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersion
    {
        public uint Size;
        public uint Major;
        public uint Minor;
        public uint Build;
        public uint Platform;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
    }
    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
    private static extern int RtlGetVersion(ref OsVersion version);

    private static string NativePath(string path)
    {
        return path.StartsWith("\\\\", StringComparison.Ordinal) ? "\\\\?\\UNC\\" + path.Substring(2) : "\\\\?\\" + path;
    }

    private static bool Rename(SafeFileHandle source, string destination, uint flags, out int error)
    {
        byte[] name = Encoding.Unicode.GetBytes(NativePath(destination));
        int rootOffset = IntPtr.Size == 8 ? 8 : 4;
        int lengthOffset = rootOffset + IntPtr.Size;
        int nameOffset = lengthOffset + 4;
        IntPtr block = Marshal.AllocHGlobal(nameOffset + name.Length + 2);
        try
        {
            for (int i = 0; i < nameOffset; ++i) Marshal.WriteByte(block, i, 0);
            Marshal.WriteInt32(block, 0, unchecked((int)flags));
            Marshal.WriteIntPtr(block, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(block, lengthOffset, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(block, nameOffset), name.Length);
            Marshal.WriteInt16(block, nameOffset + name.Length, 0);
            bool result = SetFileInformationByHandle(source, RenameEx, block, (uint)(nameOffset + name.Length + 2));
            error = result ? 0 : Marshal.GetLastWin32Error();
            return result;
        }
        finally { Marshal.FreeHGlobal(block); }
    }

    private static bool Unlink(SafeFileHandle handle, out int error)
    {
        IntPtr block = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(block, 0x13); // DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE
            bool result = SetFileInformationByHandle(handle, DispositionEx, block, 4);
            error = result ? 0 : Marshal.GetLastWin32Error();
            return result;
        }
        finally { Marshal.FreeHGlobal(block); }
    }

    private static SafeFileHandle MakeStage(string path, string content)
    {
        SafeFileHandle result = CreateFileW(NativePath(path), Delete | GenericRead | GenericWrite,
            ShareAll, IntPtr.Zero, CreateNew, NormalAttribute, IntPtr.Zero);
        if (result.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Create staging file");
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            uint count;
            if (!WriteFile(result, bytes, (uint)bytes.Length, out count, IntPtr.Zero) || count != bytes.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Write staging content");
            if (!FlushFileBuffers(result)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Flush staging file");
            result.Dispose();
            result = CreateFileW(NativePath(path), Delete, ShareAll, IntPtr.Zero, OpenExisting, NormalAttribute, IntPtr.Zero);
            if (result.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Reopen owned staging file with DELETE only");
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static string ReadHolder(FileStream holder)
    {
        holder.Position = 0;
        byte[] bytes = new byte[128];
        int count = holder.Read(bytes, 0, bytes.Length);
        return Encoding.UTF8.GetString(bytes, 0, count);
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        StringBuilder path = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, 0);
        return length == 0 ? "error=" + Marshal.GetLastWin32Error() : path.ToString();
    }

    private static string ObserveContent(string path)
    {
        try
        {
            using (FileStream stream = new FileStream(NativePath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream)) return "[" + reader.ReadToEnd() + "]";
        }
        catch (Exception error) { return "error=" + error.GetType().Name + " hresult=" + error.HResult; }
    }

    private static void Check(string description, bool condition)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + description);
        if (!condition) ++failures;
    }

    private static void Baseline(string root, string mode, FileShare sharing)
    {
        string target = Path.Combine(root, "baseline-" + mode + ".txt");
        File.WriteAllText(target, "old", new UTF8Encoding(false));
        using (FileStream holder = new FileStream(target, FileMode.Open, FileAccess.Read, sharing))
        {
            bool standard = DeleteFileW(NativePath(target));
            int standardError = standard ? 0 : Marshal.GetLastWin32Error();
            Console.WriteLine("OBS standard-delete " + mode + " result=" + standard + " error=" + standardError + " pathExists=" + File.Exists(target));
            if (!standard)
            {
                using (SafeFileHandle handle = CreateFileW(NativePath(target), Delete, ShareAll,
                    IntPtr.Zero, OpenExisting, NormalAttribute, IntPtr.Zero))
                {
                    int error;
                    bool result;
                    if (handle.IsInvalid) { error = Marshal.GetLastWin32Error(); result = false; }
                    else result = Unlink(handle, out error);
                    Console.WriteLine("OBS direct-posix-delete " + mode + " result=" + result + " error=" + error + " pathExists=" + File.Exists(target));
                }
            }
            Check("baseline holder content " + mode, ReadHolder(holder) == "old");
        }
        string directTarget = Path.Combine(root, "direct-" + mode + ".txt");
        File.WriteAllText(directTarget, "old", new UTF8Encoding(false));
        using (FileStream holder = new FileStream(directTarget, FileMode.Open, FileAccess.Read, sharing))
        {
            int error;
            bool removed;
            using (SafeFileHandle handle = CreateFileW(NativePath(directTarget), Delete, ShareAll,
                IntPtr.Zero, OpenExisting, NormalAttribute, IntPtr.Zero))
            {
                if (handle.IsInvalid) { error = Marshal.GetLastWin32Error(); removed = false; }
                else removed = Unlink(handle, out error);
            }
            bool expected = (sharing & FileShare.Delete) != 0;
            Console.WriteLine("OBS direct-posix-independent " + mode + " result=" + removed + " error=" + error + " pathExists=" + File.Exists(directTarget));
            Check("direct POSIX respects sharing " + mode, removed == expected && (expected || error == 32));
            Check("direct POSIX path observation " + mode, File.Exists(directTarget) == !expected);
            Check("direct POSIX old handle remains readable " + mode, ReadHolder(holder) == "old");
        }
    }

    private static void ReplaceCase(string root, string mode, FileShare sharing, bool readOnly)
    {
        string target = Path.Combine(root, "replace-" + mode + ".txt");
        string stagePath = target + ".staging";
        File.WriteAllText(target, "old", new UTF8Encoding(false));
        if (readOnly) File.SetAttributes(target, FileAttributes.ReadOnly);
        using (FileStream holder = new FileStream(target, FileMode.Open, FileAccess.Read, sharing))
        using (SafeFileHandle stage = MakeStage(stagePath, "new"))
        {
            int error;
            bool result = Rename(stage, target, readOnly ? 0x43u : 3u, out error);
            Console.WriteLine("OBS posix-replace " + mode + " result=" + result + " error=" + error);
            Console.WriteLine("OBS replacement-location " + mode + " handle=" + FinalPath(stage) + " stageExists=" + File.Exists(stagePath) + " targetContent=" + ObserveContent(target));
            bool expected = (sharing & FileShare.Delete) != 0;
            Check("replace respects sharing " + mode, result == expected && (expected || error == 32));
            Check("old holder keeps old content " + mode, ReadHolder(holder) == "old");
            if (result)
            {
                stage.Dispose();
                Check("new path has new content " + mode, ObserveContent(target) == "[new]");
                Check("stage path gone " + mode, !File.Exists(stagePath));
            }
            else Check("failed replacement keeps stage " + mode, File.Exists(stagePath));
        }
    }

    private static void DeleteCase(string root, string mode, FileShare sharing, bool readOnly)
    {
        string target = Path.Combine(root, "delete-" + mode + ".txt");
        string stagePath = target + ".tombstone";
        File.WriteAllText(target, "old", new UTF8Encoding(false));
        if (readOnly) File.SetAttributes(target, FileAttributes.ReadOnly);
        using (FileStream holder = new FileStream(target, FileMode.Open, FileAccess.Read, sharing))
        using (SafeFileHandle tombstone = MakeStage(stagePath, ""))
        {
            int error;
            bool replaced = Rename(tombstone, target, readOnly ? 0x43u : 3u, out error);
            Console.WriteLine("OBS tombstone-replace " + mode + " result=" + replaced + " error=" + error);
            Console.WriteLine("OBS tombstone-location " + mode + " handle=" + FinalPath(tombstone) + " stageExists=" + File.Exists(stagePath) + " targetContent=" + ObserveContent(target));
            bool expected = (sharing & FileShare.Delete) != 0;
            Check("tombstone respects sharing " + mode, replaced == expected && (expected || error == 32));
            if (replaced)
            {
                bool removed = Unlink(tombstone, out error);
                Console.WriteLine("OBS tombstone-unlink " + mode + " result=" + removed + " error=" + error + " pathExists=" + File.Exists(target));
                Check("tombstone unlink " + mode, removed);
                tombstone.Dispose();
                Console.WriteLine("OBS after-close " + mode + " pathExists=" + File.Exists(target) + " targetContent=" + ObserveContent(target) + " stageExists=" + File.Exists(stagePath));
                Check("deleted path absent while holder alive " + mode, !File.Exists(target));
                Check("deleted file holder keeps old content " + mode, ReadHolder(holder) == "old");
            }
        }
    }

    private static void ImageCase(string root, bool deletion)
    {
        string tag = deletion ? "image-delete" : "image-replace";
        string target = Path.Combine(root, tag + ".exe");
        string ready = Path.Combine(root, tag + ".ready");
        string release = Path.Combine(root, tag + ".release");
        string completed = Path.Combine(root, tag + ".completed");
        string stagePath = Path.Combine(root, tag + ".stage");
        File.Copy(typeof(PosixProbe).Assembly.Location, target);
        ProcessStartInfo start = new ProcessStartInfo(target, "--image-holder \"" + ready + "\" \"" + release + "\" \"" + completed + "\"");
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.WindowStyle = ProcessWindowStyle.Hidden;
        using (Process child = Process.Start(start))
        {
            try
            {
                Stopwatch deadline = Stopwatch.StartNew();
                while (!File.Exists(ready) && !child.HasExited && deadline.ElapsedMilliseconds < 5000) Thread.Sleep(20);
                if (!File.Exists(ready)) throw new InvalidOperationException("Owned image holder did not become ready");
                using (SafeFileHandle stage = MakeStage(stagePath, deletion ? "" : "new"))
                {
                    int error;
                    bool replaced = Rename(stage, target, 3, out error);
                    Console.WriteLine("OBS " + tag + " rename result=" + replaced + " error=" + error + " processAlive=" + !child.HasExited);
                    if (replaced && deletion)
                    {
                        bool removed = Unlink(stage, out error);
                        Console.WriteLine("OBS " + tag + " unlink result=" + removed + " error=" + error + " pathExists=" + File.Exists(target));
                        stage.Dispose();
                        Check(tag + " path absent", removed && !File.Exists(target));
                    }
                    else if (replaced) { stage.Dispose(); Check(tag + " new content", File.ReadAllText(target) == "new"); }
                    else
                    {
                        Console.WriteLine("LIMIT " + tag + " is refused by this Windows/NTFS image-section policy; this is an observed limit, not a failed probe.");
                        Check(tag + " preserves stage after refusal", error == 5 && File.Exists(stagePath));
                    }
                    Check(tag + " old process remains alive", !child.HasExited);
                }
                File.WriteAllText(release, "release");
                Check(tag + " old executable can finish", child.WaitForExit(5000) && child.ExitCode == 0 && File.Exists(completed));
            }
            finally
            {
                if (!child.HasExited)
                {
                    File.WriteAllText(release, "release");
                    if (!child.WaitForExit(1000)) { child.Kill(); child.WaitForExit(5000); }
                }
            }
        }
    }

    private static void DataMappingCase(string root, bool deletion)
    {
        string mode = deletion ? "data-mapping-delete" : "data-mapping-replace";
        string target = Path.Combine(root, mode + ".txt");
        string stagePath = target + ".stage";
        File.WriteAllText(target, "old", new UTF8Encoding(false));
        using (FileStream holder = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        using (MemoryMappedFile mapping = MemoryMappedFile.CreateFromFile(holder, null, 0,
            MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true))
        using (MemoryMappedViewAccessor view = mapping.CreateViewAccessor(0, 3, MemoryMappedFileAccess.ReadWrite))
        using (SafeFileHandle stage = MakeStage(stagePath, deletion ? "" : "new"))
        {
            int error;
            bool replaced = Rename(stage, target, 3, out error);
            Console.WriteLine("OBS " + mode + " rename result=" + replaced + " error=" + error);
            Check(mode + " replacement", replaced);
            if (replaced && deletion)
            {
                bool removed = Unlink(stage, out error);
                stage.Dispose();
                Console.WriteLine("OBS " + mode + " unlink result=" + removed + " error=" + error + " pathExists=" + File.Exists(target));
                Check(mode + " path absent", removed && !File.Exists(target));
            }
            else if (replaced)
            {
                stage.Dispose();
                Check(mode + " new content", ObserveContent(target) == "[new]");
            }
            byte[] oldBytes = new byte[3];
            view.ReadArray(0, oldBytes, 0, oldBytes.Length);
            Check(mode + " old mapping retains content", Encoding.UTF8.GetString(oldBytes) == "old");
        }
    }

    private static void PathEncodingCase(string root)
    {
        string directory = Path.Combine(root, "中文 空格");
        for (int i = 0; i < 4; ++i) directory = Path.Combine(directory, new string('a', 80) + i);
        Directory.CreateDirectory(NativePath(directory));
        string target = Path.Combine(directory, "正在使用 中文.txt");
        string stagePath = target + ".stage";
        File.WriteAllText(NativePath(target), "old", new UTF8Encoding(false));
        using (FileStream holder = new FileStream(NativePath(target), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (SafeFileHandle stage = MakeStage(stagePath, "new"))
        {
            int error;
            bool replaced = Rename(stage, target, 3, out error);
            Console.WriteLine("OBS unicode-long-path chars=" + target.Length + " rename result=" + replaced + " error=" + error);
            stage.Dispose();
            Check("unicode long path replacement", replaced);
            Check("unicode long path new content", ObserveContent(target) == "[new]");
            Check("unicode long path old handle content", ReadHolder(holder) == "old");
            Check("unicode long path staging name removed", !File.Exists(NativePath(stagePath)));
        }
    }

    private static int Main(string[] args)
    {
        AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
        AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        if (args.Length == 4 && args[0] == "--image-holder")
        {
            File.WriteAllText(args[1], "ready");
            Stopwatch wait = Stopwatch.StartNew();
            while (!File.Exists(args[2]) && wait.ElapsedMilliseconds < 30000) Thread.Sleep(20);
            File.WriteAllText(args[3], "finished");
            return 0;
        }
        string casesRoot = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(PosixProbe).Assembly.Location), "cases"));
        string root = Path.Combine(casesRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        OsVersion version = new OsVersion();
        version.Size = (uint)Marshal.SizeOf(version);
        int versionResult = RtlGetVersion(ref version);
        Console.WriteLine("OS " + version.Major + "." + version.Minor + "." + version.Build + " versionResult=" + versionResult + " pointerBytes=" + IntPtr.Size + " filesystem=" + new DriveInfo(Path.GetPathRoot(root)).DriveFormat + " root=" + root);
        try
        {
            string[] modes = { "share-none", "deny-delete", "share-delete" };
            FileShare[] shares = { FileShare.None, FileShare.ReadWrite, FileShare.ReadWrite | FileShare.Delete };
            for (int i = 0; i < modes.Length; ++i)
            {
                Baseline(root, modes[i], shares[i]);
                ReplaceCase(root, modes[i], shares[i], false);
                DeleteCase(root, modes[i], shares[i], false);
            }
            ReplaceCase(root, "readonly-deny-delete", FileShare.ReadWrite, true);
            DeleteCase(root, "readonly-deny-delete", FileShare.ReadWrite, true);
            ReplaceCase(root, "readonly-share-delete", FileShare.ReadWrite | FileShare.Delete, true);
            DeleteCase(root, "readonly-share-delete", FileShare.ReadWrite | FileShare.Delete, true);
            DataMappingCase(root, false);
            DataMappingCase(root, true);
            PathEncodingCase(root);
            ImageCase(root, false);
            ImageCase(root, true);
        }
        catch (Exception error)
        {
            ++failures;
            Console.WriteLine("FAIL unhandled " + error);
        }
        finally
        {
            // Cleanup is restricted to this run's single GUID child. Never
            // remove the shared cases directory or an arbitrary caller path.
            if (!String.Equals(Path.GetDirectoryName(Path.GetFullPath(root)), casesRoot,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing cleanup outside the owned probe case directory.");
            foreach (string file in Directory.GetFiles(NativePath(root), "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(NativePath(root), true);
        }
        Console.WriteLine("RESULT failures=" + failures);
        return failures == 0 ? 0 : 1;
    }
}
