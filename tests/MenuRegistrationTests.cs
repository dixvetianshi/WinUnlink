using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.Win32;
using WinUnlink;

namespace WinUnlinkTests
{
    public static class MenuRegistrationTests
    {
        public static int Main(string[] args)
        {
            if (args == null || args.Length != 1 || args[0] != "--allow-real-user-registry")
            {
                Console.Error.WriteLine("此测试会写入真实 HKCU 注册表，仅供隔离虚拟机或沙盒中人工启用。" +
                    "必须显式传入 --allow-real-user-registry；默认不创建文件、不读取或修改注册表。");
                return 2;
            }

            string id = Guid.NewGuid().ToString("N");
            const string productRoot = @"Software\WinUnlink";
            const string integrationRoot = @"Software\WinUnlink\IntegrationTests";
            string registryRoot = integrationRoot + "\\" + id;
            string fileKey = registryRoot + @"\File";
            string directoryKey = registryRoot + @"\Directory";
            string[] menuKeys = { fileKey, directoryKey };
            string executable = Assembly.GetExecutingAssembly().Location;
            string casesDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(executable), "cases"));
            string testDirectory = Path.Combine(casesDirectory, id);
            bool productRootExisted = false;
            bool integrationRootExisted = false;
            bool productRootCreated = false;
            bool integrationRootCreated = false;
            bool runRootCreated = false;
            bool testDirectoryCreated = false;
            int passed = 0;
            int exitCode = 0;
            try
            {
                using (RegistryKey existing = Registry.CurrentUser.OpenSubKey(productRoot))
                    productRootExisted = existing != null;
                using (RegistryKey existing = Registry.CurrentUser.OpenSubKey(integrationRoot))
                    integrationRootExisted = existing != null;
                using (RegistryKey existing = Registry.CurrentUser.OpenSubKey(registryRoot))
                    if (existing != null) throw new InvalidOperationException("测试 GUID 分支已存在，保留其原始内容并停止测试。");
                if (Directory.Exists(testDirectory))
                    throw new InvalidOperationException("测试 GUID 目录已存在，保留其原始内容并停止测试。");

                if (!productRootExisted)
                {
                    using (RegistryKey created = Registry.CurrentUser.CreateSubKey(productRoot))
                    {
                        if (created == null) throw new InvalidOperationException("无法创建测试父分支。");
                        productRootCreated = true;
                    }
                }
                if (!integrationRootExisted)
                {
                    using (RegistryKey created = Registry.CurrentUser.CreateSubKey(integrationRoot))
                    {
                        if (created == null) throw new InvalidOperationException("无法创建测试集合分支。");
                        integrationRootCreated = true;
                    }
                }
                using (RegistryKey created = Registry.CurrentUser.CreateSubKey(registryRoot))
                {
                    if (created == null) throw new InvalidOperationException("无法创建本次测试的 GUID 分支。");
                    runRootCreated = true;
                }
                Directory.CreateDirectory(testDirectory);
                testDirectoryCreated = true;

                ContextMenuRegistration registration = new ContextMenuRegistration(menuKeys, testDirectory);
                ContextMenuRegistration legacyFileOnly = new ContextMenuRegistration(fileKey, testDirectory);
                string plan = new ContextMenuRegistration().Plan(executable);
                Check(plan.Contains(ContextMenuRegistration.MenuKeyPath) &&
                    plan.Contains(ContextMenuRegistration.DirectoryMenuKeyPath) && !plan.Contains(@"Directory\Background"),
                    "默认计划同时覆盖文件和文件夹且不增加空白处菜单"); passed++;

                MenuRegistrationResult first = registration.Install(executable);
                Check(first.Changed && first.BackupPaths.Length == 2 &&
                    first.BackupPath == first.BackupPaths[0] && first.BackupPaths.All(File.Exists),
                    "安装两项前分别生成配置备份"); passed++;
                for (int i = 0; i < menuKeys.Length; i++)
                {
                    XElement backup = XElement.Load(first.BackupPaths[i]);
                    Check((string)backup.Attribute("key") == menuKeys[i] && !(bool)backup.Attribute("existed"),
                        "备份保留菜单原始缺省状态 " + i); passed++;
                }
                CheckCommand(fileKey, executable);
                CheckCommand(directoryKey, executable);
                passed++;
                MenuRegistrationResult second = registration.Install(executable);
                Check(!second.Changed && second.BackupPaths.SequenceEqual(first.BackupPaths) &&
                    Directory.GetFiles(testDirectory, "*.xml").Length == 2,
                    "重复安装两项保持幂等并保留首次备份"); passed++;

                string movedExecutable = Path.Combine(testDirectory, "移 动 程序.exe");
                File.Copy(executable, movedExecutable);
                MenuRegistrationResult moved = registration.Install(movedExecutable);
                Check(moved.Changed && moved.BackupPaths.SequenceEqual(first.BackupPaths),
                    "移动程序后更新两项命令并保留原始备份"); passed++;
                CheckCommand(fileKey, movedExecutable);
                CheckCommand(directoryKey, movedExecutable);
                passed++;
                MenuRegistrationResult removed = registration.Uninstall();
                Check(removed.Changed && Missing(fileKey) && Missing(directoryKey) && first.BackupPaths.All(File.Exists),
                    "卸载恢复两项缺省状态并保留备份"); passed++;
                Check(!registration.Uninstall().Changed, "重复卸载两项保持幂等"); passed++;

                MenuRegistrationResult oldFile = legacyFileOnly.Install(executable);
                string oldFileSnapshot = Snapshot(fileKey);
                Check(Missing(directoryKey), "旧版 fixture 只注册文件菜单"); passed++;
                MenuRegistrationResult upgrade = registration.Install(executable);
                Check(upgrade.Changed && upgrade.BackupPaths.Length == 2 && upgrade.BackupPath == oldFile.BackupPath &&
                    Snapshot(fileKey) == oldFileSnapshot && !Missing(directoryKey),
                    "文件菜单已注册时仍新增文件夹并保留旧文件项与备份"); passed++;
                Check(!registration.Install(executable).Changed, "补全后的两项注册保持幂等"); passed++;
                registration.Uninstall();

                legacyFileOnly.Install(executable);
                string fileBeforeForeign = Snapshot(fileKey);
                int backupsBeforeForeign = Directory.GetFiles(testDirectory, "*.xml").Length;
                using (RegistryKey foreign = Registry.CurrentUser.CreateSubKey(directoryKey))
                    foreign.SetValue("UserChoice", "original");
                Expect<InvalidOperationException>(delegate { registration.Install(movedExecutable); });
                using (RegistryKey foreign = Registry.CurrentUser.OpenSubKey(directoryKey))
                    Check(Snapshot(fileKey) == fileBeforeForeign && (string)foreign.GetValue("UserChoice") == "original" &&
                        foreign.GetValueNames().Length == 1 &&
                        Directory.GetFiles(testDirectory, "*.xml").Length == backupsBeforeForeign,
                        "文件夹菜单冲突的全量预检保留文件菜单并不产生新备份");
                passed++;
                Expect<InvalidOperationException>(delegate { registration.Uninstall(); });
                Check(Snapshot(fileKey) == fileBeforeForeign && !Missing(directoryKey),
                    "卸载预检发现外来文件夹项时保留两项"); passed++;
                Registry.CurrentUser.DeleteSubKeyTree(directoryKey);
                registration.Uninstall();

                using (RegistryKey foreign = Registry.CurrentUser.CreateSubKey(fileKey))
                    foreign.SetValue("UserChoice", "original file");
                Expect<InvalidOperationException>(delegate { registration.Install(executable); });
                using (RegistryKey foreign = Registry.CurrentUser.OpenSubKey(fileKey))
                    Check((string)foreign.GetValue("UserChoice") == "original file" && Missing(directoryKey),
                        "文件菜单冲突时不创建文件夹菜单");
                passed++;
                Registry.CurrentUser.DeleteSubKeyTree(fileKey);

                MenuRegistrationResult withEdit = registration.Install(executable);
                string fileBeforeEdit = Snapshot(fileKey);
                using (RegistryKey edited = Registry.CurrentUser.OpenSubKey(directoryKey, true))
                    edited.SetValue("UserChoice", "keep me");
                Expect<InvalidOperationException>(delegate { registration.Uninstall(); });
                Expect<InvalidOperationException>(delegate { registration.Install(movedExecutable); });
                using (RegistryKey edited = Registry.CurrentUser.OpenSubKey(directoryKey, true))
                {
                    Check(Snapshot(fileKey) == fileBeforeEdit && (string)edited.GetValue("UserChoice") == "keep me",
                        "文件夹项被用户修改时安装和卸载都保留全部现有项");
                    edited.DeleteValue("UserChoice");
                }
                passed++;

                string fileBeforeBackupFailure = Snapshot(fileKey);
                string directoryBeforeBackupFailure = Snapshot(directoryKey);
                string folderBackupText = File.ReadAllText(withEdit.BackupPaths[1]);
                File.WriteAllText(withEdit.BackupPaths[1], "<invalid />");
                Expect<InvalidDataException>(delegate { registration.Uninstall(); });
                Expect<InvalidDataException>(delegate { registration.Install(movedExecutable); });
                Check(Snapshot(fileKey) == fileBeforeBackupFailure && Snapshot(directoryKey) == directoryBeforeBackupFailure,
                    "文件夹备份无效时全量预检保留两项且不更新文件菜单"); passed++;
                File.WriteAllText(withEdit.BackupPaths[1], folderBackupText);
                registration.Uninstall();

                legacyFileOnly.Install(movedExecutable);
                string persistentSnapshot = Snapshot(fileKey);
                Expect<InvalidOperationException>(delegate { registration.Install(movedExecutable, true); });
                Check(Snapshot(fileKey) == persistentSnapshot && Missing(directoryKey),
                    "不能把旧持久文件菜单当成临时菜单复用或新增临时文件夹项"); passed++;
                registration.Uninstall();

                MenuRegistrationResult temporaryFile = legacyFileOnly.Install(movedExecutable, true);
                string temporarySnapshot = Snapshot(fileKey);
                MenuRegistrationResult temporaryUpgrade = registration.Install(movedExecutable, true);
                Check(temporaryUpgrade.Changed && temporaryUpgrade.BackupPath == temporaryFile.BackupPath &&
                    Snapshot(fileKey) == temporarySnapshot && !Missing(directoryKey) &&
                    temporaryUpgrade.BackupPaths.All(p => p.StartsWith(Path.Combine(testDirectory, "temporary-menu-backups"),
                        StringComparison.OrdinalIgnoreCase)),
                    "旧临时文件菜单补全文件夹时保留其状态和程序位置内的备份"); passed++;
                AssertVolatile(fileKey);
                AssertVolatile(fileKey + @"\command");
                AssertVolatile(directoryKey);
                AssertVolatile(directoryKey + @"\command");
                passed++;
                Check(!registration.Install(movedExecutable, true).Changed,
                    "两项临时菜单重复启用保持幂等"); passed++;
                Expect<InvalidOperationException>(delegate { registration.Install(movedExecutable, false); });
                Check(Snapshot(fileKey) == temporarySnapshot && !Missing(directoryKey),
                    "临时与持久模式不一致时保留两项"); passed++;
                registration.Uninstall();
                Check(Missing(fileKey) && Missing(directoryKey) && temporaryUpgrade.BackupPaths.All(File.Exists),
                    "手动卸载两项临时菜单并保留配置备份"); passed++;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL menu tests: " + error);
                exitCode = 1;
            }
            finally
            {
                // Only this run's owned GUID branch may be removed. Parent
                // branches are removed only if this test created them and they
                // remain empty; existing or unknown parent contents stay.
                if (runRootCreated)
                    TryCleanup(delegate { Registry.CurrentUser.DeleteSubKeyTree(registryRoot, false); },
                        "本次测试 GUID 分支", ref exitCode);
                if (integrationRootCreated && !integrationRootExisted)
                    TryCleanup(delegate { DeleteEmptyKey(integrationRoot); }, "测试集合父分支", ref exitCode);
                if (productRootCreated && !productRootExisted)
                    TryCleanup(delegate { DeleteEmptyKey(productRoot); }, "测试创建的产品父分支", ref exitCode);
                if (testDirectoryCreated)
                    TryCleanup(delegate
                    {
                        string absolute = Path.GetFullPath(testDirectory).TrimEnd(Path.DirectorySeparatorChar);
                        string expected = Path.GetFullPath(Path.Combine(casesDirectory, id)).TrimEnd(Path.DirectorySeparatorChar);
                        Guid ownedId;
                        if (!String.Equals(absolute, expected, StringComparison.OrdinalIgnoreCase) ||
                            !Guid.TryParseExact(Path.GetFileName(absolute), "N", out ownedId))
                            throw new InvalidOperationException("拒绝清理未经确认的测试目录。");
                        if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
                    }, "本次测试 GUID 目录", ref exitCode);
            }
            Console.WriteLine("RESULT menu tests passed=" + passed + " failed=" + (exitCode == 0 ? 0 : 1));
            return exitCode;
        }

        private static bool Missing(string path)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path)) return key == null;
        }

        private static string Snapshot(string path)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path))
            {
                if (key == null) return null;
                string[] values = key.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Select(n => n + "|" + key.GetValueKind(n) + "|" +
                        Convert.ToString(key.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames))).ToArray();
                using (RegistryKey command = key.OpenSubKey("command"))
                    return String.Join("\n", values) + "\ncommand=" +
                        (command == null ? "<missing>" : Convert.ToString(command.GetValue("")));
            }
        }

        private static void CheckCommand(string path, string executable)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path))
            using (RegistryKey command = key == null ? null : key.OpenSubKey("command"))
                Check(command != null && (string)command.GetValue("") == "\"" + executable + "\" --inspect \"%1\"",
                    "中文和空格路径的菜单命令引用：" + path);
        }

        private static void AssertVolatile(string path)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path, true))
            {
                if (key == null) throw new InvalidOperationException("临时菜单项缺失：" + path);
                bool created = false;
                bool refusedNonvolatile = false;
                try
                {
                    using (RegistryKey child = key.CreateSubKey("nonvolatile-probe",
                        RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryOptions.None))
                        created = child != null;
                }
                catch (IOException error)
                {
                    // ERROR_CHILD_MUST_BE_VOLATILE: a volatile key cannot contain
                    // a nonvolatile child. This checks the actual registry flag.
                    if ((error.HResult & 0xffff) != 1021) throw;
                    refusedNonvolatile = true;
                }
                finally
                {
                    if (created) key.DeleteSubKey("nonvolatile-probe", false);
                }
                Check(refusedNonvolatile, "菜单项实际使用 Volatile：" + path);
            }
        }

        private static void Expect<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException("预期的保护失败未出现：" + typeof(T).Name);
        }

        private static void DeleteEmptyKey(string path)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path))
            {
                if (key == null) return;
                if (key.SubKeyCount != 0 || key.ValueCount != 0) return;
            }
            Registry.CurrentUser.DeleteSubKey(path, false);
        }

        private static void TryCleanup(Action action, string name, ref int exitCode)
        {
            try { action(); }
            catch (Exception error)
            {
                exitCode = 1;
                Console.Error.WriteLine("FAIL cleanup " + name + ": " + error.Message);
            }
        }

        private static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            Console.WriteLine("PASS " + name);
        }
    }
}
