namespace DbCodeGen.Core.Model;

/// <summary>
/// 数据库原始类型到目标语言类型的映射条目，存于全局配置（AppConfig.TypeMappings），
/// 由用户经"类型映射"窗口增删改，随配置导入导出与备份恢复一并持久化。
/// 匹配时大小写不敏感，并去除长度/精度等括号后缀修饰。
/// </summary>
public sealed class TypeMappingEntry
{
    /// <summary>
    /// 数据库原始类型，如 bigint、varchar、timestamp with time zone。
    /// </summary>
    public string DbType { get; set; } = string.Empty;

    /// <summary>
    /// 目标语言类型，如 Long、BigDecimal、LocalDateTime。
    /// </summary>
    public string TargetType { get; set; } = string.Empty;

    /// <summary>
    /// 可选导包，如 java.math.BigDecimal；无导包需求时为空。
    /// </summary>
    public string? Import { get; set; }

    /// <summary>
    /// 可选备注，如"MySQL 大整数"，供映射窗口展示说明。
    /// </summary>
    public string? Remark { get; set; }
}
