using DbCodeGen.Core.Threading;

namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板包目录变更操作串行门：进程内经 SemaphoreSlim 互斥同进程的 async 交错读写，
/// 跨进程经命名 Mutex 与同机同用户的其它进程（桌面端/MCP 服务端）互斥用户模板库变更，
/// 防止两个进程并发导入/复制/删除同一包目录造成竞争。
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

    /// <summary>
    /// 使用当前用户域的默认跨进程锁名与默认超时创建串行门。
    /// </summary>
    public PackageOperationGate()
        : this(
            CrossProcessLockNames.ForPackageOperations(),
            TimeSpan.FromMilliseconds(DefaultCrossProcessWaitTimeoutMilliseconds))
    {
    }

    /// <summary>
    /// 使用指定跨进程锁名与超时创建串行门，供测试注入独立锁名做隔离验证。
    /// </summary>
    /// <param name="mutexName">跨进程命名互斥锁名。</param>
    /// <param name="crossProcessWaitTimeout">跨进程互斥等待超时。</param>
    internal PackageOperationGate(string mutexName, TimeSpan crossProcessWaitTimeout)
    {
        _mutexName = mutexName;
        _crossProcessWaitTimeout = crossProcessWaitTimeout;
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
            try
            {
                using CrossProcessMutexScope mutexScope = CrossProcessMutexScope.Wait(_mutexName, _crossProcessWaitTimeout);
                return action(cancellationToken).GetAwaiter().GetResult();
            }
            catch (TimeoutException exception)
            {
                // 超时收敛为模板包异常，与包操作的既有异常契约一致，由调用方按统一通道提示
                throw new TemplatePackageException(
                    $"等待跨进程模板包操作锁超时（{(long)_crossProcessWaitTimeout.TotalMilliseconds}ms），另一进程可能正持有模板库锁。", exception);
            }
        }, CancellationToken.None).ConfigureAwait(false);
    }
}
