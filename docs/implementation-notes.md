# 实现说明

WinUnlink 根据 Windows 公开 API 实现文件与文件夹的占用查询、删除及替换。文件系统身份、目录树解释、进程身份检查和界面确认各有明确边界；不使用内核驱动、注入或远程关闭其他程序的文件句柄。

## 文件系统与进程身份

文件和目录检查通过 `GetFileInformationByHandle` 获取卷序列号、文件索引与类型，操作前重新核对。目录树保留根与子项身份、类型、长度和只读状态；操作前重新枚举核对，避免继续使用已变更的树快照。这些检查不是内容哈希，也不把整个文件系统视为静止快照。

文件夹范围通过句柄确认的实际路径判断，拒绝磁盘和 UNC 共享根。目标与来源不能是相同目录或相互包含，路径别名也使用实际路径判断。最终重解析点与目录树中发现的重解析点均拒绝，不跟随 junction 或符号链接遍历外部目录。

占用查询使用 [Restart Manager](https://learn.microsoft.com/en-us/windows/win32/rstmgr/about-restart-manager)。文件夹查询注册枚举出的文件路径，汇总并按 PID 与创建时间去重，不把目录根路径直接作为文件资源注册。空树、目录句柄、工作目录、驱动与受保护进程占用可能不在结果中；成功的查询或空列表不证明完整占用覆盖。

进程身份使用 [RM_UNIQUE_PROCESS](https://learn.microsoft.com/en-us/windows/win32/api/restartmanager/ns-restartmanager-rm_unique_process) 的 PID 与创建时间。结束进程时通过同一个打开的句柄核对创建时间、关键性和用户会话，拒绝自身、系统关键进程、服务、会话 0、其他用户会话及身份未知的对象。

“停止并删除”对文件或文件夹均可使用：确认程序列表后检查目标快照，逐个结束可处理的占用进程，再执行删除。停止失败会保留逐个结果并跳过删除；已经停止的进程不会被描述成没有操作。

## 文件删除与替换

目标删除按显式方式分派，默认使用 Windows 回收站，文件、文件夹和“停止并删除”均沿用该选择。回收路径通过独立 STA 线程调用 [IFileOperation](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ifileoperation)，请求 `FOFX_RECYCLEONDELETE` 并保留 `FOF_WANTNUKEWARNING` 警告；代码不会因回收失败调用永久删除接口。成功结果须包含 Shell 返回的回收站项目，且原目标已经离开原位置。实际回收、系统无法回收时的提示与取消行为尚未验证。

直接删除文件优先使用 `FileDispositionInfoEx` 的 POSIX 与忽略只读属性标志。允许共享删除时，删除句柄关闭后文件名消失，其他旧句柄仍可访问原内容，符合 [FILE_DISPOSITION_INFORMATION_EX](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddk/ns-ntddk-_file_disposition_information_ex) 的语义。扩展信息类不可用时才尝试传统删除；仍等待其他句柄关闭时报告未完成。

文件替换先将来源复制到目标同目录的独占临时文件并刷新数据，再使用 `FileRenameInfoEx` 提交。支持 [POSIX rename](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information) 时，旧句柄访问旧内容，新打开的路径访问新内容。提交后核对目标与新文件身份；系统接受提交但无法核实结果时报告 `Partial`，保留可确认的实际路径，不以恢复为由删除已提交的新文件。

[CreateFileW 的共享规则](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)仍然有效：未授予 `FILE_SHARE_DELETE` 的句柄可能使删除或替换返回错误 32。关闭对应程序、确认停止占用者或对单文件安排重启处理是现有恢复方式，本工具不保证 Linux `unlink` 的全部行为。

## 文件夹删除与整体替换

文件夹直接删除先检查整个目录树，再删除文件、按深度删除子目录，最后删除根。逐项操作仍可能遇到后来的占用或权限问题；已有项目被删除时返回 `Partial`，保留剩余目标路径和实际移除数量。不会还原已完成的删除。

文件夹替换不合并目录：先复制来源的完整目录树到目标同级的临时目录，核对来源、目标与准备的树，再将原目标重命名到备份位置，把新目录重命名到目标。来源保持保留，旧目标中来源没有的项目随旧备份清理而移除。

两次重命名不是原子整树交换，目标路径中间可能短暂不存在。提交失败会尝试将备份移回目标并检查原树；恢复失败或恢复检查未知时返回 `Partial`，显示原内容与新目录的保留位置。该恢复失败分支尚未真实故障注入。

提交成功后先核对新目标，再检查并删除旧备份。新目标核查、旧备份身份或清理失败时返回 `Partial`，不报告整体完成。替换的旧内容与临时备份清理属于永久删除，不受目标删除方式选择控制，也不送到回收站。新文件和目录继承其父目录权限，不复制原目标的自定义 ACL、时间或只读属性。

## 系统集成与恢复

右键菜单拥有当前用户的文件与目录两个独立入口。安装前同时核对两项所有权、模式和备份；重复注册保持幂等。旧版只有文件入口时可补上目录入口，已有文件入口的原始备份保留。安装与卸载遇到预期失败时尝试恢复已修改项；恢复失败保留具体原因，不把部分变更说成全部完成。当前双入口和升级的注册表测试仅完成编译，运行范围见验证说明。

临时模式使用 [RegistryOptions.Volatile](https://learn.microsoft.com/en-us/dotnet/api/microsoft.win32.registryoptions?view=netframework-4.8)，两个菜单与各自命令子键均新建为内存键，随用户 hive 卸载而消失。关闭程序不会卸载 hive；通常注销会卸载，真正重启会清除。普通与临时模式不自动互换。

单文件重启操作使用 [MoveFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw)，需要管理员权限和本地路径。重启删除仅允许明确选择直接删除；回收站模式在界面和调用边界均拒绝排队。重启替换不受删除模式控制，先准备同目录临时文件并保留到重启。`Scheduled` 只表示登记成功，启动队列按路径执行。文件夹不支持重启排队，界面禁用该入口，调用边界也拒绝登记。

## 参考软件与来源

没有找到 IObit Unlocker 的官方公开开源源码。[官方产品页](https://www.iobit.com/en/iobit-unlocker.php)提供免费二进制；[EULA](https://www.iobit.com/en/eula.php)保留专有权利。免费下载与开源是不同概念，这一查询不代表穷尽所有可能的源码来源。

参考程序、右键扩展及驱动只进行静态文件和导入表检查，没有运行 IObit、加载驱动或反编译相关函数体。导入表中的文件、句柄与进程 API 仅作能力线索，不作为算法或运行行为证明。发布包不包含参考软件。

运行结果及未验证范围见 [验证说明](verification.md)。
