using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板包加载与校验器：解析 template.json、校验引擎与包名、逐模板文件做防目录穿越与存在性校验，
/// 产出运行时 TemplatePackageInfo。内置包与用户包共用同一套校验规则。
/// </summary>
public static class TemplatePackageLoader
{
    /// <summary>
    /// 模板包清单文件名，约定为包根目录下的 template.json。
    /// </summary>
    public const string ManifestFileName = "template.json";

    /// <summary>
    /// 当前唯一支持的模板引擎。
    /// </summary>
    public const string SupportedEngine = "scriban";

    /// <summary>
    /// 包名校验规则：字母/数字/中划线/下划线组成，且至少含一个字母或数字。
    /// </summary>
    private static readonly Regex PackageNamePattern = new(
        "^[A-Za-z0-9_-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// manifest 序列化选项：camelCase 命名并允许注释与尾逗号，便于人工与 AI 编写清单。
    /// </summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    /// <summary>
    /// 加载并完整校验一个模板包目录。
    /// </summary>
    /// <param name="packageRootPath">模板包根目录绝对路径。</param>
    /// <param name="isBuiltin">是否内置包，内置包将标记为只读。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>校验通过的模板包运行时信息。</returns>
    /// <exception cref="TemplatePackageException">目录不存在、清单缺失/非法、引擎不支持、包名不合法、模板文件缺失或路径穿越时抛出。</exception>
    public static async Task<TemplatePackageInfo> LoadFromDirectoryAsync(
        string packageRootPath,
        bool isBuiltin,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packageRootPath))
        {
            throw new TemplatePackageException("模板包根目录路径为空。");
        }

        string rootFullPath = Path.GetFullPath(packageRootPath);
        if (!Directory.Exists(rootFullPath))
        {
            throw new TemplatePackageException($"模板包根目录不存在：{rootFullPath}");
        }

        string manifestPath = Path.Combine(rootFullPath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new TemplatePackageException($"模板包缺少 {ManifestFileName} 清单文件：{rootFullPath}");
        }

        TemplateManifest manifest = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        TemplateManifest normalized = NormalizeManifest(manifest);

        // 包名、引擎与文件清单的完整性校验
        ValidatePackageIdentity(normalized);

        var files = new List<TemplateFileInfo>(normalized.Files.Count);
        foreach (TemplateFileEntry entry in normalized.Files)
        {
            string templateRelative = NormalizeRelativePath(entry.Template);
            if (templateRelative.Length == 0)
            {
                throw new TemplatePackageException("模板文件路径为空，清单 files[].template 不能为空。");
            }

            // 模板相对路径防目录穿越并解析到包根内绝对路径
            string templateFullPath = ResolveWithinRoot(rootFullPath, templateRelative);
            if (!File.Exists(templateFullPath))
            {
                throw new TemplatePackageException($"模板文件不存在：{templateRelative}");
            }

            // 输出相对路径静态骨架防目录穿越
            if (!IsSafeRelativeSkeleton(entry.Output))
            {
                throw new TemplatePackageException($"模板文件 {templateRelative} 的输出路径不合法（禁止绝对路径、.. 段与盘符前缀）：{entry.Output}");
            }

            files.Add(new TemplateFileInfo
            {
                RelativeTemplatePath = templateRelative,
                TemplatePath = templateFullPath,
                OutputPath = entry.Output,
                IsEnabled = entry.Enabled
            });
        }

        return new TemplatePackageInfo
        {
            Name = normalized.Name,
            Description = normalized.Description,
            Engine = normalized.Engine,
            BasePackage = normalized.BasePackage,
            RootPath = rootFullPath,
            ManifestPath = manifestPath,
            IsBuiltin = isBuiltin,
            ModifiedTime = Directory.GetLastWriteTime(rootFullPath),
            TypeMap = normalized.TypeMap,
            Files = files
        };
    }

    /// <summary>
    /// 校验包名是否符合目录名规则（字母/数字/中划线/下划线，至少一个字母或数字）。
    /// </summary>
    /// <param name="name">待校验的包名。</param>
    /// <returns>包名合法返回 true，否则返回 false。</returns>
    public static bool IsValidPackageName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return PackageNamePattern.IsMatch(name) && name.Any(char.IsLetterOrDigit);
    }

    /// <summary>
    /// 校验相对路径的静态骨架安全：非空、非绝对路径、无盘符前缀、不含 .. 段。{{变量}} 占位允许存在。
    /// </summary>
    /// <param name="relativePath">待校验的相对路径，支持 {{变量}} 占位。</param>
    /// <returns>路径骨架安全返回 true，否则返回 false。</returns>
    internal static bool IsSafeRelativeSkeleton(string relativePath)
    {
        string path = (relativePath ?? string.Empty).Trim();
        if (path.Length == 0)
        {
            return false;
        }

        if (Path.IsPathRooted(path))
        {
            return false;
        }

        if (path.Contains(':'))
        {
            return false;
        }

        string[] segments = path.Replace('\\', '/').Split('/');
        foreach (string segment in segments)
        {
            if (segment == "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 将相对路径规范化：统一正斜杠、去除重复分隔符与首部斜杠，并折叠当前目录段与空段。
    /// 供包内路径匹配与安全校验复用，UI 层按用户输入定位文件树项时同样使用该规范化结果。
    /// </summary>
    /// <param name="relativePath">原始相对路径。</param>
    /// <returns>规范化后的相对路径（正斜杠分隔）。</returns>
    public static string NormalizeRelativePath(string relativePath)
    {
        string path = (relativePath ?? string.Empty).Trim().Replace('\\', '/');
        while (path.Contains("//", StringComparison.Ordinal))
        {
            path = path.Replace("//", "/", StringComparison.Ordinal);
        }

        path = path.TrimStart('/');

        // 折叠当前目录段 . 与重复空段，保持规范相对路径
        var segments = new List<string>();
        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            segments.Add(segment);
        }

        return string.Join("/", segments);
    }

    /// <summary>
    /// 校验相对路径安全并解析为根目录内的绝对路径，防目录穿越（zip slip）。
    /// </summary>
    /// <param name="rootDirectory">包根目录绝对路径。</param>
    /// <param name="relativePath">待解析的相对路径。</param>
    /// <returns>根目录内的绝对路径。</returns>
    /// <exception cref="TemplatePackageException">路径含绝对路径、盘符前缀、.. 段或越出根目录时抛出。</exception>
    internal static string ResolveWithinRoot(string rootDirectory, string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        if (normalized.Length == 0)
        {
            throw new TemplatePackageException("相对路径为空。");
        }

        // 拒绝绝对路径、盘符前缀与 UNC 等以冒号或根标记开头的路径
        if (Path.IsPathRooted(relativePath) || Path.IsPathRooted(normalized) || normalized.Contains(':'))
        {
            throw new TemplatePackageException($"相对路径含绝对路径或盘符前缀，已拒绝：{relativePath}");
        }

        string[] segments = normalized.Split('/');
        foreach (string segment in segments)
        {
            if (segment == "..")
            {
                throw new TemplatePackageException($"相对路径含 .. 段，已拒绝：{relativePath}");
            }
        }

        string rootFull;
        string candidate;
        try
        {
            rootFull = Path.GetFullPath(rootDirectory);
            candidate = Path.GetFullPath(Path.Combine(rootFull, normalized.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            // 非法路径字符等异常统一转换为模板包异常，保证防穿越契约对外一致
            throw new TemplatePackageException($"相对路径含非法字符，已拒绝：{relativePath}", exception);
        }

        if (!IsPathWithinRoot(rootFull, candidate))
        {
            throw new TemplatePackageException($"相对路径越出包根目录，已拒绝：{relativePath}");
        }

        return candidate;
    }

    /// <summary>
    /// 判断完整路径是否位于根目录内部（大小写不敏感的前缀校验）。
    /// </summary>
    /// <param name="rootDirectory">根目录绝对路径。</param>
    /// <param name="fullPath">待判断的完整路径。</param>
    /// <returns>路径位于根目录内返回 true，否则返回 false。</returns>
    internal static bool IsPathWithinRoot(string rootDirectory, string fullPath)
    {
        string rootFull = Path.GetFullPath(rootDirectory);
        string pathFull = Path.GetFullPath(fullPath);
        string prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return pathFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 读取并反序列化 template.json 清单。
    /// </summary>
    private static async Task<TemplateManifest> ReadManifestAsync(string manifestPath, CancellationToken cancellationToken)
    {
        TemplateManifest? manifest;
        try
        {
            string json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            manifest = JsonSerializer.Deserialize<TemplateManifest>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new TemplatePackageException($"{ManifestFileName} 清单解析失败：{exception.Message}", exception);
        }

        if (manifest is null)
        {
            throw new TemplatePackageException($"{ManifestFileName} 清单内容为空。");
        }

        return manifest;
    }

    /// <summary>
    /// 归一化清单字段：去除首尾空白、小写化引擎、清理类型映射空值与空项，补齐缺省空集合。
    /// </summary>
    /// <param name="manifest">反序列化后的原始清单。</param>
    /// <returns>归一化后的清单。</returns>
    private static TemplateManifest NormalizeManifest(TemplateManifest manifest)
    {
        manifest.Name = (manifest.Name ?? string.Empty).Trim();
        manifest.Description = (manifest.Description ?? string.Empty).Trim();
        manifest.Engine = (manifest.Engine ?? string.Empty).Trim().ToLowerInvariant();
        manifest.BasePackage = string.IsNullOrWhiteSpace(manifest.BasePackage) ? null : manifest.BasePackage.Trim();
        manifest.TypeMap ??= new Dictionary<string, string>();
        manifest.Files ??= new List<TemplateFileEntry>();

        // 清理类型映射中的空键空值，键统一小写化，便于渲染侧大小写不敏感命中
        var cleanedTypeMap = new Dictionary<string, string>();
        foreach (KeyValuePair<string, string> pair in manifest.TypeMap)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                cleanedTypeMap[pair.Key.Trim().ToLowerInvariant()] = pair.Value.Trim();
            }
        }

        manifest.TypeMap = cleanedTypeMap;

        // 清单 files 中存在空条目时直接判为非法，避免后续空引用崩溃
        foreach (TemplateFileEntry entry in manifest.Files)
        {
            if (entry is null)
            {
                throw new TemplatePackageException("清单 files 中存在空条目，清单不合法。");
            }

            entry.Template = (entry.Template ?? string.Empty).Trim();
            entry.Output = (entry.Output ?? string.Empty).Trim();
        }

        return manifest;
    }

    /// <summary>
    /// 校验包名、引擎与文件清单的完整性。
    /// </summary>
    /// <param name="manifest">归一化后的清单。</param>
    private static void ValidatePackageIdentity(TemplateManifest manifest)
    {
        if (!IsValidPackageName(manifest.Name))
        {
            throw new TemplatePackageException($"包名不合法（须为字母/数字/中划线/下划线且不含路径分隔符或 ..）：{manifest.Name}");
        }

        if (!string.Equals(manifest.Engine, SupportedEngine, StringComparison.OrdinalIgnoreCase))
        {
            throw new TemplatePackageException($"不支持的模板引擎：{manifest.Engine}，当前仅支持 {SupportedEngine}。");
        }

        if (manifest.Files.Count == 0)
        {
            throw new TemplatePackageException("清单 files 不能为空，至少声明一个模板文件。");
        }
    }
}
