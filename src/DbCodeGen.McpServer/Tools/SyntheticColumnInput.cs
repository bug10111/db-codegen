using System.ComponentModel;

namespace DbCodeGen.McpServer.Tools;

/// <summary>
/// 合成列输入模型：MCP 调用方显式构造的单列元数据，属性名由服务端按原始列名派生（camelCase），
/// 与数据库读取侧转换规则一致。
/// </summary>
public sealed class SyntheticColumnInput
{
    /// <summary>
    /// 原始列名，与数据库中的实际列名一致（如 user_name），必填。
    /// </summary>
    [Description("原始列名，如 user_name；属性名由服务端自动派生")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 原始数据库类型（如 varchar、bigint），不含长度修饰；默认空串。
    /// </summary>
    [Description("原始数据库类型，如 varchar、bigint，不含长度修饰")]
    public string RawDbType { get; set; } = string.Empty;

    /// <summary>
    /// 列注释，可省略。
    /// </summary>
    [Description("列注释，可省略")]
    public string? Comment { get; set; }

    /// <summary>
    /// 是否主键列，联合主键的每一列均为 true；默认 false。
    /// </summary>
    [Description("是否主键列，联合主键每列均为 true")]
    public bool IsPrimaryKey { get; set; }

    /// <summary>
    /// 是否自增列，默认 false。
    /// </summary>
    [Description("是否自增列")]
    public bool AutoIncrement { get; set; }

    /// <summary>
    /// 是否可空，false 表示非空列；默认 false。
    /// </summary>
    [Description("是否可空，false 表示非空列")]
    public bool IsNullable { get; set; }

    /// <summary>
    /// 列默认值，无默认值可省略。
    /// </summary>
    [Description("列默认值，可省略")]
    public string? DefaultValue { get; set; }

    /// <summary>
    /// 列长度，仅字符等类型有值，可省略。
    /// </summary>
    [Description("列长度，仅字符等类型有值，可省略")]
    public int? Length { get; set; }

    /// <summary>
    /// 精度，numeric/decimal 等类型有值，可省略。
    /// </summary>
    [Description("精度，numeric/decimal 等类型有值，可省略")]
    public int? Precision { get; set; }

    /// <summary>
    /// 小数位，numeric/decimal 等类型有值，可省略。
    /// </summary>
    [Description("小数位，numeric/decimal 等类型有值，可省略")]
    public int? Scale { get; set; }
}
