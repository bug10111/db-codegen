using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板预设解析器：校验并归一 manifest.presets 声明，按预设名称解析模板文件集合与默认变量，
/// 并生成列表展示用摘要。预设名称大小写不敏感（清单内按大小写不敏感唯一性校验）。
/// </summary>
public static class TemplatePresetResolver
{
    /// <summary>
    /// 条目选择口径：包内全部启用条目（缺省）。
    /// </summary>
    public const string IncludeEnabled = "enabled";

    /// <summary>
    /// 条目选择口径：包内全部条目（含 enabled=false）。
    /// </summary>
    public const string IncludeAll = "all";

    /// <summary>
    /// 预设名称合法标识符规则：字母/下划线开头，后续允许字母、数字、下划线、中划线。
    /// </summary>
    private static readonly Regex PresetNamePattern = new(
        "^[A-Za-z_][A-Za-z0-9_-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 校验并归一清单 presets 声明：名称/说明/选择口径/文件存在性/变量键逐项校验，
    /// 未声明 include 与 files 时按 enabled 口径补缺省。非法声明抛模板包异常并指明 preset 名称。
    /// </summary>
    /// <param name="presets">原始 presets 声明，可为空。</param>
    /// <param name="files">已归一化的清单文件条目（用于校验 preset.files 引用的模板存在性）。</param>
    /// <returns>归一化后的预设字典（大小写不敏感键），无声明时为空字典。</returns>
    /// <exception cref="TemplatePackageException">预设名称非法/重复、说明缺失、选择口径非法或互斥声明、
    /// 文件清单为空或引用未声明模板、变量键空白或重复时抛出。</exception>
    public static Dictionary<string, TemplatePresetDefinition> Normalize(
        Dictionary<string, TemplatePresetDefinition>? presets,
        IReadOnlyList<TemplateFileEntry> files)
    {
        var normalized = new Dictionary<string, TemplatePresetDefinition>(StringComparer.OrdinalIgnoreCase);
        if (presets is null || presets.Count == 0)
        {
            return normalized;
        }

        // 清单模板路径集合：preset.files 只能引用清单已声明条目（允许 disabled），不允许凭空追加
        HashSet<string> knownTemplates = new(StringComparer.OrdinalIgnoreCase);
        foreach (TemplateFileEntry entry in files)
        {
            if (!string.IsNullOrWhiteSpace(entry.Template))
            {
                knownTemplates.Add(TemplatePackageLoader.NormalizeRelativePath(entry.Template));
            }
        }

        foreach (KeyValuePair<string, TemplatePresetDefinition> pair in presets)
        {
            string name = (pair.Key ?? string.Empty).Trim();
            if (!PresetNamePattern.IsMatch(name))
            {
                throw new TemplatePackageException(
                    $"模板包清单 presets 名称非法：{pair.Key}；须为字母/下划线开头，仅含字母、数字、下划线、中划线。");
            }

            TemplatePresetDefinition? definition = pair.Value;
            if (definition is null)
            {
                throw new TemplatePackageException($"模板包清单 presets.{name} 定义为空。");
            }

            if (!normalized.TryAdd(name, NormalizeDefinition(name, definition, files, knownTemplates)))
            {
                throw new TemplatePackageException($"模板包清单 presets 存在名称重复项（大小写不敏感）：{name}。");
            }
        }

        return normalized;
    }

    /// <summary>
    /// 按名称解析预设对应的模板文件集合：files 声明逐条映射；include=all 取全部条目；
    /// include=enabled（缺省）取全部启用条目。未知/空名称与空结果均结构化拒绝并列出可用预设。
    /// </summary>
    /// <param name="package">模板包运行时信息（presets 已由加载器归一）。</param>
    /// <param name="presetName">预设名称，大小写不敏感。</param>
    /// <returns>参与生成的模板文件运行时信息集合（保序）。</returns>
    /// <exception cref="ArgumentException">预设不存在、未解析出任何模板或引用模板缺失时抛出。</exception>
    public static IReadOnlyList<TemplateFileInfo> ResolveFiles(TemplatePackageInfo package, string presetName)
    {
        ArgumentNullException.ThrowIfNull(package);
        TemplatePresetDefinition definition = RequireDefinition(package, presetName);

        var resolved = new List<TemplateFileInfo>();
        if (definition.Files is not null)
        {
            foreach (string path in definition.Files)
            {
                TemplateFileInfo? entry = FindFile(package, path);
                if (entry is null)
                {
                    throw new ArgumentException(
                        $"预设 {presetName} 引用的模板 {path} 不在包 {package.Name} 清单中；请检查 template.json presets.{presetName}.files。");
                }

                resolved.Add(entry);
            }
        }
        else if (string.Equals(definition.Include, IncludeAll, StringComparison.OrdinalIgnoreCase))
        {
            resolved.AddRange(package.Files);
        }
        else
        {
            foreach (TemplateFileInfo file in package.Files)
            {
                if (file.IsEnabled)
                {
                    resolved.Add(file);
                }
            }
        }

        if (resolved.Count == 0)
        {
            throw new ArgumentException(
                $"预设 {presetName} 未解析出任何模板（包 {package.Name} 可能没有启用条目）；请改用 files 显式清单或先启用模板。");
        }

        return resolved;
    }

    /// <summary>
    /// 按名称解析预设默认变量：返回逐键克隆的 JSON 值字典（调用方显式 variables 同名键优先覆盖）。
    /// </summary>
    /// <param name="package">模板包运行时信息。</param>
    /// <param name="presetName">预设名称，大小写不敏感。</param>
    /// <returns>预设默认变量字典；未声明变量时为空字典。</returns>
    /// <exception cref="ArgumentException">预设不存在时抛出。</exception>
    public static IReadOnlyDictionary<string, JsonElement> ResolveVariables(TemplatePackageInfo package, string presetName)
    {
        ArgumentNullException.ThrowIfNull(package);
        TemplatePresetDefinition definition = RequireDefinition(package, presetName);
        var variables = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (definition.Variables is null)
        {
            return variables;
        }

        foreach (KeyValuePair<string, JsonElement> pair in definition.Variables)
        {
            variables[pair.Key] = pair.Value.Clone();
        }

        return variables;
    }

    /// <summary>
    /// 生成预设摘要清单（名称/说明/解析后文件数），保持清单声明顺序；供模板包列表、
    /// 模板列表与模板读取回执透出。
    /// </summary>
    /// <param name="package">模板包运行时信息。</param>
    /// <returns>预设摘要清单；包未声明 presets 时为空列表。</returns>
    public static IReadOnlyList<TemplatePresetSummary> GetSummaries(TemplatePackageInfo package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var summaries = new List<TemplatePresetSummary>(package.Presets.Count);
        foreach (KeyValuePair<string, TemplatePresetDefinition> pair in package.Presets)
        {
            IReadOnlyList<TemplateFileInfo> files = ResolveFiles(package, pair.Key);
            summaries.Add(new TemplatePresetSummary(pair.Key, pair.Value.Description, files.Count));
        }

        return summaries;
    }

    /// <summary>
    /// 生成可用预设名称清单文本（错误消息与工具回执用），无预设时返回「（无）」。
    /// </summary>
    /// <param name="package">模板包运行时信息。</param>
    /// <returns>逗号分隔的预设名称文本。</returns>
    public static string DescribeAvailable(TemplatePackageInfo package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Presets.Count == 0)
        {
            return "（无）";
        }

        return string.Join(", ", package.Presets.Keys);
    }

    /// <summary>
    /// 归一单条预设定义：说明必填、include/files 二选一、文件路径归一与存在性校验、变量键清理。
    /// </summary>
    /// <param name="name">已校验的预设名称。</param>
    /// <param name="definition">原始定义。</param>
    /// <param name="files">清单文件条目（用于校验选择口径至少能解析出一个条目）。</param>
    /// <param name="knownTemplates">清单已声明模板路径集合（大小写不敏感）。</param>
    /// <returns>归一化后的定义。</returns>
    private static TemplatePresetDefinition NormalizeDefinition(
        string name,
        TemplatePresetDefinition definition,
        IReadOnlyList<TemplateFileEntry> files,
        HashSet<string> knownTemplates)
    {
        string description = (definition.Description ?? string.Empty).Trim();
        if (description.Length == 0)
        {
            throw new TemplatePackageException($"模板包清单 presets.{name}.description 必填。");
        }

        string? include = null;
        if (!string.IsNullOrWhiteSpace(definition.Include))
        {
            include = definition.Include.Trim().ToLowerInvariant();
            if (include is not (IncludeEnabled or IncludeAll))
            {
                throw new TemplatePackageException(
                    $"模板包清单 presets.{name}.include 取值非法：{definition.Include}；合法取值为 enabled / all。");
            }
        }

        List<string>? presetFiles = null;
        if (definition.Files is not null)
        {
            if (include is not null)
            {
                throw new TemplatePackageException(
                    $"模板包清单 presets.{name} 的 include 与 files 互斥：二选一声明条目选择口径。");
            }

            if (definition.Files.Count == 0)
            {
                throw new TemplatePackageException($"模板包清单 presets.{name}.files 为空清单，至少声明一个模板路径。");
            }

            presetFiles = new List<string>(definition.Files.Count);
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (string rawPath in definition.Files)
            {
                string path = TemplatePackageLoader.NormalizeRelativePath(rawPath);
                if (path.Length == 0)
                {
                    throw new TemplatePackageException($"模板包清单 presets.{name}.files 含空白模板路径。");
                }

                if (!knownTemplates.Contains(path))
                {
                    throw new TemplatePackageException(
                        $"模板包清单 presets.{name}.files 引用的模板不在清单 files[] 中：{path}。");
                }

                if (!seen.Add(path))
                {
                    throw new TemplatePackageException($"模板包清单 presets.{name}.files 含重复模板路径：{path}。");
                }

                presetFiles.Add(path);
            }
        }

        Dictionary<string, JsonElement>? variables = null;
        if (definition.Variables is not null && definition.Variables.Count > 0)
        {
            variables = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, JsonElement> pair in definition.Variables)
            {
                string key = (pair.Key ?? string.Empty).Trim();
                if (key.Length == 0)
                {
                    throw new TemplatePackageException($"模板包清单 presets.{name}.variables 含空白键。");
                }

                if (!variables.TryAdd(key, pair.Value.Clone()))
                {
                    throw new TemplatePackageException($"模板包清单 presets.{name}.variables 键重复：{key}。");
                }
            }
        }

        // 选择口径必须至少解析出一个条目：空包或全部未启用时 preset 不可用，加载期即拒绝
        string effectiveInclude = include ?? IncludeEnabled;
        if (presetFiles is null)
        {
            bool resolvesAny = effectiveInclude == IncludeAll
                ? files.Count > 0
                : files.Any(entry => entry.Enabled);
            if (!resolvesAny)
            {
                throw new TemplatePackageException(
                    $"模板包清单 presets.{name} 解析不出任何模板（include={effectiveInclude}）；请启用模板或改用 files 显式清单。");
            }
        }

        // files 口径下 include 恒为空；include/files 均未声明时补缺省 enabled，保证解析口径唯一
        return new TemplatePresetDefinition
        {
            Description = description,
            Include = presetFiles is null ? effectiveInclude : null,
            Files = presetFiles,
            Variables = variables
        };
    }

    /// <summary>
    /// 按名称查找预设定义（大小写不敏感）；未命中时抛参数异常并列出可用预设。
    /// </summary>
    /// <param name="package">模板包运行时信息。</param>
    /// <param name="presetName">预设名称。</param>
    /// <returns>命中的预设定义。</returns>
    private static TemplatePresetDefinition RequireDefinition(TemplatePackageInfo package, string presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName))
        {
            throw new ArgumentException(
                $"preset 不能为空；包 {package.Name} 可用 presets：{DescribeAvailable(package)}。");
        }

        string name = presetName.Trim();
        foreach (KeyValuePair<string, TemplatePresetDefinition> pair in package.Presets)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        throw new ArgumentException(
            $"未知 preset：{name}；包 {package.Name} 可用 presets：{DescribeAvailable(package)}。"
            + "请先经 list_template_packages / list_templates 查看 presets，或省略 preset 使用全部启用模板。");
    }

    /// <summary>
    /// 在包内模板条目中按相对路径查找（大小写不敏感，反斜杠归一为正斜杠）。
    /// </summary>
    /// <param name="package">模板包运行时信息。</param>
    /// <param name="path">模板相对路径。</param>
    /// <returns>命中的条目；未命中返回 null。</returns>
    private static TemplateFileInfo? FindFile(TemplatePackageInfo package, string path)
    {
        string normalized = TemplatePackageLoader.NormalizeRelativePath(path);
        foreach (TemplateFileInfo file in package.Files)
        {
            if (string.Equals(file.RelativeTemplatePath, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        return null;
    }
}
