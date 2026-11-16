using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace WinUnlink
{
    public sealed class MenuRegistrationResult
    {
        public string Message { get; set; }
        public string BackupPath { get; set; }
        public string[] BackupPaths { get; set; }
        public bool Changed { get; set; }
    }

    public sealed class ContextMenuRegistration
    {
        public const string MenuKeyPath = @"Software\Classes\*\shell\WinUnlink.FileTools";
        public const string DirectoryMenuKeyPath = @"Software\Classes\Directory\shell\WinUnlink.FileTools";
        private const string OwnerId = "87cf7d33-ef74-424b-bf25-10aa3b780cd1";
        private const string OwnerName = "WinUnlinkOwner";
        private readonly string[] keyPaths;
        private readonly string backupDirectory;

        public ContextMenuRegistration() : this(new[] { MenuKeyPath, DirectoryMenuKeyPath },
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinUnlink", "backups")) { }

        // A single item supports old file-only fixtures and their upgrade tests.
        internal ContextMenuRegistration(string registryKeyPath, string backups)
            : this(new[] { registryKeyPath }, backups) { }

        // Tests use paths below their own GUID branch. Construction does not
        // open, create, or register any key.
        internal ContextMenuRegistration(string[] registryKeyPaths, string backups)
        {
            if (registryKeyPaths == null || registryKeyPaths.Length == 0 ||
                registryKeyPaths.Any(String.IsNullOrWhiteSpace) ||
                registryKeyPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != registryKeyPaths.Length)
                throw new ArgumentException("菜单注册路径必须非空且不重复。", "registryKeyPaths");
            if (String.IsNullOrWhiteSpace(backups)) throw new ArgumentException("备份目录不能为空。", "backups");
            keyPaths = (string[])registryKeyPaths.Clone();
            backupDirectory = backups;
        }

        public string Plan(string executablePath, bool temporary = false)
        {
            return "给当前用户的文件和文件夹右键菜单添加“WinUnlink：删除或替换”。\r\n\r\n" +
                "注册表：\r\n" + String.Join("\r\n", keyPaths.Select(p => "HKCU\\" + p)) +
                "\r\n程序：" + executablePath + "\r\n备份目录：" + BackupDirectoryFor(executablePath, temporary) +
                (temporary ? "\r\n\r\n两项临时菜单及其 command 子项仅保存在内存中，随用户注册表卸载而消失；重启会清除，也可手动移除。关闭程序不会移除菜单。" : "") +
                "\r\n\r\nWin11 中入口位于“显示更多选项”。程序移动后需重新注册。";
        }

        public MenuRegistrationResult Install(string executablePath, bool temporary = false)
        {
            executablePath = Path.GetFullPath(executablePath);
            if (!File.Exists(executablePath)) throw new IOException("注册的程序路径不存在：" + executablePath);

            // Validate all ownership, lifetime and backups before preparing any
            // new backup or modifying a registration. An unchanged file item
            // never short-circuits a missing folder item during an upgrade.
            List<MenuItem> items = ReadItems();
            foreach (MenuItem item in items)
            {
                if (item.Original == null) { item.NeedsChange = true; continue; }
                if (item.Original.Temporary != temporary)
                    throw new InvalidOperationException("HKCU\\" + item.KeyPath +
                        " 已有菜单的有效期与本次请求不同。请先移除已有菜单，再重新启用；未修改任何菜单项。");
                bool sameExecutable = String.Equals(item.Original.Executable, executablePath, StringComparison.OrdinalIgnoreCase);
                if (temporary && !sameExecutable)
                    throw new InvalidOperationException("临时菜单绑定原程序位置。请先移除已有菜单，再从新位置临时启用；未修改任何菜单项。");
                item.NeedsChange = !sameExecutable;
            }

            try
            {
                foreach (MenuItem item in items)
                {
                    BackupFacts backup = item.Original == null
                        ? CreateBackup(item.KeyPath, BackupDirectoryFor(executablePath, temporary)) : item.Original.Backup;
                    item.Desired = new MenuFacts(executablePath, backup, temporary);
                }
            }
            catch (Exception error)
            {
                string retained = String.Join("；", items.Where(i => i.Desired != null).Select(i => i.Desired.Backup.Path));
                throw new IOException("菜单备份准备未完成，尚未修改任何注册表菜单项。已准备的备份：" +
                    retained + "。原因：" + error.Message, error);
            }
            if (!items.Any(i => i.NeedsChange))
                return Result(items, false, "文件和文件夹右键菜单均已注册，无需修改。");

            try
            {
                foreach (MenuItem item in items.Where(i => i.NeedsChange))
                {
                    item.MutationStarted = true;
                    WriteMenu(item.KeyPath, item.Desired);
                }
            }
            catch (Exception error)
            {
                throw RollbackFailure(items, "右键菜单写入未完成", error);
            }
            NotifyExplorer();
            return Result(items, true, temporary
                ? "已临时启用当前用户的文件和文件夹右键菜单。随用户注册表卸载而失效，也可立即手动移除。"
                : "已注册当前用户的文件和文件夹右键菜单。");
        }

        public MenuRegistrationResult Uninstall()
        {
            // A backup or ownership failure on either item stops both removals.
            List<MenuItem> items = ReadItems();
            foreach (MenuItem item in items)
            {
                item.Desired = item.Original;
                item.NeedsChange = item.Original != null;
            }
            if (!items.Any(i => i.NeedsChange))
                return Result(items, false, "文件和文件夹右键菜单均未注册。");
            try
            {
                foreach (MenuItem item in items.Where(i => i.NeedsChange))
                {
                    item.MutationStarted = true;
                    Registry.CurrentUser.DeleteSubKeyTree(item.KeyPath, false);
                }
            }
            catch (Exception error)
            {
                throw RollbackFailure(items, "右键菜单移除未完成", error);
            }
            NotifyExplorer();
            return Result(items, true, "已移除文件和文件夹右键菜单；原有菜单项缺省状态已恢复，备份文件仍保留。");
        }

        private List<MenuItem> ReadItems()
        {
            List<MenuItem> items = new List<MenuItem>();
            foreach (string keyPath in keyPaths)
            {
                MenuItem item = new MenuItem(keyPath);
                using (RegistryKey existing = Registry.CurrentUser.OpenSubKey(keyPath))
                    if (existing != null) item.Original = ReadOwnedMenu(keyPath, existing);
                items.Add(item);
            }
            return items;
        }

        private MenuFacts ReadOwnedMenu(string keyPath, RegistryKey key)
        {
            if ((key.GetValue(OwnerName) as string) != OwnerId)
                throw new InvalidOperationException("HKCU\\" + keyPath + " 已由其他配置使用，未覆盖或移除任何菜单项。");
            string executable = ReadString(key, "WinUnlinkExecutable", keyPath);
            string backupPath = ReadString(key, "WinUnlinkBackup", keyPath);
            object marker = key.GetValue("WinUnlinkTemporary", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            bool temporary = marker != null;
            if (temporary && (!(marker is string) || (string)marker != "1"))
                throw new InvalidDataException("菜单有效期标记不完整，保持现有配置：HKCU\\" + keyPath);
            Dictionary<string, string> expected = ExpectedValues(executable, backupPath, temporary);
            string[] names = key.GetValueNames();
            string[] subkeys = key.GetSubKeyNames();
            if (names.Length != expected.Count || subkeys.Length != 1 ||
                !String.Equals(subkeys[0], "command", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("菜单项含有安装后加入或删除的配置，保持这些修改：HKCU\\" + keyPath);
            foreach (KeyValuePair<string, string> value in expected)
                if (key.GetValueKind(value.Key) != RegistryValueKind.String ||
                    (string)key.GetValue(value.Key, null, RegistryValueOptions.DoNotExpandEnvironmentNames) != value.Value)
                    throw new InvalidOperationException("菜单项已经被修改，保持现有配置：HKCU\\" + keyPath);
            using (RegistryKey command = key.OpenSubKey("command"))
                if (command == null || command.GetValueNames().Length != 1 || command.GetSubKeyNames().Length != 0 ||
                    command.GetValueKind("") != RegistryValueKind.String ||
                    (string)command.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) != Command(executable))
                    throw new InvalidOperationException("菜单启动命令已经被修改，保持现有配置：HKCU\\" + keyPath);
            BackupFacts backup = ReadBackup(keyPath, backupPath, BackupDirectoryFor(executable, temporary));
            return new MenuFacts(executable, backup, temporary);
        }

        private static string ReadString(RegistryKey key, string name, string keyPath)
        {
            string value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (String.IsNullOrEmpty(value))
                throw new InvalidDataException("菜单信息不完整，保持现有配置：HKCU\\" + keyPath);
            return value;
        }

        private static BackupFacts ReadBackup(string keyPath, string backupPath, string selectedDirectory)
        {
            string fullBackup = Path.GetFullPath(backupPath);
            string prefix = Path.GetFullPath(selectedDirectory).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullBackup.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("备份不在本工具的备份目录内，保持所有菜单项：" + fullBackup);
            using (XmlReader reader = XmlReader.Create(fullBackup,
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            {
                XElement snapshot = XElement.Load(reader);
                if (snapshot.Name != "registry-backup" || (string)snapshot.Attribute("key") != keyPath ||
                    snapshot.Attribute("existed") == null)
                    throw new InvalidDataException("菜单备份格式不匹配，保持所有菜单项：" + backupPath);
                bool existed;
                try { existed = (bool)snapshot.Attribute("existed"); }
                catch (FormatException error) { throw new InvalidDataException("菜单备份的原始状态无效：" + backupPath, error); }
                if (existed)
                    throw new InvalidDataException("备份声明原先已有其他配置，此版本不能恢复该状态；保持所有菜单项：" + backupPath);
                return new BackupFacts(backupPath);
            }
        }

        private static BackupFacts CreateBackup(string keyPath, string directory)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "context-menu-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
                Guid.NewGuid().ToString("N") + ".xml");
            XElement snapshot = new XElement("registry-backup", new XAttribute("key", keyPath), new XAttribute("existed", false));
            bool created = false;
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                {
                    created = true;
                    using (XmlWriter writer = XmlWriter.Create(stream,
                        new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
                        snapshot.Save(writer);
                }
            }
            catch (Exception error)
            {
                if (created)
                {
                    try { File.Delete(path); }
                    catch (Exception cleanupError)
                    {
                        throw new IOException("菜单备份写入失败，部分备份清理未完成，请保留并检查：" + path +
                            "。写入原因：" + error.Message + "；清理原因：" + cleanupError.Message, error);
                    }
                }
                throw new IOException("菜单备份创建失败：" + path + "。原因：" + error.Message, error);
            }
            return new BackupFacts(path);
        }

        private static void WriteMenu(string keyPath, MenuFacts facts)
        {
            RegistryOptions options = facts.Temporary ? RegistryOptions.Volatile : RegistryOptions.None;
            using (RegistryKey menu = Registry.CurrentUser.CreateSubKey(keyPath, RegistryKeyPermissionCheck.ReadWriteSubTree, options))
            {
                if (menu == null) throw new IOException("无法创建菜单项：HKCU\\" + keyPath);
                // Stamp ownership before other values so rollback recognizes
                // a partial write without treating validity as ownership.
                menu.SetValue(OwnerName, OwnerId, RegistryValueKind.String);
                foreach (KeyValuePair<string, string> value in facts.Values)
                    if (value.Key != OwnerName) menu.SetValue(value.Key, value.Value, RegistryValueKind.String);
                using (RegistryKey command = menu.CreateSubKey("command", RegistryKeyPermissionCheck.ReadWriteSubTree, options))
                {
                    if (command == null) throw new IOException("无法创建菜单命令：HKCU\\" + keyPath);
                    command.SetValue("", facts.Command, RegistryValueKind.String);
                }
            }
        }

        private static IOException RollbackFailure(List<MenuItem> items, string operation, Exception originalError)
        {
            List<string> outcomes = new List<string>();
            foreach (MenuItem item in items.AsEnumerable().Reverse())
            {
                if (!item.MutationStarted) { outcomes.Add("HKCU\\" + item.KeyPath + "：未修改。"); continue; }
                try
                {
                    using (RegistryKey current = Registry.CurrentUser.OpenSubKey(item.KeyPath))
                        if (current != null) VerifyRecoverySubset(item, current);
                    if (item.Original == null) Registry.CurrentUser.DeleteSubKeyTree(item.KeyPath, false);
                    else WriteMenu(item.KeyPath, item.Original);
                    outcomes.Add("HKCU\\" + item.KeyPath + "：已回滚到操作前状态。");
                }
                catch (Exception recoveryError)
                {
                    outcomes.Add("HKCU\\" + item.KeyPath + "：回滚未完成，保持当前内容，请检查此项。原因：" + recoveryError.Message);
                }
            }
            NotifyExplorer();
            string backups = String.Join("；", items.Where(i => i.Desired != null).Select(i => i.Desired.Backup.Path));
            return new IOException(operation + "，不能视为全部成功。原因：" + originalError.Message +
                "\r\n" + String.Join("\r\n", outcomes) + "\r\n配置备份仍保留：" + backups, originalError);
        }

        // Recovery accepts only old/new values this operation could have
        // written or removed. Foreign values and user edits are preserved.
        private static void VerifyRecoverySubset(MenuItem item, RegistryKey key)
        {
            if ((key.GetValue(OwnerName) as string) != OwnerId)
                throw new InvalidOperationException("当前项的所有权无法确认，未覆盖或删除。");
            foreach (string name in key.GetValueNames())
            {
                object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (key.GetValueKind(name) != RegistryValueKind.String || !(value is string) ||
                    !(AllowedValue(item.Original, name, (string)value) || AllowedValue(item.Desired, name, (string)value)))
                    throw new InvalidOperationException("当前项存在非本次操作产生的修改，未覆盖或删除。");
            }
            string[] subkeys = key.GetSubKeyNames();
            if (subkeys.Any(n => !String.Equals(n, "command", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("当前项存在额外子项，未覆盖或删除。");
            using (RegistryKey command = key.OpenSubKey("command"))
            {
                if (command == null) return;
                if (command.GetSubKeyNames().Length != 0 || command.GetValueNames().Any(n => n != ""))
                    throw new InvalidOperationException("当前命令存在额外配置，未覆盖或删除。");
                if (command.GetValueNames().Length == 0) return;
                object value = command.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (command.GetValueKind("") != RegistryValueKind.String || !(value is string) ||
                    !((item.Original != null && item.Original.Command == (string)value) ||
                      (item.Desired != null && item.Desired.Command == (string)value)))
                    throw new InvalidOperationException("当前命令存在非本次操作产生的修改，未覆盖或删除。");
            }
        }

        private static bool AllowedValue(MenuFacts facts, string name, string value)
        {
            string expected;
            return facts != null && facts.Values.TryGetValue(name, out expected) && expected == value;
        }

        private static MenuRegistrationResult Result(List<MenuItem> items, bool changed, string message)
        {
            string[] backups = items.Select(i => i.Desired ?? i.Original).Where(f => f != null).Select(f => f.Backup.Path).ToArray();
            return new MenuRegistrationResult { Message = message, Changed = changed,
                BackupPath = backups.FirstOrDefault(), BackupPaths = backups };
        }

        private string BackupDirectoryFor(string executablePath, bool temporary)
        {
            return temporary ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executablePath)), "temporary-menu-backups") : backupDirectory;
        }

        private static Dictionary<string, string> ExpectedValues(string executablePath, string backupPath, bool temporary)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { "", "WinUnlink：删除或替换…" }, { "Icon", "\"" + executablePath + "\",0" },
                { "MultiSelectModel", "Single" }, { OwnerName, OwnerId },
                { "WinUnlinkExecutable", executablePath }, { "WinUnlinkBackup", backupPath }
            };
            if (temporary) values.Add("WinUnlinkTemporary", "1");
            return values;
        }

        private static string Command(string executablePath) { return "\"" + executablePath + "\" --inspect \"%1\""; }

        private sealed class BackupFacts
        {
            internal readonly string Path;
            internal BackupFacts(string path) { Path = path; }
        }

        private sealed class MenuFacts
        {
            internal readonly string Executable;
            internal readonly BackupFacts Backup;
            internal readonly bool Temporary;
            internal readonly Dictionary<string, string> Values;
            internal readonly string Command;
            internal MenuFacts(string executable, BackupFacts backup, bool temporary)
            {
                Executable = executable;
                Backup = backup;
                Temporary = temporary;
                Values = ExpectedValues(executable, backup.Path, temporary);
                Command = ContextMenuRegistration.Command(executable);
            }
        }

        private sealed class MenuItem
        {
            internal readonly string KeyPath;
            internal MenuFacts Original;
            internal MenuFacts Desired;
            internal bool NeedsChange;
            internal bool MutationStarted;
            internal MenuItem(string keyPath) { KeyPath = keyPath; }
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
        private static void NotifyExplorer() { SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); }
    }
}
