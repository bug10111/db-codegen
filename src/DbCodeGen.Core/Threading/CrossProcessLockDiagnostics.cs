using System.Diagnostics;

namespace DbCodeGen.Core.Threading;

/// <summary>
/// 跨进程锁诊断助手：锁等待超时收敛报错时枚举本机 DbCodeGen 相关进程作为疑似持锁者线索，
/// 附入异常文案帮助定位持锁进程（命名 Mutex 内核对象不直接暴露属主，进程清单为候选线索而非定谳）。
/// </summary>
internal static class CrossProcessLockDiagnostics
{
    /// <summary>
    /// 构建疑似持锁者线索文本：枚举当前运行中、进程名以 DbCodeGen 开头且非本进程的进程清单；
    /// 未发现候选时如实注明（持锁进程可能已异常退出由内核回收，或为非 DbCodeGen 命名的进程）。
    /// 仅超时诊断路径调用（低频），枚举开销可接受。
    /// </summary>
    /// <returns>可直接附入异常/日志的诊断线索文本（以句号结尾）。</returns>
    internal static string DescribeSuspectProcesses()
    {
        try
        {
            int currentProcessId = Environment.ProcessId;
            List<string> candidates = new();
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id != currentProcessId
                        && process.ProcessName.StartsWith("DbCodeGen", StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add($"{process.ProcessName}(PID {process.Id})");
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }

            return candidates.Count == 0
                ? "疑似持锁者线索：当前未发现其它 DbCodeGen 进程（持锁进程可能已异常退出由内核回收，或为非 DbCodeGen 命名的进程）。"
                : $"疑似持锁者线索：当前运行中的 DbCodeGen 进程：{string.Join("、", candidates)}。";
        }
        catch (Exception exception)
        {
            // 诊断线索属锦上添花信息，枚举失败不得掩盖超时主异常；失败原因显式附入线索文本保持可诊断
            return $"疑似持锁者线索：进程枚举失败（{exception.Message}）。";
        }
    }
}
