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
        public bool Changed { get; set; }
    }

    public sealed class ContextMenuRegistration
    {
        public const string MenuKeyPath = @"Software\Classes\*\shell\WinUnlink.FileTools";
        private const string OwnerId = "87cf7d33-ef74-424b-bf25-10aa3b780cd1";
        private const string OwnerName = "WinUnlinkOwner";
        private readonly string keyPath;
        private readonly string backupDirectory;

        public ContextMenuRegistration() : this(MenuKeyPath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinUnlink", "backups")) { }

        // The alternate root is used only for isolated registry integration tests.
        internal ContextMenuRegistration(string registryKeyPath, string backups)
        {
            keyPath = registryKeyPath;
            backupDirectory = backups;
        }

        public string Plan(string executablePath, bool temporary = false)
        {
            return "给当前用户的文件右键菜单添加“WinUnlink：删除或替换”。\r\n\r\n" +
                "注册表：HKCU\\" + keyPath + "\r\n程序：" + executablePath +
                "\r\n备份目录：" + BackupDirectoryFor(executablePath, temporary) +
                (temporary ? "\r\n\r\n临时菜单仅保存在内存中，随用户注册表卸载而消失；重启会清除，也可手动移除。关闭程序不会移除菜单。" : "") +
                "\r\n\r\nWin11 中入口位于“显示更多选项”。程序移动后需重新注册。";
        }

        public MenuRegistrationResult Install(string executablePath, bool temporary = false)
        {
            executablePath = Path.GetFullPath(executablePath);
            if (!File.Exists(executablePath))
                throw new IOException("注册的程序路径不存在：" + executablePath);

            string selectedBackupDirectory = BackupDirectoryFor(executablePath, temporary);
            string backupPath;
            using (RegistryKey existing = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                if (existing != null)
                {
                    EnsureOwnedAndUnmodified(existing);
                    if (IsTemporary(existing) != temporary)
                        throw new InvalidOperationException("已有菜单的有效期与本次请求不同。请先移除已有菜单，再重新启用；未修改现有配置。");
                    backupPath = (string)existing.GetValue("WinUnlinkBackup", null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames);
                    ValidateBackup(backupPath, BackupDirectoryFor((string)existing.GetValue("WinUnlinkExecutable"), temporary));
                    string oldExecutable = (string)existing.GetValue("WinUnlinkExecutable");
                    if (temporary && !String.Equals(oldExecutable, executablePath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("临时菜单绑定本次程序位置。请先移除已有菜单，再从新位置临时启用；未修改现有配置。");
                    if (String.Equals(oldExecutable, executablePath, StringComparison.OrdinalIgnoreCase))
                        return new MenuRegistrationResult { Message = "右键菜单已经注册，无需修改。",
                            BackupPath = backupPath, Changed = false };
                }
                else
                {
                    Directory.CreateDirectory(selectedBackupDirectory);
                    backupPath = Path.Combine(selectedBackupDirectory,
                        "context-menu-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                        "-" + Guid.NewGuid().ToString("N") + ".xml");
                    XElement snapshot = new XElement("registry-backup",
                        new XAttribute("key", keyPath), new XAttribute("existed", false));
                    using (FileStream stream = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write))
                    using (XmlWriter writer = XmlWriter.Create(stream,
                        new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
                        snapshot.Save(writer);
                }
            }

            try
            {
                RegistryOptions options = temporary ? RegistryOptions.Volatile : RegistryOptions.None;
                using (RegistryKey menu = Registry.CurrentUser.CreateSubKey(keyPath, RegistryKeyPermissionCheck.ReadWriteSubTree, options))
                {
                    foreach (KeyValuePair<string, string> value in ExpectedValues(executablePath, backupPath, temporary))
                        menu.SetValue(value.Key, value.Value, RegistryValueKind.String);
                    using (RegistryKey command = menu.CreateSubKey("command", RegistryKeyPermissionCheck.ReadWriteSubTree, options))
                        command.SetValue("", Command(executablePath), RegistryValueKind.String);
                }
            }
            catch (Exception error)
            {
                throw new IOException("右键菜单写入未完成。配置备份保留在 " + backupPath +
                    "；请检查 HKCU\\" + keyPath + "。原因：" + error.Message, error);
            }
            NotifyExplorer();
            return new MenuRegistrationResult { Message = temporary ? "已临时启用当前用户的文件右键菜单。重启后失效，也可立即手动移除。" : "已注册当前用户的文件右键菜单。",
                BackupPath = backupPath, Changed = true };
        }

        public MenuRegistrationResult Uninstall()
        {
            string backupPath;
            string installedExecutable;
            bool temporary;
            using (RegistryKey existing = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                if (existing == null)
                    return new MenuRegistrationResult { Message = "右键菜单尚未注册。", Changed = false };
                EnsureOwnedAndUnmodified(existing);
                backupPath = (string)existing.GetValue("WinUnlinkBackup");
                installedExecutable = (string)existing.GetValue("WinUnlinkExecutable");
                temporary = IsTemporary(existing);
            }
            XElement snapshot = ValidateBackup(backupPath, BackupDirectoryFor(installedExecutable, temporary));
            if ((bool)snapshot.Attribute("existed"))
                throw new InvalidDataException("此版本只恢复由本工具创建的独立菜单项；备份声明已有其他配置，保持原状。备份：" + backupPath);
            Registry.CurrentUser.DeleteSubKeyTree(keyPath);
            NotifyExplorer();
            return new MenuRegistrationResult { Message = "已移除右键菜单；原有注册表状态已恢复，备份文件仍保留。",
                BackupPath = backupPath, Changed = true };
        }

        private XElement ValidateBackup(string backupPath, string selectedBackupDirectory)
        {
            if (String.IsNullOrEmpty(backupPath))
                throw new InvalidDataException("菜单备份路径缺失，保持现有配置。请检查 HKCU\\" + keyPath);
            string fullBackup = Path.GetFullPath(backupPath);
            string directoryPrefix = Path.GetFullPath(selectedBackupDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullBackup.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("备份不在本工具的备份目录内，保持现有配置。" + fullBackup);
            using (XmlReader reader = XmlReader.Create(fullBackup,
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            {
                XElement snapshot = XElement.Load(reader);
                if (snapshot.Name != "registry-backup" || (string)snapshot.Attribute("key") != keyPath ||
                    snapshot.Attribute("existed") == null)
                    throw new InvalidDataException("菜单备份格式不匹配，保持现有配置：" + backupPath);
                return snapshot;
            }
        }

        private void EnsureOwnedAndUnmodified(RegistryKey key)
        {
            if ((string)key.GetValue(OwnerName) != OwnerId)
                throw new InvalidOperationException("HKCU\\" + keyPath + " 已由其他配置使用，未覆盖。请保留该项并检查冲突。");
            string executablePath = key.GetValue("WinUnlinkExecutable") as string;
            string backupPath = key.GetValue("WinUnlinkBackup") as string;
            if (String.IsNullOrEmpty(executablePath) || String.IsNullOrEmpty(backupPath))
                throw new InvalidDataException("已注册的菜单信息不完整，保持现有配置。请检查 HKCU\\" + keyPath);
            Dictionary<string, string> expected = ExpectedValues(executablePath, backupPath, IsTemporary(key));
            if (key.GetValueNames().Length != expected.Count ||
                key.GetSubKeyNames().Length != 1 || key.GetSubKeyNames()[0] != "command")
                throw new InvalidOperationException("菜单项含有安装后加入的配置，保持这些修改。请检查 HKCU\\" + keyPath);
            foreach (KeyValuePair<string, string> value in expected)
            {
                if (key.GetValueKind(value.Key) != RegistryValueKind.String ||
                    (string)key.GetValue(value.Key, null, RegistryValueOptions.DoNotExpandEnvironmentNames) != value.Value)
                    throw new InvalidOperationException("菜单项已经被修改，保持现有配置。请检查 HKCU\\" + keyPath);
            }
            using (RegistryKey command = key.OpenSubKey("command"))
            {
                if (command == null || command.GetValueNames().Length != 1 || command.GetSubKeyNames().Length != 0 ||
                    command.GetValueKind("") != RegistryValueKind.String || (string)command.GetValue("") != Command(executablePath))
                    throw new InvalidOperationException("菜单启动命令已经被修改，保持现有配置。请检查 HKCU\\" + keyPath);
            }
        }

        private string BackupDirectoryFor(string executablePath, bool temporary)
        {
            return temporary ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executablePath)), "temporary-menu-backups") : backupDirectory;
        }

        private static bool IsTemporary(RegistryKey key)
        {
            object value = key.GetValue("WinUnlinkTemporary", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value == null) return false;
            if (!(value is string) || (string)value != "1")
                throw new InvalidDataException("菜单有效期标记不完整，保持现有配置。");
            return true;
        }

        private static Dictionary<string, string> ExpectedValues(string executablePath, string backupPath, bool temporary)
        {
            Dictionary<string, string> values = new Dictionary<string, string> {
                { "", "WinUnlink：删除或替换…" }, { "Icon", "\"" + executablePath + "\",0" },
                { "MultiSelectModel", "Single" }, { OwnerName, OwnerId },
                { "WinUnlinkExecutable", executablePath }, { "WinUnlinkBackup", backupPath }
            };
            if (temporary) values.Add("WinUnlinkTemporary", "1");
            return values;
        }

        private static string Command(string executablePath)
        {
            return "\"" + executablePath + "\" --inspect \"%1\"";
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
        private static void NotifyExplorer()
        {
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
