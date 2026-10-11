using System.Diagnostics;
using DbCodeGen.Core.Threading;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板包目录变更操作串行门：进程内经 SemaphoreSlim 互斥同进程的 async 交错读写，
/// 跨进程经命名 Mutex 与同机同用户的其它进程（桌面端/MCP 服务端）互斥用户模板库变更，
/// 防止两个进程并发导入/复制/删除同一包目录造成竞争。
/// 锁等待全程可观测：锁被其它进程持有时经日志记录进入等待、等待心跳与获得/超时收敛三态（注入日志器时生效），
/// 超时收敛异常文案携带等待时长、锁名与疑似持锁者线索。
/// </summary>
public sealed class PackageOperationGate : IDisposable
{
    /// <summary>
    /// 默认跨进程互斥等待超时（毫秒），按覆盖 zip 导入导出等较重 IO 的时长设定，
    /// 超时抛出模板包异常避免调用方在另一进程异常持锁时无限等待。
    /// </summary>
    internal const int DefaultCrossProcessWaitTimeoutMilliseconds = 30_000;

    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly string _mutexName;
    private readonly TimeSpan _crossProcessWaitTimeout;
    private readonly ILogger? _logger;

    /// <summary>
    /// 使用当前用户域的默认跨进程锁名与默认超时创建串行门（无日志器，锁等待不可观测）。
    /// </summary>
    public PackageOperationGate()
        : this(CrossProcessLockNames.ForPackageOperations(),
            TimeSpan.FromMilliseconds(DefaultCrossProcessWaitTimeoutMilliseconds))
    {
    }

    /// <summary>
    /// 使用当前用户域的默认跨进程锁名、默认超时与指定日志器创建串行门：
    /// 注入日志器后锁等待全程可观测（进入等待/等待心跳/获得或超时收敛三态均有日志承载）。
    /// </summary>
    /// <param name="logger">串行门日志器（宿主日志管道供给）；为 null 时等待期不产生日志、行为与无日志器构造一致。</param>
    public PackageOperationGate(ILogger? logger)
        : this(CrossProcessLockNames.ForPackageOperations(),
            TimeSpan.FromMilliseconds(DefaultCrossProcessWaitTimeoutMilliseconds), logger)
    {
    }

    /// <summary>
    /// 使用指定跨进程锁名、超时与日志器创建串行门，供测试注入独立锁名做隔离验证。
    /// </summary>
    /// <param name="mutexName">跨进程命名互斥锁名。</param>
    /// <param name="crossProcessWaitTimeout">跨进程互斥等待超时。</param>
    /// <param name="logger">串行门日志器；为 null 时等待期不产生日志。</param>
    internal PackageOperationGate(string mutexName, TimeSpan crossProcessWaitTimeout, ILogger? logger = null)
    {
        _mutexName = mutexName;
        _crossProcessWaitTimeout = crossProcessWaitTimeout;
        _logger = logger;
    }

    /// <summary>
    /// 在串行门内执行变更操作：进程内同一时刻仅允许一个操作进入，跨进程与同用户域其它进程互斥。
    /// </summary>
    /// <param name="action">在门内执行的变更操作。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>操作完成的任务。</returns>
    public async Task ExecuteExclusiveAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunUnderCrossProcessLockAsync(
                async token =>
                {
                    await action(token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// 在串行门内执行变更操作并返回结果：进程内同一时刻仅允许一个操作进入，跨进程与同用户域其它进程互斥。
    /// </summary>
    /// <typeparam name="T">操作返回结果的类型。</typeparam>
    /// <param name="action">在门内执行的变更操作。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>操作结果。</returns>
    public async Task<T> ExecuteExclusiveAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunUnderCrossProcessLockAsync(action, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// 释放串行门持有的信号量资源。
    /// </summary>
    public void Dispose()
    {
        _semaphore.Dispose();
    }

    /// <summary>
    /// 在专用线程上获取命名互斥锁并同步桥接执行变更操作：
    /// 命名 Mutex 具有线程所有权，获取、变更执行与释放必须落在同一线程，
    /// 异步延续的线程漂移会破坏所有权语义，故由单一池线程持锁全程执行；
    /// 变更操作内部 IO 均已 ConfigureAwait(false)，同步桥接无同步上下文死锁，调用方线程也不被阻塞。
    /// 锁未立即被获得时经日志记录等待三态（进入等待/等待心跳/获得或超时收敛），超时异常携带诊断信息。
    /// </summary>
    /// <typeparam name="T">操作返回结果的类型。</typeparam>
    /// <param name="action">在锁内执行的变更操作。</param>
    /// <param name="cancellationToken">取消标记，贯穿变更操作全程。</param>
    /// <returns>操作结果。</returns>
    private async Task<T> RunUnderCrossProcessLockAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        // Task.Run 承载线程所有权隔离需求（非虚假异步包装）：锁的获取与释放在同一池线程上成对完成
        return await Task.Run(() =>
        {
            var lockStopwatch = Stopwatch.StartNew();
            try
            {
                using CrossProcessMutexScope mutexScope = CrossProcessMutexScope.Wait(
                    _mutexName,
                    _crossProcessWaitTimeout,
                    LogLockWaiting);
                LogLockAcquired(lockStopwatch.Elapsed);
                return action(cancellationToken).GetAwaiter().GetResult();
            }
            catch (TimeoutException exception)
            {
                // 超时收敛为模板包异常，与包操作的既有异常契约一致，由调用方按统一通道提示；
                // 文案携带等待时长、锁名与疑似持锁者线索，超时事实同步落错误日志供取证
                string suspectHint = CrossProcessLockDiagnostics.DescribeSuspectProcesses();
                _logger?.LogError(
                    exception,
                    "等待跨进程模板包操作锁超时：已等待 {WaitedMilliseconds}ms 未获得锁，锁名 {LockName}。{SuspectHint}",
                    (long)_crossProcessWaitTimeout.TotalMilliseconds, _mutexName, suspectHint);
                throw new TemplatePackageException(
                    $"等待跨进程模板包操作锁超时：已等待 {(long)_crossProcessWaitTimeout.TotalMilliseconds}ms 未获得锁"
                    + $"（锁名 {_mutexName}），另一进程可能正持有模板库锁。{suspectHint}",
                    exception);
            }
        }, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// 记录锁等待进度（等待心跳）：锁未立即获得时由分片等待逐片回调，
    /// 首次回调已等待时长为零即进入等待态，后续回调携带累计等待时长。
    /// </summary>
    /// <param name="waited">进入等待以来累计已等待时长。</param>
    private void LogLockWaiting(TimeSpan waited)
    {
        _logger?.LogInformation(
            "仍在等待跨进程模板包操作锁：已等待 {WaitedMilliseconds}ms / 上限 {TimeoutMilliseconds}ms，"
            + "锁名 {LockName}（另一进程可能正持有模板库锁）。",
            (long)waited.TotalMilliseconds, (long)_crossProcessWaitTimeout.TotalMilliseconds, _mutexName);
    }

    /// <summary>
    /// 记录锁获得事实：仅锁存在实际等待（超过 1ms）时记录一条获得日志，
    /// 锁立即可得的常态快速路径不产生日志，避免每次包操作的常态噪音。
    /// </summary>
    /// <param name="waited">从进入跨进程锁获取到获得锁的累计等待时长。</param>
    private void LogLockAcquired(TimeSpan waited)
    {
        if (waited.TotalMilliseconds > 1)
        {
            _logger?.LogInformation(
                "等待跨进程模板包操作锁 {WaitedMilliseconds}ms 后获得，锁名 {LockName}。",
                (long)waited.TotalMilliseconds, _mutexName);
        }
    }
}
