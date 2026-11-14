using System;
using System.IO;
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
            string registryRoot = @"Software\WinUnlink\IntegrationTests\" + id;
            string menuKey = registryRoot + @"\Menu";
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
                // Capture the original parent state before creating any registry keys.
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

                ContextMenuRegistration registration = new ContextMenuRegistration(menuKey, testDirectory);
                MenuRegistrationResult first = registration.Install(executable);
                Check(first.Changed && File.Exists(first.BackupPath), "安装前生成配置备份"); passed++;
                XElement backup = XElement.Load(first.BackupPath);
                Check((string)backup.Attribute("key") == menuKey && !(bool)backup.Attribute("existed"), "备份保留原始缺省状态"); passed++;
                using (RegistryKey installed = Registry.CurrentUser.OpenSubKey(menuKey))
                using (RegistryKey command = installed.OpenSubKey("command"))
                    Check((string)command.GetValue("") == "\"" + executable + "\" --inspect \"%1\"", "中文与空格路径的菜单命令引用");
                passed++;
                MenuRegistrationResult second = registration.Install(executable);
                Check(!second.Changed && second.BackupPath == first.BackupPath && Directory.GetFiles(testDirectory).Length == 1,
                    "重复安装保持幂等且保留首次备份"); passed++;

                string movedExecutable = Path.Combine(testDirectory, "移 动 程序.exe");
                File.Copy(executable, movedExecutable);
                MenuRegistrationResult moved = registration.Install(movedExecutable);
                Check(moved.Changed && moved.BackupPath == first.BackupPath, "移动程序后更新命令并保留原始备份"); passed++;
                MenuRegistrationResult removed = registration.Uninstall();
                using (RegistryKey existing = Registry.CurrentUser.OpenSubKey(menuKey))
                    Check(removed.Changed && existing == null && File.Exists(first.BackupPath), "卸载恢复原始状态并保留备份");
                passed++;
                Check(!registration.Uninstall().Changed, "重复卸载保持幂等"); passed++;

                using (RegistryKey foreign = Registry.CurrentUser.CreateSubKey(menuKey))
                    foreign.SetValue("UserChoice", "original");
                bool rejected = false;
                try { registration.Install(executable); }
                catch (InvalidOperationException) { rejected = true; }
                using (RegistryKey foreign = Registry.CurrentUser.OpenSubKey(menuKey))
                    Check(rejected && (string)foreign.GetValue("UserChoice") == "original" && foreign.GetValueNames().Length == 1,
                        "菜单名冲突时保留已有配置");
                passed++;
                Registry.CurrentUser.DeleteSubKeyTree(menuKey);

                MenuRegistrationResult withEdit = registration.Install(executable);
                using (RegistryKey edited = Registry.CurrentUser.OpenSubKey(menuKey, true)) edited.SetValue("UserChoice", "keep me");
                rejected = false;
                try { registration.Uninstall(); }
                catch (InvalidOperationException) { rejected = true; }
                using (RegistryKey edited = Registry.CurrentUser.OpenSubKey(menuKey, true))
                {
                    Check(rejected && (string)edited.GetValue("UserChoice") == "keep me", "卸载时保留安装后的用户修改");
                    edited.DeleteValue("UserChoice");
                }
                passed++;

                string backupText = File.ReadAllText(withEdit.BackupPath);
                File.WriteAllText(withEdit.BackupPath, "<invalid />");
                rejected = false;
                try { registration.Uninstall(); }
                catch (InvalidDataException) { rejected = true; }
                using (RegistryKey kept = Registry.CurrentUser.OpenSubKey(menuKey))
                    Check(rejected && kept != null, "备份无效时保留当前菜单");
                passed++;
                File.WriteAllText(withEdit.BackupPath, backupText);
                registration.Uninstall();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL menu tests: " + error);
                exitCode = 1;
            }
            finally
            {
                // Clean only this run's owned GUID branch, then parents that this
                // test created and that remain empty. Original/unknown keys stay.
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
