namespace DbCodeGen.Core.Model;

/// <summary>
/// 列元数据实体，描述单张表的一列原始信息，供模板渲染上下文、AI 样例与预览消费。
/// 只承载数据库原始元数据，不包含 mappedType（映射后类型由渲染侧按当前模板包实时计算），
/// 也不包含索引信息（v1 不读取索引元数据）。
/// </summary>
public class ColumnInfo
{
    /// <summary>
    /// 原始列名，与数据库中的实际列名一致。
    /// </summary>
    public string RawName { get; set; } = string.Empty;

    /// <summary>
    /// 驼峰属性名，读取时按列名实时转换，供模板变量使用。
    /// </summary>
    public string PropertyName { get; set; } = string.Empty;

    /// <summary>
    /// 列注释，数据库无注释时为 null。
    /// </summary>
    public string? Comment { get; set; }

    /// <summary>
    /// 原始数据库类型，如 varchar、bigint、timestamp，不含长度修饰。
    /// </summary>
    public string RawDbType { get; set; } = string.Empty;

    /// <summary>
    /// 是否主键列，联合主键的每一列均为 true。
    /// </summary>
    public bool IsPrimaryKey { get; set; }

    /// <summary>
    /// 是否自增列。
    /// </summary>
    public bool AutoIncrement { get; set; }

    /// <summary>
    /// 是否可空，false 表示非空列。
    /// </summary>
    public bool IsNullable { get; set; }

    /// <summary>
    /// 列的默认值，无默认值时为 null。
    /// </summary>
    public string? DefaultValue { get; set; }

    /// <summary>
    /// 列长度，仅字符等类型有值，其余类型为 null。
    /// </summary>
    public int? Length { get; set; }

    /// <summary>
    /// 精度，numeric/decimal 等类型有值，其余类型为 null。
    /// </summary>
    public int? Precision { get; set; }

    /// <summary>
    /// 小数位，numeric/decimal 等类型有值，其余类型为 null。
    /// </summary>
    public int? Scale { get; set; }
}
