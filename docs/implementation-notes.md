# 实现说明

WinUnlink 是根据 Windows 公开 API 独立实现的单文件删除与替换工具。文件操作、进程身份检查和界面确认各有明确边界；不使用内核驱动、注入或远程关闭其他程序的文件句柄。

## 文件与进程身份

文件检查通过 `GetFileInformationByHandle` 获取卷序列号和文件索引，操作前重新核对目标身份。替换来源与目标身份相同时拒绝操作，包括硬链接别名；不能把同名路径当成同一个文件。

占用查询使用 [Restart Manager](https://learn.microsoft.com/en-us/windows/win32/rstmgr/about-restart-manager)。返回值只表示查询时报告的程序和服务，不是完整句柄清单；失败或空结果不能证明无人占用。

进程身份使用 [RM_UNIQUE_PROCESS](https://learn.microsoft.com/en-us/windows/win32/api/restartmanager/ns-restartmanager-rm_unique_process) 的 PID 与创建时间。结束进程时通过同一个打开的进程句柄核对创建时间、关键性和用户会话，拒绝自身、系统关键进程、服务、会话 0、其他用户会话及无法确认身份的对象。

“停止并删除”先确认本次程序列表，再检查目标身份，逐个结束可处理的占用进程，最后自动删除。某个进程停止失败时保留此前停止结果并跳过删除，不会把混合结果显示成全部未操作。

## 删除与替换

删除优先使用 `FileDispositionInfoEx` 的 POSIX 与忽略只读属性标志。允许共享删除时，删除句柄关闭后文件名消失，其他旧句柄仍可访问原内容，符合 [FILE_DISPOSITION_INFORMATION_EX](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddk/ns-ntddk-_file_disposition_information_ex) 的语义。不支持扩展信息类时才尝试传统删除；名称仍等待其他句柄关闭时报告未完成。

替换先将来源复制到目标同目录的独占临时文件并刷新数据，再使用 `FileRenameInfoEx` 提交。支持 [POSIX rename](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information) 时，旧句柄访问旧内容，新打开的路径访问新内容。提交后核对目标与新文件身份；系统接受提交但无法核实结果时报告 `Partial`，保留可确认的实际路径，不以恢复为由删除已提交的新文件。新文件继承父目录权限，原目标的自定义 ACL 不被复制。

[CreateFileW 的共享规则](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)仍然有效：已有句柄未授予 `FILE_SHARE_DELETE` 时，本项目所测删除与替换返回错误 32。独占占用只能通过关闭对应程序、明确确认“停止并删除”或重启时处理。本工具不保证保持所有程序与旧句柄继续使用的 Linux `unlink` 等价行为。

## 系统集成与恢复

右键菜单只写入当前用户的独立菜单项，普通注册前保存备份，重复注册保持幂等。卸载核对所有权与配置；出现用户修改时保留现状。临时模式使用 [RegistryOptions.Volatile](https://learn.microsoft.com/en-us/dotnet/api/microsoft.win32.registryoptions?view=netframework-4.8)，菜单与命令子键均新建为内存键，随用户 hive 卸载而消失。关闭程序不会卸载 hive；通常注销会卸载，真正重启会清除。

重启操作使用 [MoveFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw)，需要管理员权限且限定本地路径。替换先准备同目录临时文件并保留到重启。`Scheduled` 只表示登记成功；启动队列按路径执行，重启前改变相关路径会改变操作对象。

## 参考软件与来源

没有找到 IObit Unlocker 的官方公开开源源码。[官方产品页](https://www.iobit.com/en/iobit-unlocker.php)提供免费二进制；[EULA](https://www.iobit.com/en/eula.php)保留专有权利。免费下载与开源是不同概念，这一查询不代表穷尽所有可能的源码来源。

参考程序、右键扩展及驱动只进行静态文件和导入表检查，没有运行 IObit、加载驱动或反编译相关函数体。导入表中的文件、句柄与进程 API 仅作为能力线索，不作为算法或运行行为证明。WinUnlink 的实现依据上述公开接口，发布包不包含参考软件。

运行证据及未验证范围见 [验证说明](verification.md)。
