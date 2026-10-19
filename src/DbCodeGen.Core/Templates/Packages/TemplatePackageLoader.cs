using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板包加载与校验器：解析 template.json、校验引擎与包名、逐模板文件做防目录穿越与存在性校验，
/// 产出运行时 TemplatePackageInfo。内置包与用户包共用同一套校验规则；文件清单允许为空（空模板包）。
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
    /// 契约文档缺省相对包根路径：manifest 未声明 contractPath 时按该路径发现传参契约。
    /// </summary>
    public const string DefaultContractPath = "README.md.scriban";

    /// <summary>
    /// 合法包角色集合（backend / scaffold / web / fullstack / utility）。
    /// </summary>
    private static readonly HashSet<string> KnownPackageRoles = new(StringComparer.Ordinal)
    {
        "backend", "scaffold", "web", "fullstack", "utility"
    };

    /// <summary>
    /// 合法技术栈集合（java / net / web / mixed）。
    /// </summary>
    private static readonly HashSet<string> KnownStacks = new(StringComparer.Ordinal)
    {
        "java", "net", "web", "mixed"
    };

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

            // 输出相对路径静态骨架防目录穿越，允许 .. 段（由生成侧解析时限定在工作区根内）
            if (!IsSafeOutputPathSkeleton(entry.Output))
            {
                throw new TemplatePackageException($"模板文件 {templateRelative} 的输出路径不合法（禁止绝对路径与盘符前缀，.. 仅限工作区根内）：{entry.Output}");
            }

            // 作用域解析与校验：非法取值在包加载阶段结构化拒绝，缺省按 table（既有包行为不变）
            TemplateScope scope = TemplateScopeParser.Parse(entry.Scope, templateRelative);

            // 输出根解析与校验：未声明时保持 null（按作用域沿用既有语义），workspace/web 语义在生成侧拒绝 .. 段
            TemplateOutputRoot? outputRoot = TemplateOutputRootParser.Parse(entry.Root, templateRelative);

            // root=workspace/web 为固定基准目录：输出路径静态含 .. 段时在加载阶段即拒绝（渲染后再由生成侧兜底校验）
            if ((outputRoot is TemplateOutputRoot.Workspace or TemplateOutputRoot.Web) && ContainsParentTraversal(entry.Output))
            {
                throw new TemplatePackageException(
                    $"模板文件 {templateRelative} 的 root={TemplateOutputRootParser.ToContractText(outputRoot.Value)} 禁止输出路径含 .. 段：{entry.Output}");
            }

            // 产出角色：声明值优先，缺省按输出路径与模板文件名推断，供能力元数据与覆盖告警消费
            string role = TemplateFileRoleResolver.Resolve(entry.Role, templateRelative, entry.Output);

            files.Add(new TemplateFileInfo
            {
                RelativeTemplatePath = templateRelative,
                TemplatePath = templateFullPath,
                OutputPath = entry.Output,
                Scope = scope,
                OutputRoot = outputRoot,
                Role = role,
                IsEnabled = entry.Enabled
            });
        }

        TemplateCapabilities capabilities = InferCapabilities(normalized, files);
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
            Files = files,
            Role = capabilities.Role,
            Stack = capabilities.Stack,
            Scopes = capabilities.Scopes,
            HasBackendView = capabilities.HasBackendView,
            HasWebPage = capabilities.HasWebPage,
            ContractPath = normalized.ContractPath ?? DefaultContractPath,
            DeployModes = normalized.DeployModes ?? new List<string>(),
            PermissionModule = normalized.PermissionModule ?? string.Empty,
            PlatformTables = normalized.PlatformTables ?? new TableFilterRules(),
            ExcludeTables = normalized.ExcludeTables ?? new TableFilterRules(),
            Naming = normalized.Naming,
            Presets = normalized.Presets ?? new Dictionary<string, TemplatePresetDefinition>(StringComparer.OrdinalIgnoreCase)
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
    /// 校验输出相对路径的静态骨架安全：非空、非绝对路径、无盘符前缀、不含冒号；允许 .. 段（由解析端限定在工作区根内）。
    /// </summary>
    /// <param name="relativePath">待校验的相对路径，支持 {{变量}} 占位。</param>
    /// <returns>路径骨架安全返回 true，否则返回 false。</returns>
    internal static bool IsSafeOutputPathSkeleton(string relativePath)
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

        // 能力元数据归一：角色/技术栈/部署形态统一小写，枚举取值非法时结构化拒绝（防声明笔误静默失效）
        manifest.Role = NormalizeOptionalKnownValue(manifest.Role, KnownPackageRoles, "role");
        manifest.Stack = NormalizeOptionalKnownValue(manifest.Stack, KnownStacks, "stack");
        manifest.PermissionModule = string.IsNullOrWhiteSpace(manifest.PermissionModule) ? null : manifest.PermissionModule.Trim();
        manifest.DeployModes = NormalizeStringList(manifest.DeployModes);
        manifest.ContractPath = NormalizeContractPath(manifest.ContractPath);
        manifest.PlatformTables = NormalizeFilterRules(manifest.PlatformTables, "platformTables");
        manifest.ExcludeTables = NormalizeFilterRules(manifest.ExcludeTables, "excludeTables");
        manifest.Naming = NormalizeNamingOptions(manifest.Naming);

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
            entry.Scope = string.IsNullOrWhiteSpace(entry.Scope) ? null : entry.Scope.Trim();
            entry.Root = string.IsNullOrWhiteSpace(entry.Root) ? null : entry.Root.Trim();
            entry.Role = string.IsNullOrWhiteSpace(entry.Role) ? null : entry.Role.Trim();
        }

        // 预设声明归一与校验：说明/选择口径/文件存在性与变量键在加载阶段一次性校验，
        // 避免生成期才暴露 preset 配置错误；未声明时保持 null（回写清单不新增空 presets 字段）
        Dictionary<string, TemplatePresetDefinition> normalizedPresets =
            TemplatePresetResolver.Normalize(manifest.Presets, manifest.Files);
        manifest.Presets = normalizedPresets.Count == 0 ? null : normalizedPresets;

        return manifest;
    }

    /// <summary>
    /// 判断输出路径模板是否静态含 .. 段（正反斜杠均识别）；模板表达式渲染出的 .. 由生成侧兜底校验。
    /// </summary>
    /// <param name="outputPathTemplate">输出路径模板。</param>
    /// <returns>静态含 .. 段时为 true。</returns>
    private static bool ContainsParentTraversal(string outputPathTemplate)
    {
        foreach (string segment in (outputPathTemplate ?? string.Empty).Replace('\\', '/').Split('/'))
        {
            if (segment == "..")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 归一可选枚举文本：空白返回 null（缺省推断），非空白统一小写并校验属于合法集合，非法即结构化拒绝。
    /// </summary>
    /// <param name="value">原始文本，可空。</param>
    /// <param name="knownValues">合法取值集合。</param>
    /// <param name="fieldName">字段名，供错误消息定位。</param>
    /// <returns>归一后的取值；空白时为 null。</returns>
    /// <exception cref="TemplatePackageException">取值非法时抛出。</exception>
    private static string? NormalizeOptionalKnownValue(string? value, HashSet<string> knownValues, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim().ToLowerInvariant();
        if (!knownValues.Contains(normalized))
        {
            throw new TemplatePackageException(
                $"模板包清单字段 {fieldName} 取值非法：{value}；合法取值为 {string.Join(" / ", knownValues)}。");
        }

        return normalized;
    }

    /// <summary>
    /// 归一去空白字符串清单：去空白、剔除空项、保持声明顺序；无有效项时返回 null。
    /// </summary>
    /// <param name="values">原始清单，可空。</param>
    /// <returns>归一后的清单；无有效项时为 null。</returns>
    private static List<string>? NormalizeStringList(List<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        var normalized = new List<string>(values.Count);
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                normalized.Add(value.Trim());
            }
        }

        return normalized.Count == 0 ? null : normalized;
    }

    /// <summary>
    /// 归一契约文档路径：空白取缺省 README.md.scriban；非空白按安全相对路径校验（禁止绝对路径、盘符与 .. 段）。
    /// </summary>
    /// <param name="contractPath">原始契约路径，可空。</param>
    /// <returns>归一后的相对路径；空白时为 null（由调用方取缺省常量）。</returns>
    /// <exception cref="TemplatePackageException">契约路径不安全时抛出。</exception>
    private static string? NormalizeContractPath(string? contractPath)
    {
        if (string.IsNullOrWhiteSpace(contractPath))
        {
            return null;
        }

        string normalized = NormalizeRelativePath(contractPath);
        if (normalized.Length == 0 || !IsSafeRelativeSkeleton(normalized))
        {
            throw new TemplatePackageException($"模板包清单 contractPath 不合法（禁止绝对路径、盘符前缀与 .. 段）：{contractPath}");
        }

        return normalized;
    }

    /// <summary>
    /// 归一表过滤规则：清空白项、正则预编译校验（非法正则结构化拒绝），空规则返回 null。
    /// </summary>
    /// <param name="rules">原始规则集合，可空。</param>
    /// <param name="fieldName">字段名，供错误消息定位。</param>
    /// <returns>归一后的规则集合；无有效规则时为 null。</returns>
    /// <exception cref="TemplatePackageException">正则模式非法时抛出。</exception>
    private static TableFilterRules? NormalizeFilterRules(TableFilterRules? rules, string fieldName)
    {
        if (rules is null)
        {
            return null;
        }

        TableFilterRules normalized = new()
        {
            Exact = NormalizeStringList(rules.Exact) ?? new List<string>(),
            Prefixes = NormalizeStringList(rules.Prefixes) ?? new List<string>(),
            Regex = NormalizeStringList(rules.Regex) ?? new List<string>()
        };

        // 正则声明在加载阶段预编译校验，避免生成期才暴露灾难性模式或语法错误
        foreach (string pattern in normalized.Regex)
        {
            try
            {
                _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException exception)
            {
                throw new TemplatePackageException($"模板包清单 {fieldName}.regex 模式非法：{pattern}；{exception.Message}", exception);
            }
        }

        return normalized.IsEmpty ? null : normalized;
    }

    /// <summary>
    /// 归一命名策略声明：字符串去空白、前缀清单清理、packageSegmentCase 校验（lower / preserve），空声明返回 null。
    /// </summary>
    /// <param name="options">原始命名策略，可空。</param>
    /// <returns>归一后的命名策略；无有效字段时为 null。</returns>
    /// <exception cref="TemplatePackageException">packageSegmentCase 取值非法时抛出。</exception>
    private static NamingPolicyOptions? NormalizeNamingOptions(NamingPolicyOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        string? packageSegmentCase = null;
        if (!string.IsNullOrWhiteSpace(options.PackageSegmentCase))
        {
            packageSegmentCase = options.PackageSegmentCase.Trim().ToLowerInvariant();
            if (packageSegmentCase is not (ResolvedNamingPolicy.PackageSegmentCaseLower or ResolvedNamingPolicy.PackageSegmentCasePreserve))
            {
                throw new TemplatePackageException(
                    $"模板包清单 naming.packageSegmentCase 取值非法：{options.PackageSegmentCase}；合法取值为 lower / preserve。");
            }
        }

        var normalized = new NamingPolicyOptions
        {
            StripTablePrefix = options.StripTablePrefix,
            TablePrefixes = NormalizeStringList(options.TablePrefixes),
            Singularize = options.Singularize,
            EntitySuffix = string.IsNullOrWhiteSpace(options.EntitySuffix) ? null : options.EntitySuffix.Trim(),
            PackageSegmentCase = packageSegmentCase,
            LayerPrefix = string.IsNullOrWhiteSpace(options.LayerPrefix) ? null : options.LayerPrefix.Trim()
        };

        bool hasAnyValue = normalized.StripTablePrefix is not null
            || normalized.TablePrefixes is not null
            || normalized.Singularize is not null
            || normalized.EntitySuffix is not null
            || normalized.PackageSegmentCase is not null
            || normalized.LayerPrefix is not null;
        return hasAnyValue ? normalized : null;
    }

    /// <summary>
    /// 校验包名与引擎的合法性；文件清单允许为空，files 为空的包视为合法空模板包。
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
    }

    /// <summary>
    /// 派生包能力元数据：manifest 声明值优先，缺省按文件产物扩展名与产出角色推断，
    /// 保证既有包（无新字段）也能被工具面判定用途与生成覆盖面。
    /// </summary>
    /// <param name="manifest">归一后的清单。</param>
    /// <param name="files">校验通过的模板文件清单。</param>
    /// <returns>包能力元数据。</returns>
    private static TemplateCapabilities InferCapabilities(TemplateManifest manifest, IReadOnlyList<TemplateFileInfo> files)
    {
        bool hasJava = files.Any(file => ProducesExtension(file, "java"));
        bool hasTs = files.Any(file => ProducesExtension(file, "ts") || ProducesExtension(file, "tsx"));
        bool hasCs = files.Any(file => ProducesExtension(file, "cs"));

        string role = manifest.Role ?? (hasJava ? "backend" : hasTs ? "web" : "utility");
        string stack = manifest.Stack ?? InferStack(hasJava, hasTs, hasCs);

        // 作用域去重派生：顺序固定 table / module / project，供能力元数据与前端工具面直读
        var scopes = new List<string>();
        foreach (TemplateScope scope in new[] { TemplateScope.Table, TemplateScope.Module, TemplateScope.Project })
        {
            if (files.Any(file => file.Scope == scope))
            {
                scopes.Add(TemplateScopeParser.ToContractText(scope));
            }
        }

        bool hasBackendView = manifest.HasBackendView ?? files.Any(file =>
            file.Role == TemplateFileRoleResolver.BackendView
            || NormalizePathForCapability(file.OutputPath).Contains("/view/", StringComparison.Ordinal)
            || NormalizePathForCapability(file.OutputPath).EndsWith("view.java", StringComparison.Ordinal));

        bool hasWebPage = manifest.HasWebPage ?? files.Any(file =>
            file.Role is TemplateFileRoleResolver.WebPage
                or TemplateFileRoleResolver.WebApi
                or TemplateFileRoleResolver.WebDto
                or TemplateFileRoleResolver.WebRoute
                or TemplateFileRoleResolver.WebMenu
            || IsWebOutputPath(NormalizePathForCapability(file.OutputPath)));

        return new TemplateCapabilities(role, stack, scopes, hasBackendView, hasWebPage);
    }

    /// <summary>
    /// 按产物扩展名种类推断技术栈：单一语言栈返回 java/web/net，多栈或无识别产物返回 mixed。
    /// </summary>
    /// <param name="hasJava">是否存在 Java 产物。</param>
    /// <param name="hasTs">是否存在 TypeScript/TSX 产物。</param>
    /// <param name="hasCs">是否存在 C# 产物。</param>
    /// <returns>技术栈文本。</returns>
    private static string InferStack(bool hasJava, bool hasTs, bool hasCs)
    {
        int kindCount = (hasJava ? 1 : 0) + (hasTs ? 1 : 0) + (hasCs ? 1 : 0);
        if (kindCount != 1)
        {
            return "mixed";
        }

        return hasJava ? "java" : hasTs ? "web" : "net";
    }

    /// <summary>
    /// 判断模板条目是否产出指定扩展名：以输出路径（优先）或模板文件名的最后一个扩展段为准。
    /// </summary>
    /// <param name="file">模板文件运行时信息。</param>
    /// <param name="extension">扩展名（不含点，大小写不敏感）。</param>
    /// <returns>产出该扩展名时为 true。</returns>
    private static bool ProducesExtension(TemplateFileInfo file, string extension)
    {
        string source = string.IsNullOrWhiteSpace(file.OutputPath) ? file.RelativeTemplatePath : file.OutputPath;
        int separatorIndex = Math.Max(source.LastIndexOf('/'), source.LastIndexOf('\\'));
        int dotIndex = source.LastIndexOf('.');
        if (dotIndex <= separatorIndex || dotIndex == source.Length - 1)
        {
            return false;
        }

        return string.Equals(source[(dotIndex + 1)..], extension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断归一化输出路径是否位于 web 前端目录（web/ 开头或含 /web/ 片段）或页面目录（/pages/）。
    /// </summary>
    /// <param name="normalizedPath">小写归一后的输出路径。</param>
    /// <returns>属于前端产出路径时为 true。</returns>
    private static bool IsWebOutputPath(string normalizedPath)
    {
        return normalizedPath.StartsWith("web/", StringComparison.Ordinal)
            || normalizedPath.Contains("/web/", StringComparison.Ordinal)
            || normalizedPath.Contains("/pages/", StringComparison.Ordinal);
    }

    /// <summary>
    /// 将路径归一为小写正斜杠形态，供能力推断的片段匹配使用。
    /// </summary>
    /// <param name="path">原始路径，可空。</param>
    /// <returns>归一后的路径；空值返回空串。</returns>
    private static string NormalizePathForCapability(string? path)
    {
        return (path ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
    }

    /// <summary>
    /// 包能力元数据中间载体。
    /// </summary>
    /// <param name="Role">包角色。</param>
    /// <param name="Stack">技术栈。</param>
    /// <param name="Scopes">作用域清单。</param>
    /// <param name="HasBackendView">是否具备后端 View 层。</param>
    /// <param name="HasWebPage">是否具备前端页面。</param>
    private sealed record TemplateCapabilities(
        string Role,
        string Stack,
        List<string> Scopes,
        bool HasBackendView,
        bool HasWebPage);
}
