using System.Diagnostics;

namespace DbCodeGen.Core.Threading;

/// <summary>
/// 跨进程命名互斥锁作用域：创建命名 Mutex 并限时获取，Dispose 时释放所有权。
/// Mutex 具有线程所有权语义，本作用域的获取与释放必须在同一线程上成对使用，
/// 异步上下文中应经专用线程同步桥接持有（参见 PackageOperationGate）。
/// 前一持有进程崩溃退出时内核将 Mutex 标记为 abandoned，本作用域捕获后视为已获得继续执行，不产生永久锁死。
/// 等待期经分片等待实现心跳回调：锁被其它进程持有时调用方可逐片获得等待进度信号，等待与超时总语义与单次限时等待一致。
/// </summary>
internal sealed class CrossProcessMutexScope : IDisposable
{
    /// <summary>
    /// 分片等待的单片时长（毫秒）：锁被占用时按该片长切分总超时逐片等待，
    /// 每片结束未获得即触发一次心跳回调，总时限与超时语义不受分片影响。
    /// </summary>
    internal const int WaitSliceMilliseconds = 5_000;

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
    /// <param name="onWaitHeartbeat">等待进度心跳回调（可选）：锁未立即获得时先以零耗时回调标记进入等待，
    /// 之后每片等待结束仍未获得时以已等待时长再次回调；锁立即可得时全程不回调。</param>
    /// <returns>已持有命名互斥锁的作用域，由获取线程负责 Dispose 释放。</returns>
    /// <exception cref="TimeoutException">超时未获得锁时抛出，内核对象句柄已清理。</exception>
    internal static CrossProcessMutexScope Wait(string name, TimeSpan timeout, Action<TimeSpan>? onWaitHeartbeat = null)
    {
        Mutex mutex = new(initiallyOwned: false, name);
        try
        {
            // 零超时探测：锁立即可得时零等待直接返回（常态快速路径，不触发任何心跳回调）
            if (TryAcquireSlice(mutex, TimeSpan.Zero))
            {
                return new CrossProcessMutexScope(mutex);
            }

            var stopwatch = Stopwatch.StartNew();
            onWaitHeartbeat?.Invoke(TimeSpan.Zero);

            TimeSpan slice = TimeSpan.FromMilliseconds(WaitSliceMilliseconds);
            while (true)
            {
                TimeSpan remaining = timeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"等待跨进程互斥锁超时：{name}（{(long)timeout.TotalMilliseconds}ms）。");
                }

                // 末片按剩余时长收缩，保证总等待时限与单次限时等待完全一致
                TimeSpan currentSlice = remaining < slice ? remaining : slice;
                if (TryAcquireSlice(mutex, currentSlice))
                {
                    return new CrossProcessMutexScope(mutex);
                }

                onWaitHeartbeat?.Invoke(stopwatch.Elapsed);
            }
        }
        catch
        {
            // 获取失败时清理本句柄，避免泄漏；已存在的命名对象本身不受 Dispose 影响
            mutex.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 在限定时长内尝试获得 Mutex 所有权：获得返回 true；
    /// 前一持有进程未释放即退出（abandoned）时本线程已获得所有权，同样按获得处理返回 true。
    /// </summary>
    /// <param name="mutex">待获取的互斥锁。</param>
    /// <param name="slice">本片等待时长，零表示非阻塞探测。</param>
    /// <returns>是否已获得所有权。</returns>
    private static bool TryAcquireSlice(Mutex mutex, TimeSpan slice)
    {
        try
        {
            return mutex.WaitOne(slice);
        }
        catch (AbandonedMutexException)
        {
            // 前一持有进程未释放即退出：本线程已获得所有权，接管继续执行
            return true;
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
