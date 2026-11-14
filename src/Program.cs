using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Forms;

namespace WinUnlink
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e) {
                MessageBox.Show(e.Exception.Message, "WinUnlink 操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            try
            {
                if (args.Length != 0 && args[0].StartsWith("--", StringComparison.Ordinal) && args[0] != "--inspect")
                {
                    // A WinExe may have redirected pipes without an attached console.
                    // Set writers directly; changing the console code page can fail.
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
                    Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
                }
                if (args.Length != 0 && args[0] == "--scan") return ScanCommand(args);
                if (args.Length != 0 && (args[0] == "--install-menu" || args[0] == "--install-menu-temporary" || args[0] == "--uninstall-menu")) return MenuCommand(args);
                if (args.Length != 0 && args[0] == "--help")
                {
                    WriteReport(args, new CommandReport { Success = true, Message =
                        "WinUnlink.exe [--inspect FILE] [--replacement SOURCE]\n" +
                        "WinUnlink.exe --scan FILE [--report JSON]\n" +
                        "WinUnlink.exe --install-menu --yes [--report JSON]\n" +
                        "WinUnlink.exe --install-menu-temporary --yes [--report JSON]\n" +
                        "WinUnlink.exe --uninstall-menu --yes [--report JSON]\n" +
                        "删除与替换在窗口内确认；扫描命令只读取文件和占用进程。" }); return 0;
                }
                string path = null;
                string source = null;
                if (args.Length > 0)
                {
                    if (args[0] == "--inspect" && args.Length >= 2) path = args[1];
                    else if (!args[0].StartsWith("--", StringComparison.Ordinal) && args.Length == 1) path = args[0];
                    else throw new ArgumentException("未知参数。使用 --help 查看用法。");
                    source = Option(args, "--replacement");
                }
                Application.Run(new MainForm(path, source));
                string cleanup = LockInspector.RetryCleanup();
                if (cleanup != null) MessageBox.Show(cleanup, "占用查询清理未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 0;
            }
            catch (Exception error)
            {
                if (args.Length != 0 && args[0] != "--inspect" && args[0].StartsWith("--", StringComparison.Ordinal))
                {
                    try { WriteReport(args, new CommandReport { Success = false, Message = error.Message }); }
                    catch (Exception reportingError) { Console.Error.WriteLine(error.Message + "\n报告失败：" + reportingError.Message); }
                }
                else MessageBox.Show(error.Message, "WinUnlink", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }

        private static int ScanCommand(string[] args)
        {
            if (args.Length < 2) throw new ArgumentException("--scan 后需要文件路径。");
            FileSnapshot target = FileOperations.Inspect(args[1]);
            LockScanResult result = LockInspector.Scan(target.Path);
            WriteReport(args, new CommandReport { Success = result.IsComplete, Message = result.Error ??
                "查询完成。此列表只覆盖 Restart Manager 报告结果，不能证明不存在其他占用。",
                Target = target.Path, Identity = target.Identity, Processes = result.Processes.Select(p => new ProcessReport {
                    Pid = p.ProcessId, Name = p.Name, Path = p.ImagePath, StartTimeFileTime = p.StartTimeFileTime,
                    Type = p.Type, CanTerminate = p.CanTerminate, Restriction = p.TerminationRestriction
                }).ToList() });
            return result.IsComplete ? 0 : 1;
        }

        private static int MenuCommand(string[] args)
        {
            ContextMenuRegistration registration = new ContextMenuRegistration();
            bool temporary = args[0] == "--install-menu-temporary";
            string reportPath = Option(args, "--report");
            // Validate and reserve the report destination before registry changes.
            using (FileStream reportFile = reportPath == null ? null :
                new FileStream(Path.GetFullPath(reportPath), FileMode.CreateNew, FileAccess.Write))
            {
                if (!args.Contains("--yes"))
                {
                    WriteReport(args, new CommandReport { Success = false, Message = registration.Plan(Application.ExecutablePath, temporary) +
                        "\n\n确认写入时加 --yes，或在窗口内使用菜单按钮。" }, reportFile); return 2;
                }
                MenuRegistrationResult result;
                try
                {
                    result = args[0] == "--uninstall-menu" ? registration.Uninstall() : registration.Install(Application.ExecutablePath, temporary);
                }
                catch (Exception error)
                {
                    WriteReport(args, new CommandReport { Success = false, Message = error.Message }, reportFile); return 1;
                }
                try
                {
                    WriteReport(args, new CommandReport { Success = true, Message = result.Message,
                        BackupPath = result.BackupPath, Changed = result.Changed }, reportFile); return 0;
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(result.Message + "\n菜单操作已完成，但报告写入失败：" + error.Message +
                        "\n备份：" + result.BackupPath); return 3;
                }
            }
        }

        private static string Option(string[] args, string name)
        {
            int index = Array.IndexOf(args, name);
            if (index < 0) return null;
            if (index + 1 == args.Length) throw new ArgumentException(name + " 后缺少路径。");
            return args[index + 1];
        }

        private static void WriteReport(string[] args, CommandReport report, Stream reservedReport = null)
        {
            string path = Option(args, "--report");
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(CommandReport));
            using (MemoryStream buffer = new MemoryStream())
            {
                serializer.WriteObject(buffer, report);
                string json = Encoding.UTF8.GetString(buffer.ToArray());
                if (reservedReport != null)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(json); reservedReport.Write(bytes, 0, bytes.Length); reservedReport.Flush();
                }
                else if (path == null) Console.WriteLine(json);
                else
                {
                    // Reports never overwrite an existing caller file.
                    using (FileStream file = new FileStream(Path.GetFullPath(path), FileMode.CreateNew, FileAccess.Write))
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(json); file.Write(bytes, 0, bytes.Length);
                    }
                }
            }
        }
    }

    [DataContract]
    internal sealed class CommandReport
    {
        [DataMember] public bool Success { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember(EmitDefaultValue = false)] public string Target { get; set; }
        [DataMember(EmitDefaultValue = false)] public string Identity { get; set; }
        [DataMember(EmitDefaultValue = false)] public List<ProcessReport> Processes { get; set; }
        [DataMember(EmitDefaultValue = false)] public string BackupPath { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool Changed { get; set; }
    }
    [DataContract]
    internal sealed class ProcessReport
    {
        [DataMember] public int Pid { get; set; }
        [DataMember] public long StartTimeFileTime { get; set; }
        [DataMember] public string Name { get; set; }
        [DataMember(EmitDefaultValue = false)] public string Path { get; set; }
        [DataMember] public string Type { get; set; }
        [DataMember] public bool CanTerminate { get; set; }
        [DataMember(EmitDefaultValue = false)] public string Restriction { get; set; }
    }
}
