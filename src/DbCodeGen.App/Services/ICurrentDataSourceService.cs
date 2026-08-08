using DbCodeGen.Core.Model;

namespace DbCodeGen.App.Services;

/// <summary>
/// 当前连接共享状态服务契约，承载当前数据源连接的选择、清除与变更通知。
/// 当前连接以只读快照形式对外提供，供表浏览与 SQL 执行面板读取默认连接并联动刷新。
/// </summary>
public interface ICurrentDataSourceService
{
    /// <summary>
    /// 当前数据源连接配置快照，未设置当前连接时为 null。
    /// </summary>
    DataSourceConfig? Current { get; }

    /// <summary>
    /// 设置当前连接并触发变更通知；传入 null 等价于清除当前连接。
    /// </summary>
    /// <param name="config">要设为当前连接的数据源配置，可为 null。</param>
    void SetCurrent(DataSourceConfig? config);

    /// <summary>
    /// 清除当前连接并触发变更通知，供删除当前连接时联动调用。
    /// </summary>
    void ClearCurrent();

    /// <summary>
    /// 当前连接变更事件，统一在 UI 线程派发；清除当前连接时以 null 通知消费方。
    /// </summary>
    event Action<DataSourceConfig?>? CurrentChanged;
}
