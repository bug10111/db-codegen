using System.Windows;
using System.Windows.Threading;
using DbCodeGen.Core.Model;

namespace DbCodeGen.App.Services;

/// <summary>
/// 当前连接共享状态服务实现，以单例承载当前数据源连接的选择、清除与变更通知。
/// 快照存储与读取经锁串行化，变更通知统一在 UI 线程派发，
/// 供主窗口工具栏、数据源管理窗口与各消费方联动。
/// </summary>
public sealed class CurrentDataSourceService : ICurrentDataSourceService
{
    /// <summary>
    /// 保护当前连接字段读写的同步锁，保证快照读写串行一致。
    /// </summary>
    private readonly object _syncRoot = new();

    /// <summary>
    /// UI 线程调度器，用于把当前连接变更通知派发到界面线程执行。
    /// </summary>
    private readonly Dispatcher _dispatcher;

    /// <summary>
    /// 当前数据源连接配置的内部存储，仅在锁内读写。
    /// </summary>
    private DataSourceConfig? _current;

    /// <summary>
    /// 构造当前连接共享状态服务，优先取 WPF 应用主线程调度器用于事件派发。
    /// </summary>
    public CurrentDataSourceService()
    {
        // 应用实例已创建时取主线程调度器，否则退回当前线程调度器
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    /// <inheritdoc />
    public DataSourceConfig? Current
    {
        get
        {
            lock (_syncRoot)
            {
                // 返回快照副本，防止调用方修改返回对象污染内部共享状态
                return _current is null ? null : CloneConfig(_current);
            }
        }
    }

    /// <inheritdoc />
    public void SetCurrent(DataSourceConfig? config)
    {
        DataSourceConfig? snapshot;
        lock (_syncRoot)
        {
            // 入参复制为内部快照后再持有，后续外部对象变更不影响当前连接
            snapshot = config is null ? null : CloneConfig(config);
            _current = snapshot;
        }

        // 在锁外派发通知，避免事件处理器反向调用本服务时产生死锁
        Publish(snapshot);
    }

    /// <inheritdoc />
    public void ClearCurrent()
    {
        // 清除当前连接复用设置空快照的路径，保证通知语义与状态一致
        SetCurrent(null);
    }

    /// <inheritdoc />
    public event Action<DataSourceConfig?>? CurrentChanged;

    /// <summary>
    /// 在 UI 线程派发当前连接变更通知；调用线程已是 UI 线程时直接执行。
    /// </summary>
    /// <param name="snapshot">变更后的当前连接快照，清除时为 null。</param>
    private void Publish(DataSourceConfig? snapshot)
    {
        if (_dispatcher.CheckAccess())
        {
            CurrentChanged?.Invoke(snapshot);
            return;
        }

        // 非 UI 线程调用时以异步方式把通知投递到 UI 线程执行，不阻塞调用方
        _dispatcher.BeginInvoke(() => CurrentChanged?.Invoke(snapshot));
    }

    /// <summary>
    /// 复制数据源配置快照，字段均为值类型或不可变字符串，逐字段拷贝即为有效深拷贝。
    /// </summary>
    /// <param name="source">源数据源配置。</param>
    /// <returns>与源对象等值的新数据源配置实例。</returns>
    private static DataSourceConfig CloneConfig(DataSourceConfig source)
    {
        return new DataSourceConfig
        {
            Name = source.Name,
            Type = source.Type,
            Host = source.Host,
            Port = source.Port,
            Database = source.Database,
            UserId = source.UserId,
            PasswordCipher = source.PasswordCipher,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt
        };
    }
}
