using System.Runtime.Versioning;
using DbCodeGen.Core.Templates.Packages;
using DbCodeGen.Core.Threading;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.Core.Tests.Threading;

/// <summary>
/// 跨进程命名互斥锁与模板包串行门单元测试：覆盖同进程串行语义不回退、命名锁对其它句柄可见、
/// 跨进程锁被占时的超时契约、持有进程崩溃后的 abandoned 恢复与锁名派生稳定性，
/// 以及锁等待全程可观测契约（进入等待/等待心跳/获得或超时收敛三态日志与超时诊断文案）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CrossProcessMutexTests : IDisposable
{
    private readonly string _tempRoot;

    /// <summary>
    /// 为每个测试实例创建独立临时目录。
    /// </summary>
    public CrossProcessMutexTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// 递归删除临时目录。
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// 并发多个变更操作经串行门执行时应严格互斥，最大并发进入数为 1，
    /// 验证升级跨进程锁后同进程串行语义不回退。
    /// </summary>
    [Fact]
    public async Task PackageOperationGate_ConcurrentOperations_NeverOverlap()
    {
        using PackageOperationGate gate = new(BuildUniqueLockName(), TimeSpan.FromSeconds(10));

        var sync = new object();
        int inFlight = 0;
        int maxInFlight = 0;
        var tasks = new List<Task>();
        for (int i = 0; i < 6; i++)
        {
            tasks.Add(gate.ExecuteExclusiveAsync(async token =>
            {
                lock (sync)
                {
                    inFlight++;
                    maxInFlight = Math.Max(maxInFlight, inFlight);
                }

                // 模拟包目录变更耗时，放大并发窗口便于暴露互斥失效
                await Task.Delay(50, token);
                lock (sync)
                {
                    inFlight--;
                }
            }, CancellationToken.None));
        }

        await Task.WhenAll(tasks);

        Assert.Equal(1, maxInFlight);
    }

    /// <summary>
    /// 串行门内操作执行期间，另一独立命名句柄应探测到锁被持有；操作完成后可正常获得，
    /// 验证跨进程互斥真正经内核命名对象生效（对同机其它进程同理可见）。
    /// </summary>
    [Fact]
    public async Task PackageOperationGate_HoldsNamedMutexVisibleToOtherHandles()
    {
        string lockName = BuildUniqueLockName();
        using PackageOperationGate gate = new(lockName, TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task operation = gate.ExecuteExclusiveAsync(async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        }, CancellationToken.None);
        await entered.Task;

        // 门内操作持有期间：另一句柄零超时探测应不可获得
        bool acquiredDuringOperation = false;
        using (Mutex probe = new(initiallyOwned: false, lockName))
        {
            acquiredDuringOperation = probe.WaitOne(TimeSpan.Zero);
            if (acquiredDuringOperation)
            {
                probe.ReleaseMutex();
            }
        }

        Assert.False(acquiredDuringOperation);

        release.SetResult();
        await operation;

        // 操作完成释放后：另一句柄应可获得（获得后由探测方释放归还）
        bool acquiredAfterOperation = false;
        using (Mutex probe = new(initiallyOwned: false, lockName))
        {
            acquiredAfterOperation = probe.WaitOne(TimeSpan.FromSeconds(5));
            if (acquiredAfterOperation)
            {
                probe.ReleaseMutex();
            }
        }

        Assert.True(acquiredAfterOperation);
    }

    /// <summary>
    /// 跨进程锁被另一持有方占用时，串行门应按注入超时抛出模板包异常，而非无限等待。
    /// </summary>
    [Fact]
    public async Task PackageOperationGate_CrossProcessLockHeld_ThrowsTemplatePackageExceptionAfterTimeout()
    {
        string lockName = BuildUniqueLockName();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using PackageOperationGate holdingGate = new(lockName, TimeSpan.FromSeconds(10));

        Task holdingOperation = holdingGate.ExecuteExclusiveAsync(async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        }, CancellationToken.None);
        await entered.Task;

        using PackageOperationGate waitingGate = new(lockName, TimeSpan.FromMilliseconds(200));
        TemplatePackageException exception = await Assert.ThrowsAsync<TemplatePackageException>(
            () => waitingGate.ExecuteExclusiveAsync(token => Task.CompletedTask, CancellationToken.None));

        Assert.Contains("等待跨进程模板包操作锁超时", exception.Message);

        release.SetResult();
        await holdingOperation;
    }

    /// <summary>
    /// 持锁线程未释放即退出（模拟持有进程崩溃）后，下一次获取应捕获 abandoned 标记继续执行，不产生永久锁死。
    /// </summary>
    [Fact]
    public async Task PackageOperationGate_AbandonedMutexOwnership_RecoveredByNextAcquisition()
    {
        string lockName = BuildUniqueLockName();

        // 常驻句柄维持内核对象存活，确保走 abandoned 恢复路径而非对象销毁重建路径
        using Mutex keepAlive = new(initiallyOwned: false, lockName);
        Thread lockerThread = new Thread(() =>
        {
            // 获取所有权后直接退出线程且不释放，触发内核 abandoned 标记
            Mutex owner = new(initiallyOwned: false, lockName);
            owner.WaitOne();
        });
        lockerThread.Start();
        lockerThread.Join();

        using PackageOperationGate gate = new(lockName, TimeSpan.FromSeconds(10));
        bool executed = await gate.ExecuteExclusiveAsync(
            token => Task.FromResult(true), CancellationToken.None);

        Assert.True(executed);
    }

    /// <summary>
    /// 配置文件锁名应按路径确定性派生：同路径（含大小写与末尾分隔符差异）同名、不同路径不同名。
    /// </summary>
    [Fact]
    public void CrossProcessLockNames_ConfigFileLockName_DerivedStablyByPath()
    {
        string pathA = Path.Combine(_tempRoot, "dirA", "config.json");
        string pathADifferentCase = Path.Combine(_tempRoot.ToUpperInvariant(), "DIRA", "CONFIG.JSON");

        string nameA = CrossProcessLockNames.ForConfigFile(pathA);
        string nameARepeat = CrossProcessLockNames.ForConfigFile(pathA);
        string nameACaseVariant = CrossProcessLockNames.ForConfigFile(pathADifferentCase);
        string nameB = CrossProcessLockNames.ForConfigFile(Path.Combine(_tempRoot, "dirB", "config.json"));

        Assert.Equal(nameA, nameARepeat);
        Assert.Equal(nameA, nameACaseVariant);
        Assert.NotEqual(nameA, nameB);
        Assert.StartsWith("Global\\DbCodeGen.Config.", nameA);
    }

    /// <summary>
    /// 模板包操作锁名在当前用户域内应稳定派生，桌面端与 MCP 服务端进程取同一名称实现互斥。
    /// </summary>
    [Fact]
    public void CrossProcessLockNames_PackageOperationsLockName_StableInCurrentUserScope()
    {
        string firstName = CrossProcessLockNames.ForPackageOperations();
        string secondName = CrossProcessLockNames.ForPackageOperations();

        Assert.Equal(firstName, secondName);
        Assert.StartsWith("Global\\DbCodeGen.PackageOps.", firstName);
    }

    /// <summary>
    /// 跨进程锁被另一持有方占用时，等待方应逐态产生可观测信号：进入等待（已等待 0ms）与
    /// 获得锁（等待耗时）日志先后出现；持锁释放后等待方正常完成操作，等待/超时语义不受可观测改造影响。
    /// </summary>
    [Fact]
    public async Task PackageOperationGate_LockContended_EmitsWaitingAndAcquiredLogs()
    {
        string lockName = BuildUniqueLockName();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using PackageOperationGate holdingGate = new(lockName, TimeSpan.FromSeconds(10));

        Task holdingOperation = holdingGate.ExecuteExclusiveAsync(async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        }, CancellationToken.None);
        await entered.Task;

        var capturingLogger = new CapturingLogger();
        using PackageOperationGate waitingGate = new(lockName, TimeSpan.FromSeconds(10), capturingLogger);

        // 持锁 1.2 秒后释放：等待方在分片等待中先落进入等待日志，获得后落获得日志
        _ = Task.Run(async () =>
        {
            await Task.Delay(1200);
            release.SetResult();
        });

        await waitingGate.ExecuteExclusiveAsync(token => Task.CompletedTask, CancellationToken.None);
        await holdingOperation;

        Assert.Contains(capturingLogger.Messages, message =>
            message.Contains("仍在等待跨进程模板包操作锁") && message.Contains("已等待 0ms"));
        Assert.Contains(capturingLogger.Messages, message =>
            message.Contains("后获得") && message.Contains(lockName));
    }

    /// <summary>
    /// 锁立即可得的常态快速路径应零等待日志：不产生等待与获得日志，保持常态零噪音。
    /// </summary>
    [Fact]
    public async Task PackageOperationGate_NoContention_EmitsNoWaitingLogs()
    {
        var capturingLogger = new CapturingLogger();
        using PackageOperationGate gate = new(BuildUniqueLockName(), TimeSpan.FromSeconds(10), capturingLogger);

        await gate.ExecuteExclusiveAsync(token => Task.CompletedTask, CancellationToken.None);

        Assert.DoesNotContain(capturingLogger.Messages, message => message.Contains("仍在等待跨进程模板包操作锁"));
        Assert.DoesNotContain(capturingLogger.Messages, message => message.Contains("后获得"));
    }

    /// <summary>
    /// 跨进程锁被占且等待超时时，收敛异常与错误日志应携带诊断信息：
    /// 等待时长、锁名与疑似持锁者线索齐全，既有超时异常契约（模板包异常）保持不变。
    /// </summary>
    [Fact]
    public async Task PackageOperationGate_Timeout_ExceptionAndLogCarryDiagnostics()
    {
        string lockName = BuildUniqueLockName();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using PackageOperationGate holdingGate = new(lockName, TimeSpan.FromSeconds(10));

        Task holdingOperation = holdingGate.ExecuteExclusiveAsync(async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        }, CancellationToken.None);
        await entered.Task;

        var capturingLogger = new CapturingLogger();
        using PackageOperationGate waitingGate = new(lockName, TimeSpan.FromMilliseconds(200), capturingLogger);
        TemplatePackageException exception = await Assert.ThrowsAsync<TemplatePackageException>(
            () => waitingGate.ExecuteExclusiveAsync(token => Task.CompletedTask, CancellationToken.None));

        // 异常文案携带等待时长、锁名与疑似持锁者线索；既有超时文案前缀保持不变
        Assert.Contains("等待跨进程模板包操作锁超时", exception.Message);
        Assert.Contains("已等待 200ms", exception.Message);
        Assert.Contains(lockName, exception.Message);
        Assert.Contains("疑似持锁者线索", exception.Message);

        // 错误日志同样携带等待时长与锁名，供日志侧取证
        Assert.Contains(capturingLogger.Messages, message =>
            message.Contains("等待跨进程模板包操作锁超时") && message.Contains("已等待 200ms") && message.Contains(lockName));

        release.SetResult();
        await holdingOperation;
    }

    /// <summary>
    /// 构造当前测试独占的跨进程锁名，避免与真实桌面端/其它测试实例互相干扰。
    /// </summary>
    /// <returns>全局唯一的内核命名对象名。</returns>
    private static string BuildUniqueLockName()
    {
        return $"Global\\DbCodeGen.Tests.PackageGate.{Guid.NewGuid():N}";
    }

    /// <summary>
    /// 测试用捕获日志器：把全部日志记录按格式化文本收集到内存清单，供断言锁等待三态日志。
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        /// <summary>
        /// 已记录的格式化日志文本清单（含级别前缀）。
        /// </summary>
        public List<string> Messages { get; } = new();

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add($"[{logLevel}] {formatter(state, exception)}");
        }
    }
}
