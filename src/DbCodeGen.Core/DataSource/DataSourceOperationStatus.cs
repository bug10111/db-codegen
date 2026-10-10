namespace DbCodeGen.Core.DataSource;

/// <summary>
/// 数据源连接管理操作结果状态。
/// </summary>
public enum DataSourceOperationStatus
{
    /// <summary>操作成功并已落盘。</summary>
    Success = 0,

    /// <summary>字段校验失败（名称/主机/库名/用户名为空或端口越界），未触碰配置。</summary>
    Invalid = 1,

    /// <summary>连接名称与既有连接冲突（忽略大小写），未触碰配置。</summary>
    NameConflict = 2,

    /// <summary>编辑目标（原名称对应的连接）已不存在，未触碰配置。</summary>
    OriginalMissing = 3,

    /// <summary>删除目标连接不存在，未触碰配置。</summary>
    NotFound = 4,

    /// <summary>配置落盘失败，内存快照已回滚到操作前状态。</summary>
    SaveFailed = 5,
}
