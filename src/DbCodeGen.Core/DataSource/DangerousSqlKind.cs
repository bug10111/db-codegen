namespace DbCodeGen.Core.DataSource;

/// <summary>
/// 危险语句类型，供确认弹窗明示语句类型与风险。
/// </summary>
public enum DangerousSqlKind
{
    /// <summary>
    /// 安全语句，无需确认。
    /// </summary>
    None,

    /// <summary>
    /// DROP 语句，删除表或其它数据库对象，不可恢复。
    /// </summary>
    Drop,

    /// <summary>
    /// TRUNCATE 语句，清空表中全部数据，不可恢复。
    /// </summary>
    Truncate,

    /// <summary>
    /// 无顶层 WHERE 的 DELETE 语句，会删除表中全部数据。
    /// </summary>
    DeleteWithoutWhere,

    /// <summary>
    /// 无顶层 WHERE 的 UPDATE 语句，会更新表中全部数据。
    /// </summary>
    UpdateWithoutWhere
}
