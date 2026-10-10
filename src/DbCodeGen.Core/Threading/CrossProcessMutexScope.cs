namespace DbCodeGen.Core.Threading;

/// <summary>
/// 跨进程命名互斥锁作用域：创建命名 Mutex 并限时获取，Dispose 时释放所有权。
/// Mutex 具有线程所有权语义，本作用域的获取与释放必须在同一线程上成对使用，
/// 异步上下文中应经专用线程同步桥接持有（参见 PackageOperationGate）。
/// 前一持有进程崩溃退出时内核将 Mutex 标记为 abandoned，本作用域捕获后视为已获得继续执行，不产生永久锁死。
/// </summary>
internal sealed class CrossProcessMutexScope : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    private CrossProcessMutexScope(Mutex mutex)
    {
        _mutex = mutex;
    }

    /// <summary>
    /// 以指定名称与超时获取跨进程命名互斥锁。
    /// </summary>
    /// <param name="name">内核命名对象名（含 Global\ 或 Local\ 前缀）。</param>
    /// <param name="timeout">等待超时时长。</param>
    /// <returns>已持有命名互斥锁的作用域，由获取线程负责 Dispose 释放。</returns>
    /// <exception cref="TimeoutException">超时未获得锁时抛出，内核对象句柄已清理。</exception>
    internal static CrossProcessMutexScope Wait(string name, TimeSpan timeout)
    {
        Mutex mutex = new(initiallyOwned: false, name);
        try
        {
            try
            {
                if (!mutex.WaitOne(timeout))
                {
                    throw new TimeoutException($"等待跨进程互斥锁超时：{name}（{(long)timeout.TotalMilliseconds}ms）。");
                }
            }
            catch (AbandonedMutexException)
            {
                // 前一持有进程未释放即退出：本线程已获得所有权，接管继续执行
            }

            return new CrossProcessMutexScope(mutex);
        }
        catch
        {
            // 获取失败时清理本句柄，避免泄漏；已存在的命名对象本身不受 Dispose 影响
            mutex.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 释放命名互斥锁的所有权并清理内核对象句柄；必须在获取线程上调用。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _mutex.ReleaseMutex();
        }
        finally
        {
            _mutex.Dispose();
        }
    }
}
