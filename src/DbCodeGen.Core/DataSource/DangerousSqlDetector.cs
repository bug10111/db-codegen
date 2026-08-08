using System.Text.RegularExpressions;

namespace DbCodeGen.Core.DataSource;

/// <summary>
/// 危险语句检测器，在执行前识别 DROP/TRUNCATE 与无顶层 WHERE 的 DELETE/UPDATE，供确认弹窗明示风险。
/// 检测规则：剥离注释与字符串字面量后取语句首关键字，大小写不敏感匹配；仅作安全网，不替代用户判断。
/// </summary>
public static class DangerousSqlDetector
{
    /// <summary>
    /// 单引号字符串字面量，含两个单引号转义，剥离后关键字判定不受字面量内容干扰。
    /// </summary>
    private static readonly Regex StringLiteralRegex = new(
        @"'(?:[^']|'')*'", RegexOptions.Compiled);

    /// <summary>
    /// MySQL 反引号标识符，含两个反引号转义，剥离后防止标识符内的关键字参与判定。
    /// </summary>
    private static readonly Regex BacktickIdentifierRegex = new(
        @"`(?:[^`]|``)*`", RegexOptions.Compiled);

    /// <summary>
    /// 块注释，可跨行，剥离后语句首关键字不受注释干扰。
    /// </summary>
    private static readonly Regex BlockCommentRegex = new(
        @"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// 行注释，覆盖标准 -- 与 MySQL 方言 #，剥离到行尾。
    /// </summary>
    private static readonly Regex LineCommentRegex = new(
        @"(?:--[^\r\n]*|#[^\r\n]*)", RegexOptions.Compiled);

    /// <summary>
    /// 连续空白折叠为单个空格，便于提取语句首关键字。
    /// </summary>
    private static readonly Regex WhitespaceRegex = new(
        @"\s+", RegexOptions.Compiled);

    /// <summary>
    /// 顶层 WHERE 关键字匹配，词边界限定避免误命中 where_column 等标识符。
    /// </summary>
    private static readonly Regex WhereKeywordRegex = new(
        @"\bWHERE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 检测 SQL 语句是否属于需确认的危险类型。
    /// </summary>
    /// <param name="sql">待检测的 SQL 文本，可为空串或纯空白。</param>
    /// <returns>危险语句类型，安全语句返回 None。</returns>
    public static DangerousSqlKind Detect(string sql)
    {
        // 空白语句无关键字可判定，直接视为安全
        if (string.IsNullOrWhiteSpace(sql))
        {
            return DangerousSqlKind.None;
        }

        string processed = Preprocess(sql);
        string firstKeyword = ExtractFirstKeyword(processed);
        switch (firstKeyword)
        {
            case "DROP":
                return DangerousSqlKind.Drop;
            case "TRUNCATE":
                return DangerousSqlKind.Truncate;
            case "DELETE":
                // 无顶层 WHERE 的 DELETE 需确认，含顶层 WHERE 视为已限定范围
                return WhereKeywordRegex.IsMatch(processed)
                    ? DangerousSqlKind.None
                    : DangerousSqlKind.DeleteWithoutWhere;
            case "UPDATE":
                // 无顶层 WHERE 的 UPDATE 需确认，含顶层 WHERE 视为已限定范围
                return WhereKeywordRegex.IsMatch(processed)
                    ? DangerousSqlKind.None
                    : DangerousSqlKind.UpdateWithoutWhere;
            default:
                return DangerousSqlKind.None;
        }
    }

    /// <summary>
    /// 判断 SQL 语句是否属于危险语句，供调用方快速分流。
    /// </summary>
    /// <param name="sql">待检测的 SQL 文本。</param>
    /// <returns>危险语句返回 true，否则返回 false。</returns>
    public static bool IsDangerous(string sql) => Detect(sql) != DangerousSqlKind.None;

    /// <summary>
    /// 获取危险语句类型对应的风险描述，供确认弹窗明示风险。
    /// </summary>
    /// <param name="kind">危险语句类型。</param>
    /// <returns>风险描述文本，安全类型返回空串。</returns>
    public static string GetRiskDescription(DangerousSqlKind kind)
    {
        return kind switch
        {
            DangerousSqlKind.Drop => "将执行 DROP 语句，删除表或其它数据库对象，该操作不可恢复，确定继续执行吗？",
            DangerousSqlKind.Truncate => "将执行 TRUNCATE 语句，清空表中全部数据，该操作不可恢复，确定继续执行吗？",
            DangerousSqlKind.DeleteWithoutWhere => "将执行不带 WHERE 条件的 DELETE 语句，会删除表中全部数据，确定继续执行吗？",
            DangerousSqlKind.UpdateWithoutWhere => "将执行不带 WHERE 条件的 UPDATE 语句，会更新表中全部数据，确定继续执行吗？",
            _ => string.Empty
        };
    }

    /// <summary>
    /// 预处理 SQL 文本：依次剥离字符串字面量、反引号标识符与注释，折叠空白并转为大写。
    /// </summary>
    /// <param name="sql">原始 SQL 文本。</param>
    /// <returns>预处理后的文本，用于关键字判定。</returns>
    private static string Preprocess(string sql)
    {
        // 先剥离字符串与标识符，再剥离注释，避免注释内的引号或引号内的注释干扰后续匹配
        string stripped = StringLiteralRegex.Replace(sql, string.Empty);
        stripped = BacktickIdentifierRegex.Replace(stripped, string.Empty);
        stripped = BlockCommentRegex.Replace(stripped, string.Empty);
        stripped = LineCommentRegex.Replace(stripped, string.Empty);
        stripped = WhitespaceRegex.Replace(stripped, " ");
        return stripped.ToUpperInvariant();
    }

    /// <summary>
    /// 取预处理后文本的第一个空白分隔片段作为语句首关键字，先去掉前导空白。
    /// </summary>
    /// <param name="processed">已折叠空白并转大写的文本。</param>
    /// <returns>语句首关键字，无分隔时返回整个文本。</returns>
    private static string ExtractFirstKeyword(string processed)
    {
        // 剥离注释后文本可能以空白开头，先去掉前导空白再取首个片段
        string trimmed = processed.TrimStart();
        int spaceIndex = trimmed.IndexOf(' ');
        return spaceIndex < 0 ? trimmed : trimmed[..spaceIndex];
    }
}
