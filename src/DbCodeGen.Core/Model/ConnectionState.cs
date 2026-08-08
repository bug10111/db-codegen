namespace DbCodeGen.Core.Model;

/// <summary>
/// 当前连接的生命周期状态，供工具栏下拉展示与表浏览/SQL 面板联动判定。
/// </summary>
public enum ConnectionState
{
    /// <summary>
    /// 未连接，初始状态。
    /// </summary>
    Disconnected,

    /// <summary>
    /// 连接中，选择数据源或刷新表时进入。
    /// </summary>
    Connecting,

    /// <summary>
    /// 已连接，连接成功且元数据读取成功。
    /// </summary>
    Connected,

    /// <summary>
    /// 连接失败，超时或认证失败。
    /// </summary>
    Failed
}
