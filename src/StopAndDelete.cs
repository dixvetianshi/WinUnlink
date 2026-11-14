using System;
using System.Collections.Generic;

namespace WinUnlink
{
    public sealed class ProcessStopResult
    {
        public LockingProcess Process { get; set; }
        public bool Stopped { get; set; }
        public string Error { get; set; }
    }

    public sealed class StopAndDeleteResult
    {
        public StopAndDeleteResult() { Processes = new List<ProcessStopResult>(); }
        public FileOperationResult FileResult { get; set; }
        public List<ProcessStopResult> Processes { get; private set; }
    }

    public static class StopAndDelete
    {
        // The UI confirms this exact process list before calling. Both the file
        // and each process retain their original owners' identity checks.
        public static StopAndDeleteResult Execute(FileSnapshot target, IList<LockingProcess> processes)
        {
            StopAndDeleteResult result = new StopAndDeleteResult();
            try
            {
                if (target == null || processes == null)
                    throw new ArgumentException("请先查询目标文件及其占用进程。");
                HashSet<int> processIds = new HashSet<int>();
                foreach (LockingProcess process in processes)
                {
                    if (process == null || !process.CanTerminate)
                        throw new ArgumentException("停止列表包含不能结束的进程。请重新查询并确认。");
                    if (!processIds.Add(process.ProcessId))
                        throw new ArgumentException("停止列表包含重复的 PID。请重新查询并确认。");
                }
                FileOperations.VerifyTarget(target);
            }
            catch (Exception error)
            {
                result.FileResult = Failure("没有停止任何进程，也没有删除文件：" + error.Message);
                return result;
            }

            foreach (LockingProcess process in processes)
            {
                string error;
                try { error = ProcessController.Terminate(process); }
                catch (Exception failure) { error = "结束进程未完成：" + failure.Message; }
                result.Processes.Add(new ProcessStopResult { Process = process, Stopped = error == null, Error = error });
                if (error != null)
                {
                    result.FileResult = Failure("停止占用进程未全部成功，未执行删除。部分进程可能已经停止；请查看逐个结果后重新查询。\r\n" + error);
                    return result;
                }
            }

            result.FileResult = FileOperations.Delete(target);
            if (result.FileResult.Status == OperationStatus.Completed)
                result.FileResult.Message = processes.Count == 0 ? "文件已删除，无需停止进程。" :
                    "已停止 " + processes.Count + " 个占用进程，并自动删除目标文件。";
            else if (processes.Count != 0)
                result.FileResult.Message = "已停止所确认的 " + processes.Count + " 个进程，但文件删除未完成。\r\n" + result.FileResult.Message;
            return result;
        }

        private static FileOperationResult Failure(string message)
        {
            return new FileOperationResult { Status = OperationStatus.Failed, Message = message };
        }
    }
}
