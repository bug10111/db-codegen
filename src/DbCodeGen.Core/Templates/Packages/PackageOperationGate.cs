namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板包目录变更操作串行门，基于 SemaphoreSlim 将导入/复制/删除等变更操作互斥化，
/// 防止 async 交错读写同一包目录造成竞争。
/// </summary>
public sealed class PackageOperationGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// 在串行门内执行变更操作，同一时刻仅允许一个操作进入。
    /// </summary>
    /// <param name="action">在门内执行的变更操作。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>操作完成的任务。</returns>
    public async Task ExecuteExclusiveAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// 在串行门内执行变更操作并返回结果。
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
            return await action(cancellationToken).ConfigureAwait(false);
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
}
