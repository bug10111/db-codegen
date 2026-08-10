using System.Text;

namespace DbCodeGen.Core.Model;

/// <summary>
/// 表元数据实体，贯穿模板渲染上下文、AI 样例与批量生成。表清单阶段只填充
/// 表名/库名/注释，列相关集合保持为空；列元数据在消费时经 TableCatalogService 惰性填充。
/// 同时承载表名到类名/变量名的转换规则，供读取方在构建实体时实时计算。
/// </summary>
public class TableInfo
{
    /// <summary>
    /// 原始表名，与数据库中的实际表名一致。
    /// </summary>
    public string RawName { get; set; } = string.Empty;

    /// <summary>
    /// 所属 schema/库名，跨库查询时使用。
    /// </summary>
    public string? SchemaName { get; set; }

    /// <summary>
    /// 类名，PascalCase，读取时按表名实时转换，生成用。
    /// </summary>
    public string ClassName { get; set; } = string.Empty;

    /// <summary>
    /// 变量名，camelCase，读取时按表名实时转换，模板变量与输出目录用。
    /// </summary>
    public string VariableName { get; set; } = string.Empty;

    /// <summary>
    /// 表注释，数据库无注释时为 null。
    /// </summary>
    public string? Comment { get; set; }

    /// <summary>
    /// 表创建时间；数据库无法可靠提供（如 PostgreSQL）时为 null。
    /// 表清单阶段按库查询填充，表详情阶段随注释一并读取补齐。
    /// </summary>
    public DateTime? CreatedTime { get; set; }

    /// <summary>
    /// 新建先后顺序键，越大表示表越新，供无创建时间的数据库（如 PostgreSQL）近似"新表优先"排序。
    /// PostgreSQL 取 pg_class.oid（同一数据库内随建表递增）；MySQL 有真实创建时间时不依赖本字段，保持 0。
    /// </summary>
    public long CreationOrder { get; set; }

    /// <summary>
    /// 所属数据库类型，由表元数据服务读取时打标；null 表示未知，类型映射解析时仅命中通用条目。
    /// 供类型映射按数据库类型分桶匹配，保证 MySQL 与 PostgreSQL 各自的类型名互不串用。
    /// </summary>
    public DataSourceType? DatabaseType { get; set; }

    /// <summary>
    /// 列集合，惰性填充，表清单阶段为空。
    /// </summary>
    public List<ColumnInfo> Columns { get; set; } = new();

    /// <summary>
    /// 主键列集合，与 Columns 同源，模板遍历主键列用。
    /// </summary>
    public List<ColumnInfo> PrimaryKeys { get; set; } = new();

    /// <summary>
    /// 全量列集合，与 Columns 同源，模板遍历全部列用。
    /// </summary>
    public List<ColumnInfo> FullColumn { get; set; } = new();

    /// <summary>
    /// 非主键列集合，与 Columns 同源。
    /// </summary>
    public List<ColumnInfo> OtherColumn { get; set; } = new();

    /// <summary>
    /// 将原始表名转换为类名（PascalCase），按分隔符与大小写边界拆分单词后首字母大写拼接。
    /// </summary>
    /// <param name="rawName">原始表名。</param>
    /// <returns>转换后的类名，空输入返回空串。</returns>
    public static string ToPascalCase(string rawName)
    {
        if (string.IsNullOrEmpty(rawName))
        {
            return string.Empty;
        }

        string[] words = SplitWords(rawName);
        return string.Concat(words.Select(ToTitleWord));
    }

    /// <summary>
    /// 将原始表名转换为变量名（camelCase），首段保持小写，后续段首字母大写。
    /// </summary>
    /// <param name="rawName">原始表名。</param>
    /// <returns>转换后的变量名，空输入返回空串。</returns>
    public static string ToCamelCase(string rawName)
    {
        if (string.IsNullOrEmpty(rawName))
        {
            return string.Empty;
        }

        string[] words = SplitWords(rawName);
        if (words.Length == 0)
        {
            return string.Empty;
        }

        return words[0].ToLowerInvariant() + string.Concat(words.Skip(1).Select(ToTitleWord));
    }

    /// <summary>
    /// 填充列集合并派生主键/全量/非主键子集，全量列与列集合同源，主键与非主键按标记拆分。
    /// </summary>
    /// <param name="columns">当前表的全部列元数据。</param>
    public void SetColumns(IEnumerable<ColumnInfo> columns)
    {
        List<ColumnInfo> allColumns = columns.ToList();
        Columns = allColumns;
        FullColumn = allColumns;
        PrimaryKeys = allColumns.Where(column => column.IsPrimaryKey).ToList();
        OtherColumn = allColumns.Where(column => !column.IsPrimaryKey).ToList();
    }

    /// <summary>
    /// 将原始名拆分为单词段，以非字母数字字符为分隔，并在小写/数字转大写的边界处断词。
    /// </summary>
    /// <param name="rawName">原始名称。</param>
    /// <returns>拆分后的单词数组。</returns>
    private static string[] SplitWords(string rawName)
    {
        var words = new List<string>();
        var current = new StringBuilder();

        // 逐字符扫描：字母数字累积进当前单词段，大写字母紧接小写/数字结尾时视为驼峰新词起点
        foreach (char character in rawName)
        {
            if (char.IsLetterOrDigit(character))
            {
                bool startNewWord = char.IsUpper(character)
                    && current.Length > 0
                    && (char.IsLower(current[^1]) || char.IsDigit(current[^1]));
                if (startNewWord)
                {
                    // 前一段已结束，先落盘已累积单词再开启新词段
                    words.Add(current.ToString());
                    current.Clear();
                }
                current.Append(character);
            }
            else if (current.Length > 0)
            {
                // 非字母数字字符视为分隔符，落盘当前单词段并重置
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words.ToArray();
    }

    /// <summary>
    /// 将单词段转换为标题形式，首字母大写、其余字母小写。
    /// </summary>
    /// <param name="word">单词段。</param>
    /// <returns>标题形式的单词。</returns>
    private static string ToTitleWord(string word)
    {
        if (word.Length == 0)
        {
            return string.Empty;
        }

        return char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
    }
}
