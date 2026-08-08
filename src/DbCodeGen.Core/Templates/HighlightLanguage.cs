namespace DbCodeGen.Core.Templates;

/// <summary>
/// 模板文件高亮语言枚举，由模板文件名按 <名称>.<语言段>.scriban 约定推导。
/// 目标语言决定 AvalonEdit 编辑器的代码高亮规则，Plain 表示未识别仅保留 Scriban 标签高亮。
/// </summary>
public enum HighlightLanguage
{
    /// <summary>
    /// 目标语言为 Java，对应文件名语言段 java。
    /// </summary>
    Java,

    /// <summary>
    /// 目标语言为 C#，对应文件名语言段 csharp 或 cs。
    /// </summary>
    CSharp,

    /// <summary>
    /// 目标语言为 XML，对应文件名语言段 xml。
    /// </summary>
    Xml,

    /// <summary>
    /// 目标语言为 SQL，对应文件名语言段 sql。
    /// </summary>
    Sql,

    /// <summary>
    /// 目标语言为 JSON，对应文件名语言段 json。
    /// </summary>
    Json,

    /// <summary>
    /// 未识别语言段，仅保留 Scriban 标签高亮。
    /// </summary>
    Plain
}

/// <summary>
/// 高亮语言判定工具，按模板文件名 <名称>.<语言段>.scriban 约定推导目标语言。
/// 判定规则为纯函数，供模板编辑器与高亮定义构建方复用。
/// </summary>
public static class HighlightLanguageResolver
{
    /// <summary>
    /// 按模板文件名推导高亮语言；取最后一个点号与 .scriban 后缀之间的语言段映射，
    /// 无 .scriban 后缀或语言段不在识别集内时返回 Plain。
    /// </summary>
    /// <param name="fileName">模板文件名，如 entity.java.scriban、mapper.xml.scriban。</param>
    /// <returns>推导出的高亮语言。</returns>
    public static HighlightLanguage FromTemplateFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return HighlightLanguage.Plain;
        }

        // 去掉 .scriban 后缀后取最后一个点号后的语言段，映射到枚举；未命中返回 Plain
        string name = fileName.Trim();
        if (name.EndsWith(".scriban", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".scriban".Length];
        }

        int lastDot = name.LastIndexOf('.');
        if (lastDot < 0 || lastDot == name.Length - 1)
        {
            return HighlightLanguage.Plain;
        }

        return name[(lastDot + 1)..].ToLowerInvariant() switch
        {
            "java" => HighlightLanguage.Java,
            "csharp" or "cs" => HighlightLanguage.CSharp,
            "xml" => HighlightLanguage.Xml,
            "sql" => HighlightLanguage.Sql,
            "json" => HighlightLanguage.Json,
            _ => HighlightLanguage.Plain
        };
    }
}
