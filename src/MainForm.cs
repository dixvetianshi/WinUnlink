using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WinUnlink
{
    public sealed class MainForm : Form
    {
        private readonly TextBox targetBox = new TextBox();
        private readonly TextBox replacementBox = new TextBox();
        private readonly Label fileInfo = new Label();
        private readonly Label scanInfo = new Label();
        private readonly DataGridView processGrid = new DataGridView();
        private readonly TextBox logBox = new TextBox();
        private readonly List<Control> workControls = new List<Control>();
        private readonly List<Control> fileOnlyControls = new List<Control>();
        private readonly ComboBox deleteModeBox = new ComboBox();
        private Button rebootDeleteButton;
        private readonly string initialPath;
        private TargetSnapshot snapshot;
        private LockScanResult scan = new LockScanResult();
        private bool busy;

        public MainForm(string path, string source = null)
        {
            initialPath = path;
            replacementBox.Text = source ?? "";
            Text = "WinUnlink · 文件及文件夹占用、删除与替换";
            Font = new Font("Microsoft YaHei UI", 9.5F);
            BackColor = Color.FromArgb(246, 248, 251);
            ClientSize = new Size(1040, 750);
            MinimumSize = new Size(920, 690);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;

            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill,
                Padding = new Padding(24, 18, 24, 16), ColumnCount = 1, RowCount = 9 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            Controls.Add(layout);

            Panel header = new Panel { Dock = DockStyle.Fill };
            header.Controls.Add(new Label { Text = "WinUnlink", AutoSize = true,
                Font = new Font("Segoe UI", 24, FontStyle.Bold), ForeColor = Color.FromArgb(30, 55, 85), Location = new Point(0, 0) });
            header.Controls.Add(new Label { Text = "删除或替换被占用的文件、文件夹  ·  Windows 10 / 11",
                AutoSize = true, ForeColor = Color.FromArgb(83, 97, 114), Location = new Point(3, 49) });
            layout.Controls.Add(header, 0, 0);

            layout.Controls.Add(PathRow("目标路径", targetBox, "选择目标…", ChooseTarget, true), 0, 1);
            layout.Controls.Add(PathRow("替换来源", replacementBox, "选择来源…", ChooseReplacement, false), 0, 2);
            fileInfo.Dock = DockStyle.Fill;
            fileInfo.ForeColor = Color.FromArgb(74, 87, 103);
            fileInfo.Text = "选择文件或文件夹，或将它拖到窗口。替换来源会保留。";
            layout.Controls.Add(fileInfo, 0, 3);
            scanInfo.Dock = DockStyle.Fill;
            scanInfo.Text = "占用进程";
            scanInfo.Font = new Font(Font, FontStyle.Bold);
            layout.Controls.Add(scanInfo, 0, 4);

            processGrid.Dock = DockStyle.Fill;
            processGrid.BackgroundColor = Color.White;
            processGrid.BorderStyle = BorderStyle.FixedSingle;
            processGrid.AutoGenerateColumns = false;
            processGrid.AllowUserToAddRows = false;
            processGrid.AllowUserToDeleteRows = false;
            processGrid.AllowUserToResizeRows = false;
            processGrid.RowHeadersVisible = false;
            processGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            processGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            processGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "selected", HeaderText = "选择", FillWeight = 12 });
            processGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "进程名称", FillWeight = 35, ReadOnly = true });
            processGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "pid", HeaderText = "PID", FillWeight = 14, ReadOnly = true });
            processGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "type", HeaderText = "类型", FillWeight = 18, ReadOnly = true });
            processGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "path", HeaderText = "程序路径 / 状态", FillWeight = 90, ReadOnly = true });
            processGrid.CurrentCellDirtyStateChanged += delegate {
                if (processGrid.IsCurrentCellDirty) processGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            layout.Controls.Add(processGrid, 0, 5);
            workControls.Add(processGrid);

            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 0), WrapContents = true };
            Panel deletionOptions = new Panel { Width = 820, Height = 30, Margin = new Padding(0, 0, 0, 8) };
            deletionOptions.Controls.Add(new Label { Text = "删除方式", AutoSize = true, Location = new Point(0, 5) });
            deleteModeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            deleteModeBox.Items.AddRange(new object[] { "移到回收站（默认）", "直接删除（永久）" });
            deleteModeBox.SelectedIndex = 0;
            deleteModeBox.Location = new Point(83, 0); deleteModeBox.Width = 196;
            deletionOptions.Controls.Add(deleteModeBox);
            deletionOptions.Controls.Add(new Label { Text = "仅影响删除；替换会永久移除旧目标内容。", AutoSize = true,
                ForeColor = Color.FromArgb(83, 97, 114), Location = new Point(294, 5) });
            workControls.Add(deleteModeBox);
            actions.Controls.Add(deletionOptions);
            actions.SetFlowBreak(deletionOptions, true);
            actions.Controls.Add(ActionButton("删除目标", Color.FromArgb(180, 57, 55), async delegate { await OperateAsync(false, false); }));
            actions.Controls.Add(ActionButton("停止并删除", Color.FromArgb(155, 77, 33), async delegate { await StopAndDeleteAsync(); }));
            actions.Controls.Add(ActionButton("替换目标", Color.FromArgb(41, 103, 168), async delegate { await OperateAsync(true, false); }));
            actions.Controls.Add(ActionButton("结束选中进程", Color.FromArgb(97, 108, 126), async delegate { await TerminateSelectedAsync(); }));
            Button rebootDelete = ActionButton("重启后删除", Color.FromArgb(97, 108, 126), async delegate { await OperateAsync(false, true); });
            Button rebootReplace = ActionButton("重启后替换", Color.FromArgb(97, 108, 126), async delegate { await OperateAsync(true, true); });
            actions.Controls.Add(rebootDelete); actions.Controls.Add(rebootReplace);
            fileOnlyControls.Add(rebootDelete); fileOnlyControls.Add(rebootReplace);
            rebootDeleteButton = rebootDelete;
            deleteModeBox.SelectedIndexChanged += delegate { UpdateDeletionOptions(); };
            UpdateDeletionOptions();
            actions.Controls.Add(new Label { AutoSize = true, ForeColor = Color.FromArgb(83, 97, 114),
                Text = "“停止并删除”会结束占用程序，再按所选方式处理目标。回收失败不会改为永久删除。", Margin = new Padding(0, 9, 0, 0) });
            layout.Controls.Add(actions, 0, 6);

            logBox.Dock = DockStyle.Fill;
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BackColor = Color.White;
            logBox.Text = "执行前会弹窗显示占用进程名称与 PID。Restart Manager 查询可能漏掉受保护进程、驱动或新出现的占用。";
            layout.Controls.Add(logBox, 0, 7);

            FlowLayoutPanel footer = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 5, 0, 0) };
            footer.Controls.Add(Link("注册右键菜单", RegisterMenu));
            footer.Controls.Add(Link("移除右键菜单", RemoveMenu));
            footer.Controls.Add(Link("以管理员重开", RelaunchElevated));
            footer.Controls.Add(new Label { Text = "Win11：显示更多选项  ·  文件 / 文件夹", AutoSize = true,
                Margin = new Padding(18, 1, 0, 0), ForeColor = Color.FromArgb(99, 110, 124) });
            layout.Controls.Add(footer, 0, 8);

            targetBox.TextChanged += delegate {
                if (!busy) { snapshot = null; scan = new LockScanResult(); processGrid.Rows.Clear();
                    scanInfo.Text = "占用进程 · 点击查询以刷新"; fileInfo.Text = "目标已变化，请查询占用以获取文件信息。"; }
            };
            Shown += async delegate {
                if (!String.IsNullOrEmpty(initialPath)) { targetBox.Text = initialPath; await RefreshAsync(); }
            };
            DragEnter += delegate(object sender, DragEventArgs e) {
                e.Effect = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            DragDrop += async delegate(object sender, DragEventArgs e) {
                string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (!busy && paths != null && paths.Length == 1) { targetBox.Text = paths[0]; await RefreshAsync(); }
                else if (paths != null && paths.Length != 1) MessageBox.Show(this, "每次请选择一个目标文件或文件夹。", "WinUnlink");
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (busy) { e.Cancel = true; scanInfo.Text = "正在完成操作，请稍候…"; }
            };
        }

        private Control PathRow(string label, TextBox box, string browseLabel, EventHandler choose, bool includeRefresh)
        {
            TableLayoutPanel row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = includeRefresh ? 4 : 3, Padding = new Padding(0, 3, 0, 6) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
            if (includeRefresh) row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            row.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(0, 4, 8, 0);
            row.Controls.Add(box, 1, 0);
            Button browse = new Button { Text = browseLabel, Dock = DockStyle.Fill, FlatStyle = FlatStyle.System };
            browse.Click += choose;
            row.Controls.Add(browse, 2, 0);
            workControls.Add(box); workControls.Add(browse);
            if (includeRefresh)
            {
                Button refresh = new Button { Text = "查询占用", Dock = DockStyle.Fill, FlatStyle = FlatStyle.System };
                refresh.Click += async delegate { await RefreshAsync(); };
                row.Controls.Add(refresh, 3, 0); workControls.Add(refresh);
                box.KeyDown += async delegate(object sender, KeyEventArgs e) {
                    if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await RefreshAsync(); }
                };
            }
            return row;
        }

        private Button ActionButton(string text, Color color, EventHandler action)
        {
            Button button = new Button { Text = text, AutoSize = false, Size = new Size(125, 35),
                Margin = new Padding(0, 0, 8, 0), FlatStyle = FlatStyle.Flat, BackColor = color, ForeColor = Color.White };
            button.FlatAppearance.BorderSize = 0;
            button.Click += action; workControls.Add(button); return button;
        }

        private LinkLabel Link(string text, EventHandler action)
        {
            LinkLabel link = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 0, 18, 0), LinkColor = Color.FromArgb(41, 103, 168) };
            link.Click += action; workControls.Add(link); return link;
        }

        private void ChooseTarget(object sender, EventArgs e)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("选择文件…", null, async delegate {
                using (OpenFileDialog dialog = new OpenFileDialog { Title = "选择目标文件", CheckFileExists = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) { targetBox.Text = dialog.FileName; await RefreshAsync(); }
            });
            menu.Items.Add("选择文件夹…", null, async delegate {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "选择要删除或整体替换的文件夹", ShowNewFolderButton = false })
                    if (dialog.ShowDialog(this) == DialogResult.OK) { targetBox.Text = dialog.SelectedPath; await RefreshAsync(); }
            });
            menu.Closed += delegate { menu.Dispose(); };
            Control control = sender as Control;
            if (control != null) menu.Show(control, new Point(0, control.Height));
        }

        private void ChooseReplacement(object sender, EventArgs e)
        {
            if (snapshot != null && snapshot.IsDirectory)
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "选择替换来源文件夹（来源会保留）", ShowNewFolderButton = false })
                    if (dialog.ShowDialog(this) == DialogResult.OK) replacementBox.Text = dialog.SelectedPath;
                return;
            }
            using (OpenFileDialog dialog = new OpenFileDialog { Title = "选择新文件（来源会保留）", CheckFileExists = true })
                if (dialog.ShowDialog(this) == DialogResult.OK) replacementBox.Text = dialog.FileName;
        }

        private void SetBusy(bool value)
        {
            busy = value;
            foreach (Control control in workControls) control.Enabled = !value;
            if (!value && snapshot != null && snapshot.IsDirectory)
                foreach (Control control in fileOnlyControls) control.Enabled = false;
            UpdateDeletionOptions();
            UseWaitCursor = value;
        }

        private DeleteMode SelectedDeleteMode
        {
            get { return deleteModeBox.SelectedIndex == 1 ? DeleteMode.Permanent : DeleteMode.RecycleBin; }
        }

        private void UpdateDeletionOptions()
        {
            if (rebootDeleteButton != null)
                rebootDeleteButton.Enabled = !busy && SelectedDeleteMode == DeleteMode.Permanent &&
                    (snapshot == null || !snapshot.IsDirectory);
        }

        private async Task<bool> RefreshAsync()
        {
            if (busy) return false;
            string requestedPath = targetBox.Text;
            SetBusy(true);
            scanInfo.Text = "正在查询占用进程…";
            try
            {
                TargetSnapshot fresh = await Task.Run(delegate { return TargetOperations.Inspect(requestedPath); });
                LockScanResult freshScan = await Task.Run(delegate { return TargetOperations.Scan(fresh); });
                snapshot = fresh; scan = freshScan;
                targetBox.Text = fresh.Root.Path;
                fileInfo.Text = (fresh.IsDirectory ? "文件夹：" + fresh.Directory.FileCount + " 个文件、" + fresh.Directory.DirectoryCount +
                    " 个目录，共 " + fresh.Directory.TotalBytes.ToString("N0") + " 字节" :
                    "大小：" + fresh.Root.Length.ToString("N0") + " 字节" + (fresh.Root.IsReadOnly ? "  ·  只读" : "")) +
                    "\r\n文件标识：" + fresh.Root.Identity;
                processGrid.Rows.Clear();
                foreach (LockingProcess process in scan.Processes)
                {
                    string details = process.ImagePath ?? process.InspectionError ?? process.ServiceName ?? "路径不可查询";
                    if (!process.CanTerminate) details += "  ·  " + process.TerminationRestriction;
                    int index = processGrid.Rows.Add(false, process.Name, process.ProcessId, process.Type, details);
                    DataGridViewRow row = processGrid.Rows[index]; row.Tag = process;
                    row.Cells["selected"].ReadOnly = !process.CanTerminate;
                    if (!process.CanTerminate) row.DefaultCellStyle.ForeColor = Color.FromArgb(119, 125, 135);
                    row.Cells["path"].ToolTipText = details;
                }
                scanInfo.Text = "占用进程 · " + scan.Processes.Count + " 个报告结果" + (scan.IsComplete ? "" : " · 查询未完成");
                if (!scan.IsComplete) AppendLog(scan.Error ?? "占用查询没有完成，不能据此判断文件没有占用。");
                return true;
            }
            catch (Exception error)
            {
                snapshot = null; scan = new LockScanResult(); processGrid.Rows.Clear();
                scanInfo.Text = "无法查询目标文件"; fileInfo.Text = error.Message;
                AppendLog(error.Message); return false;
            }
            finally { SetBusy(false); }
        }

        private async Task OperateAsync(bool replacement, bool reboot)
        {
            if (busy) return;
            if (replacement && String.IsNullOrWhiteSpace(replacementBox.Text))
            { MessageBox.Show(this, "先选择替换来源。文件夹会整体替换，来源会保留。", "WinUnlink"); return; }
            if (!await RefreshAsync()) return;
            TargetSnapshot selected = snapshot;
            string source = replacementBox.Text;
            DeleteMode deletionMode = SelectedDeleteMode;
            if (reboot && selected.IsDirectory)
            { MessageBox.Show(this, "文件夹暂不支持重启排队，请关闭占用后重试。", "WinUnlink"); return; }
            if (reboot && !replacement && deletionMode == DeleteMode.RecycleBin)
            { MessageBox.Show(this, "回收站模式不支持重启排队。需要永久删除时请手动选择“直接删除”。", "WinUnlink"); return; }
            string title = reboot ? (replacement ? "安排重启后替换" : "安排重启后删除") :
                (replacement ? "替换" : "删除") + (selected.IsDirectory ? "文件夹" : "文件");
            using (ConfirmationDialog dialog = new ConfirmationDialog(title, selected.Root.Path, replacement ? source : null, scan, reboot, false, selected.Directory, deletionMode))
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
            SetBusy(true);
            FileOperationResult result;
            try
            {
                result = await Task.Run(delegate {
                    if (reboot) return replacement ? TargetOperations.ScheduleReplace(selected, source) : TargetOperations.ScheduleDelete(selected, deletionMode);
                    return replacement ? TargetOperations.Replace(selected, source) : TargetOperations.Delete(selected, deletionMode);
                });
            }
            catch (Exception error) { result = new FileOperationResult { Status = OperationStatus.Failed, Message = error.Message }; }
            finally { SetBusy(false); }
            await ShowFileResultAsync(selected, result, replacement);
        }

        private async Task StopAndDeleteAsync()
        {
            if (busy || !await RefreshAsync()) return;
            TargetSnapshot selected = snapshot;
            DeleteMode deletionMode = SelectedDeleteMode;
            LockScanResult freshScan = scan;
            if (!freshScan.IsComplete)
            {
                MessageBox.Show(this, "占用查询未完成，不能自动停止程序并删除。请重新查询。\r\n" + freshScan.Error,
                    "停止并删除未执行", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            List<LockingProcess> processes = freshScan.Processes.Where(process => process.CanTerminate).ToList();
            using (ConfirmationDialog dialog = new ConfirmationDialog("停止并删除", selected.Root.Path, null, freshScan, false, true, selected.Directory, deletionMode))
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

            FileOperationResult fileResult;
            SetBusy(true);
            try
            {
                StopAndDeleteResult result = await Task.Run(delegate { return StopAndDelete.Execute(selected, processes, deletionMode); });
                foreach (ProcessStopResult outcome in result.Processes)
                    AppendLog(outcome.Process.Name + " (PID " + outcome.Process.ProcessId + ")：" +
                        (outcome.Stopped ? "已停止" : "未停止：" + outcome.Error));
                fileResult = result.FileResult;
            }
            catch (Exception error)
            {
                fileResult = new FileOperationResult { Status = OperationStatus.Failed,
                    Message = "停止并删除未完成：" + error.Message };
            }
            finally { SetBusy(false); }
            await ShowFileResultAsync(selected, fileResult, false);
        }

        private async Task ShowFileResultAsync(TargetSnapshot selected, FileOperationResult result, bool replacement)
        {
            string report = result.Message;
            if (!String.IsNullOrEmpty(result.RetainedPath)) report += "\r\n保留位置：" + result.RetainedPath;
            AppendLog(report);
            MessageBox.Show(this, report, result.Status == OperationStatus.Completed ? "操作已完成" :
                result.Status == OperationStatus.Scheduled ? "已安排重启后执行" : "操作结果",
                MessageBoxButtons.OK, result.Status == OperationStatus.Completed || result.Status == OperationStatus.Scheduled ?
                MessageBoxIcon.Information : MessageBoxIcon.Warning);
            if (result.Status == OperationStatus.Completed && !replacement)
            { snapshot = null; scan = new LockScanResult(); processGrid.Rows.Clear(); scanInfo.Text = "文件已删除"; fileInfo.Text = selected.Root.Path; }
            else await RefreshAsync();
        }

        private async Task TerminateSelectedAsync()
        {
            if (busy) return;
            processGrid.EndEdit();
            List<LockingProcess> selected = processGrid.Rows.Cast<DataGridViewRow>()
                .Where(row => Object.Equals(row.Cells["selected"].Value, true))
                .Select(row => (LockingProcess)row.Tag).ToList();
            if (selected.Count == 0) { MessageBox.Show(this, "先勾选需要结束的占用进程。系统进程和服务不能在此结束。", "WinUnlink"); return; }
            string names = String.Join("\r\n", selected.Select(p => p.Name + "  (PID " + p.ProcessId + ")"));
            if (MessageBox.Show(this, "结束以下进程会丢失未保存的工作：\r\n\r\n" + names +
                "\r\n\r\n确认结束后，请再次点击删除或替换。", "确认结束选中进程", MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            SetBusy(true);
            try
            {
                List<string> outcomes = await Task.Run(delegate {
                    List<string> messages = new List<string>();
                    foreach (LockingProcess process in selected)
                    {
                        string error = ProcessController.Terminate(process);
                        messages.Add(process.Name + " (PID " + process.ProcessId + ")：" + (error ?? "已结束"));
                    }
                    return messages;
                });
                AppendLog(String.Join("\r\n", outcomes));
            }
            finally { SetBusy(false); }
            await RefreshAsync();
        }

        private void RegisterMenu(object sender, EventArgs e)
        {
            ContextMenuRegistration registration = new ContextMenuRegistration();
            if (MessageBox.Show(this, registration.Plan(Application.ExecutablePath), "注册右键菜单",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            try { MenuRegistrationResult result = registration.Install(Application.ExecutablePath); AppendLog(result.Message + " 备份：" + result.BackupPath); }
            catch (Exception error) { MessageBox.Show(this, error.Message, "注册失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void RemoveMenu(object sender, EventArgs e)
        {
            if (MessageBox.Show(this, "移除当前用户的 WinUnlink 文件右键菜单，并保留配置备份。", "移除右键菜单",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            try { MenuRegistrationResult result = new ContextMenuRegistration().Uninstall(); AppendLog(result.Message); }
            catch (Exception error) { MessageBox.Show(this, error.Message, "移除失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void RelaunchElevated(object sender, EventArgs e)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas" };
                if (!String.IsNullOrWhiteSpace(targetBox.Text)) info.Arguments = "--inspect " + CommandLine.Quote(targetBox.Text);
                if (!String.IsNullOrWhiteSpace(replacementBox.Text) && !String.IsNullOrEmpty(info.Arguments))
                    info.Arguments += " --replacement " + CommandLine.Quote(replacementBox.Text);
                Process.Start(info); Close();
            }
            catch (Exception error) { AppendLog("管理员重开未完成：" + error.Message); }
        }

        private void AppendLog(string text)
        {
            logBox.AppendText("\r\n[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text);
            logBox.SelectionStart = logBox.TextLength; logBox.ScrollToCaret();
        }
    }

    internal sealed class ConfirmationDialog : Form
    {
        public ConfirmationDialog(string action, string target, string source, LockScanResult scan, bool reboot,
            bool stopAndDelete = false, DirectorySnapshot directory = null, DeleteMode deleteMode = DeleteMode.RecycleBin)
        {
            Text = "确认" + action; Font = new Font("Microsoft YaHei UI", 9.5F);
            ClientSize = new Size(760, 430); MinimumSize = new Size(620, 380);
            StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1,
                RowCount = 3, Padding = new Padding(20) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); Controls.Add(layout);
            string deletionDescription = deleteMode == DeleteMode.RecycleBin
                ? "移到回收站，失败时不会改成永久删除。" : "永久删除，不进入回收站。";
            Label summary = new Label { Dock = DockStyle.Fill, Text = "目标：" + target +
                (source == null ? "" : "\r\n替换来源：" + source) +
                "\r\n\r\n" + (stopAndDelete ? "结束标记为“将停止”的程序后，" + deletionDescription + "结束程序可能丢失未保存的工作。" :
                reboot ? "需要管理员权限。排队成功后仍需在重启后检查结果。" :
                directory != null ? (source == null ? "文件夹及其中全部内容：" + deletionDescription :
                "整体替换文件夹，旧目标内容会永久移除，不进回收站。来源文件夹保留。") :
                source == null ? deletionDescription : "新内容替换目标，旧目标内容永久移除。来源文件保留。") };
            layout.Controls.Add(summary, 0, 0);
            StringBuilder body = new StringBuilder("当前查询报告的占用进程：\r\n\r\n");
            if (directory != null)
                body.AppendLine("目标包含 " + directory.FileCount + " 个文件、" + directory.DirectoryCount + " 个文件夹。\r\n" +
                    "占用查询覆盖其中的文件；目录句柄、当前工作目录或受保护占用仍可能无法列出。\r\n");
            foreach (LockingProcess process in scan.Processes)
            {
                body.AppendLine(process.Name + "    PID " + process.ProcessId + "    " + process.Type +
                    "\r\n" + (String.IsNullOrEmpty(process.ImagePath) ? process.InspectionError ?? "路径不可查询" : process.ImagePath));
                if (stopAndDelete)
                    body.AppendLine(process.CanTerminate ? "将停止，然后按所选方式处理目标。" :
                        "不会停止：" + (String.IsNullOrEmpty(process.TerminationRestriction) ? "当前程序不允许结束。" : process.TerminationRestriction));
                body.AppendLine();
            }
            if (scan.Processes.Count == 0) body.AppendLine("Restart Manager 未报告占用进程。这不能证明不存在独占句柄或驱动占用。");
            if (stopAndDelete)
                body.AppendLine("此操作使用本次查询中全部可停止的程序，不依赖主窗口的勾选。受限制的程序会保留，删除仍可能因占用而失败。停止失败时不会继续删除。");
            if (!scan.IsComplete) body.AppendLine("\r\n查询未完成：" + scan.Error);
            TextBox details = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.Vertical, Text = body.ToString(), BackColor = Color.White };
            layout.Controls.Add(details, 0, 1);
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
            Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Size = new Size(96, 32) };
            Button confirm = new Button { Text = "确认" + action, DialogResult = DialogResult.OK, AutoSize = true, Height = 32 };
            buttons.Controls.Add(cancel); buttons.Controls.Add(confirm); layout.Controls.Add(buttons, 0, 2);
            AcceptButton = cancel; CancelButton = cancel;
        }
    }

    internal static class CommandLine
    {
        internal static string Quote(string value)
        {
            StringBuilder result = new StringBuilder("\""); int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '\"') result.Append('\\', slashes * 2 + 1);
                else result.Append('\\', slashes);
                result.Append(character); slashes = 0;
            }
            result.Append('\\', slashes * 2); return result.Append('\"').ToString();
        }
    }
}
