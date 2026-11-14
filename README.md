# WinUnlink

Windows 文件占用查询、删除与替换工具，提供中文桌面窗口和可选的文件右键菜单。

WinUnlink 使用 Windows 官方接口独立实现，无需内核驱动。允许共享删除的文件可以在占用程序继续运行时删除或替换；独占占用可通过“停止并删除”结束相关程序后自动删除。它不保证对所有占用实现 Linux `unlink` 的旧句柄保留行为。

## 下载与运行

从 [GitHub Releases](https://github.com/dixvetianshi/WinUnlink/releases) 下载 `WinUnlink-v0.1.1-windows-portable.zip`，解压后双击 `WinUnlink.exe`。请让 `WinUnlink.exe.config` 始终与程序位于同一目录，不要只复制 EXE。

- 运行环境：Windows 10 / 11，.NET Framework 4.8 或 4.8.1。
- 默认构建为 AnyCPU，在 64 位 Windows 上运行 64 位进程。
- 解压和普通启动不会安装右键菜单、驱动或服务，也不会登记重启任务。
- ZIP 附带 `SHA256SUMS.txt`，可用 `Get-FileHash -Algorithm SHA256` 检查文件摘要。

当前真实验证环境为 Windows 10 19044、NTFS、.NET Framework 4.8；Windows 11 尚未进行实机验证。

## 使用方法

1. 选择一个目标文件，或将文件拖入窗口。点击“查询占用”查看进程名称、PID 和程序路径。
2. 删除时点击“删除文件”。弹窗列出当前查询报告的占用程序，确认后尝试保留程序运行并删除。
3. 独占占用阻止删除时，点击“停止并删除”。确认窗口会标明将停止的程序及 PID；确认后结束全部可处理的占用程序并自动删除，无需先勾选或再点删除。
4. 替换时选择“替换来源”，再点击“替换文件”。来源文件会保留。需要结束占用者时，可勾选进程并点击“结束选中进程”，再重试替换。

删除不进入回收站。“停止并删除”和“结束选中进程”可能丢失占用程序尚未保存的工作，请先核对确认窗口中的程序名称和 PID。系统关键进程、服务、会话 0 和其他用户会话的进程不会在此结束；停止失败会显示逐个结果并跳过删除。

替换先将新内容复制到目标同目录的临时文件并刷新到磁盘，再通过重命名提交，不会先清空原目标。新文件继承目标父目录的权限，不克隆原目标的自定义 ACL、时间或只读属性。实现细节见 [实现说明](docs/implementation-notes.md)。

## 可选右键菜单

在窗口底部点击“注册右键菜单”，工具会先显示变更计划。它只添加当前用户的文件菜单项：

`HKCU\Software\Classes\*\shell\WinUnlink.FileTools`

Win10 使用普通文件右键菜单；Win11 从“显示更多选项”进入。菜单打开工具并查询文件，删除或替换仍需确认。重复注册相同程序路径不会重复修改，移动程序后可重新注册以更新路径。

普通注册的备份存放在 `%LOCALAPPDATA%\WinUnlink\backups`。点击“移除右键菜单”恢复注册前状态，保留备份；如果菜单被手动修改或同名项属于其他配置，工具会保留现有配置并说明原因。

需要临时入口时，在解压目录运行：

```powershell
.\WinUnlink.exe --install-menu-temporary --yes
```

临时菜单使用内存中的 Volatile 注册表键，随用户注册表 hive 卸载而消失，通常注销后失效，真正重启会清除。关闭工具窗口不会移除菜单。临时模式的备份存放在程序旁的 `temporary-menu-backups`。

可随时在窗口中点击“移除右键菜单”，或双击便携包根目录的 `remove-context-menu.cmd`。从源码构建时，对应撤回脚本为 `scripts/立即移除右键菜单.cmd`。已有普通菜单不会自动转换为临时菜单，需要先移除。

## 重启后处理

“重启后删除”和“重启后替换”需要管理员权限，仅接受可确认的本地磁盘路径。工具只登记操作，不会自动重启计算机。

排队成功仅表示 Windows 接受了请求，不表示文件已经删除或替换。重启后仍须检查结果。重启替换会预先准备目标同目录的临时文件，请保留结果显示的文件；重启前不要移动或替换目标，启动队列按路径执行。

## 从源码构建

在项目目录打开 PowerShell：

```powershell
.\build.ps1
.\test.ps1 -CompileOnly
```

使用 Windows 自带的 .NET Framework C# 编译器，无 NuGet 包，不需要 .NET SDK、Node.js 或 Java。程序输出到 `artifacts/bin`。可用 `build.ps1 -Platform x86` 或 `-Platform x64` 指定架构，也可用支持 .NET Framework 4.8 的 Visual Studio 打开 `WinUnlink.csproj`。

生成与 Release 相同布局的便携包：

```powershell
.\scripts\package-release.ps1
```

ZIP 和归档 SHA-256 输出到 `artifacts/releases/v0.1.1`。默认构建不包含调试路径；开发时需要调试符号可运行 `build.ps1 -IncludeDebugSymbols`。

执行文件与 CLI 测试：

```powershell
.\test.ps1
.\test.ps1 -Platform x86
```

夹具位于项目 `artifacts/**/cases/<GUID>`，只操作自建文件和测试子进程，默认跳过注册表测试。注册表测试仅供隔离的 Windows 沙盒或虚拟机中人工运行，必须显式传入 `-AllowRegistryTests`。底层系统探针位于 `tests/probe.ps1`。

## 命令入口

```powershell
.\WinUnlink.exe --inspect 'C:\work\target.dll'
.\WinUnlink.exe --scan 'C:\work\target.dll' --report 'C:\work\scan.json'
.\WinUnlink.exe --install-menu --yes --report 'C:\work\menu-install.json'
.\WinUnlink.exe --uninstall-menu --yes --report 'C:\work\menu-remove.json'
```

上述命令适用于便携包目录；源码构建时将程序路径改为 `.\artifacts\bin\WinUnlink.exe`。扫描命令只读，JSON 报告保留进程名称、PID、创建时间和操作限制。报告文件必须尚不存在。菜单命令没有 `--yes` 时只返回计划；删除与替换在窗口内确认。

## 兼容性与限制

- 当前只支持单个普通文件，拒绝文件夹、最终路径为重解析点的文件、设备路径和 NTFS 备用数据流。
- Restart Manager 可能漏掉受保护进程、驱动或新出现的占用，零条结果不能证明没有占用。
- 独占句柄、权限和运行中的可执行映像仍可能阻止操作。管理员权限不会绕过共享锁或全部映射限制。
- 不支持 POSIX 标志的系统或文件系统会尝试传统接口；仍等待其他句柄关闭时会明确报告未完成。
- Windows 11、网络共享、其他文件系统与重启队列执行尚未进行真实验证。

v0.1.1 的 AnyCPU（实际 x64）和 x86 各通过 19 个文件/进程场景与 4 个 CLI 场景。验证方法及未覆盖范围见 [验证说明](docs/verification.md)。

本项目不依赖或分发 IObit Unlocker 的二进制、驱动或反编译源码。参考来源见 [实现说明](docs/implementation-notes.md)。

## 许可证

采用 [MIT 许可证](LICENSE)，允许使用、修改与分发；请保留许可证和版权声明。
