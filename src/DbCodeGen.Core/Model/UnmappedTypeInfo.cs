namespace DbCodeGen.Core.Model;

/// <summary>
/// 生成预检发现的未映射类型引用，供界面弹窗告知用户具体哪个类型缺少映射。
/// 同类型跨表跨列出现时归并为一条，记录首次出现位置与总出现次数。
/// </summary>
public sealed class UnmappedTypeInfo
{
    /// <summary>
    /// 未映射的数据库原始类型，如 jsonb。
    /// </summary>
    public string DbType { get; set; } = string.Empty;

    /// <summary>
    /// 该类型首次出现的表名。
    /// </summary>
    public string TableName { get; set; } = string.Empty;

    /// <summary>
    /// 该类型首次出现的列名。
    /// </summary>
    public string ColumnName { get; set; } = string.Empty;

    /// <summary>
    /// 该类型在本次生成范围内跨表跨列的总出现次数。
    /// </summary>
    public int Occurrences { get; set; }
}
