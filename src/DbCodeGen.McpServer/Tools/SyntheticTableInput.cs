using System.ComponentModel;
using DbCodeGen.Core.Model;

namespace DbCodeGen.McpServer.Tools;

/// <summary>
/// 合成表输入模型：MCP 调用方显式构造的表元数据，供单模板渲染与批量生成预览/写盘消费，
/// 无需连接真实数据库。类名/变量名/属性名由服务端按原始名派生，与数据库读取侧转换规则一致，
/// 调用方只提供不可推导的原始信息。
/// </summary>
public sealed class SyntheticTableInput
{
    /// <summary>
    /// 原始表名，与数据库中的实际表名一致（如 t_user_info），必填。
    /// </summary>
    [Description("原始表名，如 t_user_info；类名/变量名由服务端自动派生")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 所属 schema/库名，跨库场景使用，可省略。
    /// </summary>
    [Description("所属 schema/库名，可省略")]
    public string? SchemaName { get; set; }

    /// <summary>
    /// 表注释，无注释可省略。
    /// </summary>
    [Description("表注释，可省略")]
    public string? Comment { get; set; }

    /// <summary>
    /// 列元数据集合，可省略：省略时表按"仅表壳无列"参与渲染（实体类等按列循环的模板将输出空列清单）；
    /// 需要完整列时建议先用 get_table_detail 从真实数据源取回后传入。
    /// </summary>
    [Description("列元数据集合，可省略；需要完整列时建议先用 get_table_detail 取回后传入")]
    public IReadOnlyList<SyntheticColumnInput>? Columns { get; set; }
}

/// <summary>
/// 合成表/列输入到 Core 表元数据的转换器：按原始名派生类名、变量名与属性名（与数据库读取侧一致），
/// 并派生主键/全量/非主键列子集，供模板渲染与批量生成直接消费。
/// </summary>
internal static class SyntheticTableMapper
{
    /// <summary>
    /// 将合成表输入转换为 Core 表元数据：原始名去空白，类名/变量名按读取侧规则派生，
    /// 列集合并派生主键/全量/非主键子集。
    /// </summary>
    /// <param name="input">合成表输入。</param>
    /// <returns>可直接参与渲染与生成的表元数据。</returns>
    /// <exception cref="ArgumentException">表名为空白或列集合含空列名/空元素时抛出。</exception>
    public static TableInfo ToTableInfo(SyntheticTableInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new ArgumentException("表名（tables[].name）不能为空。");
        }

        string rawName = input.Name.Trim();
        var table = new TableInfo
        {
            RawName = rawName,
            SchemaName = string.IsNullOrWhiteSpace(input.SchemaName) ? null : input.SchemaName.Trim(),
            Comment = string.IsNullOrWhiteSpace(input.Comment) ? null : input.Comment.Trim(),
            ClassName = TableInfo.ToPascalCase(rawName),
            VariableName = TableInfo.ToCamelCase(rawName)
        };

        // 列集合可选：省略时保持空列（表壳），传入时逐列校验并派生主键/全量/非主键子集
        if (input.Columns is not null && input.Columns.Count > 0)
        {
            List<ColumnInfo> columns = new(input.Columns.Count);
            foreach (SyntheticColumnInput columnInput in input.Columns)
            {
                columns.Add(ToColumnInfo(columnInput));
            }

            table.SetColumns(columns);
        }

        return table;
    }

    /// <summary>
    /// 将合成列输入转换为 Core 列元数据：原始列名去空白，属性名按读取侧 camelCase 规则派生。
    /// </summary>
    /// <param name="input">合成列输入。</param>
    /// <returns>列元数据。</returns>
    /// <exception cref="ArgumentException">列名为空白时抛出。</exception>
    public static ColumnInfo ToColumnInfo(SyntheticColumnInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new ArgumentException("列名（columns[].name）不能为空。");
        }

        string rawName = input.Name.Trim();
        return new ColumnInfo
        {
            RawName = rawName,
            PropertyName = TableInfo.ToCamelCase(rawName),
            Comment = string.IsNullOrWhiteSpace(input.Comment) ? null : input.Comment.Trim(),
            RawDbType = input.RawDbType?.Trim() ?? string.Empty,
            IsPrimaryKey = input.IsPrimaryKey,
            AutoIncrement = input.AutoIncrement,
            IsNullable = input.IsNullable,
            DefaultValue = string.IsNullOrWhiteSpace(input.DefaultValue) ? null : input.DefaultValue.Trim(),
            Length = input.Length,
            Precision = input.Precision,
            Scale = input.Scale
        };
    }
}
