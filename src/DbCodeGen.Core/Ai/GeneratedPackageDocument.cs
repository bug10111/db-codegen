using System.Text.Json;

namespace DbCodeGen.Core.Ai;

/// <summary>
/// AI 输出解析中间模型，由 LLM 返回的模板包 JSON 解析而来，落盘前映射为 template.json 结构。
/// 解析时兼容 markdown 代码围栏包裹，并对字段做归一化兜底。
/// </summary>
public sealed class GeneratedPackageDocument
{
    /// <summary>
    /// 模板包名，须符合目录名规则且不与模板库已有包冲突（内置包同名拒绝、用户包同名覆盖确认）。
    /// </summary>
    public string PackageName { get; set; } = string.Empty;

    /// <summary>
    /// 模板包说明，列表展示用。
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 基础包名，可填完整包名（含模块段，如 com.example.common），可为空，生成时注入渲染上下文 package 侧。
    /// </summary>
    public string? BasePackage { get; set; }

    /// <summary>
    /// 数据库原始类型到目标语言类型的映射表，供渲染侧 TypeMapper 消费。
    /// </summary>
    public Dictionary<string, string> TypeMap { get; set; } = new();

    /// <summary>
    /// 模板文件清单，每个条目声明相对路径、输出路径、默认勾选态与模板内容。
    /// </summary>
    public List<PackageFile> Files { get; set; } = new();

    /// <summary>
    /// 解析选项：camelCase 命名且属性名大小写不敏感。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 从 LLM 原始回复文本解析模板包文档，兼容 markdown 代码围栏包裹。
    /// </summary>
    /// <param name="json">LLM 返回的模板包 JSON 文本。</param>
    /// <returns>解析并规范化后的模板包文档。</returns>
    /// <exception cref="FormatException">JSON 缺失必要字段或结构不合法时抛出。</exception>
    public static GeneratedPackageDocument Parse(string json)
    {
        string cleaned = StripCodeFence(json);
        GeneratedPackageDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<GeneratedPackageDocument>(cleaned, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new FormatException($"模板包 JSON 解析失败：{exception.Message}", exception);
        }

        if (document is null)
        {
            throw new FormatException("模板包 JSON 解析结果为空。");
        }

        Normalize(document);
        return document;
    }

    /// <summary>
    /// 去除 markdown 代码围栏，LLM 常以 ```json 包裹输出，须剥离后才能解析。
    /// </summary>
    /// <param name="content">LLM 返回的原始文本。</param>
    /// <returns>去除围栏后的 JSON 文本。</returns>
    private static string StripCodeFence(string content)
    {
        string trimmed = (content ?? string.Empty).Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        int bodyStart = trimmed.IndexOf('\n');
        int fenceEnd = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (bodyStart < 0 || fenceEnd <= bodyStart)
        {
            return trimmed;
        }

        return trimmed[(bodyStart + 1)..fenceEnd].Trim();
    }

    /// <summary>
    /// 归一化字段：去除首尾空白、兜底空集合、补齐文件内容，并校验必要字段。
    /// </summary>
    /// <param name="document">解析后的模板包文档。</param>
    /// <exception cref="FormatException">缺少 packageName 或 files 为空时抛出。</exception>
    private static void Normalize(GeneratedPackageDocument document)
    {
        document.PackageName = (document.PackageName ?? string.Empty).Trim();
        document.Description = (document.Description ?? string.Empty).Trim();
        document.BasePackage = string.IsNullOrWhiteSpace(document.BasePackage) ? null : document.BasePackage.Trim();
        document.TypeMap ??= new Dictionary<string, string>();
        document.Files ??= new List<PackageFile>();

        foreach (PackageFile file in document.Files)
        {
            if (file is null)
            {
                throw new FormatException("生成包 files 中存在空条目。");
            }

            file.Name = (file.Name ?? string.Empty).Trim();
            file.RelativeOutputPath = (file.RelativeOutputPath ?? string.Empty).Trim();
            file.Content ??= string.Empty;
        }

        if (string.IsNullOrWhiteSpace(document.PackageName))
        {
            throw new FormatException("生成包缺少 packageName 字段。");
        }

        if (document.Files.Count == 0)
        {
            throw new FormatException("生成包 files 不能为空，至少需要一个模板文件。");
        }
    }
}
