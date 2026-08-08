namespace DbCodeGen.Core.Model;

/// <summary>
/// 查询结果列定义，供结果表格表头展示列名与类型。
/// </summary>
public class SqlColumnInfo
{
    /// <summary>
    /// 列名，取自 reader.GetName。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 显示类型，取自 reader.GetFieldType 的名称简化，供表头展示。
    /// </summary>
    public string DisplayType { get; set; } = string.Empty;
}
