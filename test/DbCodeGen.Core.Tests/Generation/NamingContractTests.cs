using System.Text.Json;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Generation;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbCodeGen.Core.Tests.Generation;

/// <summary>
/// 命名契约单元测试：实体名派生（前缀剥离/单复数/后缀）、包段归一与去重、权限模块派生与权限键、
/// 模板渲染上下文的 tool.packagePath / package.permissionModule / tool.permissionKey，
/// 以及 dry-run 命名报告（rawName/entityName/variableName/packagePath/tableNameInCode/permissionKey）。
/// </summary>
public sealed class NamingContractTests : IDisposable
{
    private readonly List<string> _tempRoots = new();

    /// <summary>
    /// 清理全部测试临时目录。
    /// </summary>
    public void Dispose()
    {
        foreach (string root in _tempRoots)
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// t_common_file 在剥离 t_ 前缀后派生 CommonFile / commonFile，物理表名 rawName 保持原样。
    /// </summary>
    [Fact]
    public void DeriveEntityName_StripPrefix_ProducesPascalAndCamelNames()
    {
        var policy = ResolvedNamingPolicy.Resolve(
            new NamingPolicyOptions { StripTablePrefix = true, TablePrefixes = new List<string> { "t_" } },
            variables: null);

        Assert.Equal("CommonFile", policy.DeriveEntityName("t_common_file"));
        Assert.Equal("commonFile", ToolFunctions.FirstLowerCase(policy.DeriveEntityName("t_common_file")));
    }

    /// <summary>
    /// 单复数归一仅作用末词且遵守安全规则：ies→y、ses/xes→去 es、s→去 s，ss/us/is 结尾不破坏。
    /// </summary>
    [Theory]
    [InlineData("t_categories", "Category")]
    [InlineData("t_boxes", "Box")]
    [InlineData("t_common_files", "CommonFile")]
    [InlineData("t_address", "Address")]
    [InlineData("t_status", "Status")]
    [InlineData("t_analysis", "Analysis")]
    public void DeriveEntityName_Singularize_UsesSafeRules(string rawName, string expected)
    {
        var policy = ResolvedNamingPolicy.Resolve(
            new NamingPolicyOptions
            {
                StripTablePrefix = true,
                TablePrefixes = new List<string> { "t_" },
                Singularize = true
            },
            variables: null);

        Assert.Equal(expected, policy.DeriveEntityName(rawName));
    }

    /// <summary>
    /// entitySuffix 拼接在 PascalCase 实体名之后，变量名为 camelCase 且保留后缀。
    /// </summary>
    [Fact]
    public void DeriveEntityName_WithSuffix_AppendsSuffix()
    {
        var policy = ResolvedNamingPolicy.Resolve(new NamingPolicyOptions { EntitySuffix = "Entity" }, variables: null);

        string entityName = policy.DeriveEntityName("t_asset");
        Assert.Equal("TAssetEntity", entityName);
        Assert.Equal("tAssetEntity", ToolFunctions.FirstLowerCase(entityName));
    }

    /// <summary>
    /// 请求 variables 同名键优先于 manifest 声明（表前缀、单复数、层段前缀）。
    /// </summary>
    [Fact]
    public void Resolve_RequestVariablesOverrideManifest()
    {
        var manifestOptions = new NamingPolicyOptions
        {
            StripTablePrefix = true,
            TablePrefixes = new List<string> { "t_" },
            Singularize = true,
            LayerPrefix = "common"
        };
        var variables = new Dictionary<string, object?>
        {
            ["tablePrefixes"] = new List<string> { "t_", "sys_" },
            ["singularize"] = false,
            ["layerPrefix"] = "biz"
        };

        ResolvedNamingPolicy policy = ResolvedNamingPolicy.Resolve(manifestOptions, variables);

        Assert.True(policy.StripTablePrefix);
        Assert.Equal("UserInfo", policy.DeriveEntityName("sys_user_info"));
        Assert.Equal("biz", policy.LayerPrefix);
    }

    /// <summary>
    /// 包段归一：全小写、kebab 归一、连续重复段去重（基础包已以 common 结尾时不再硬拼第二个 common）。
    /// </summary>
    [Fact]
    public void PackagePathBuilder_Compose_DeduplicatesAndLowercases()
    {
        IReadOnlyList<string> baseSegments = PackagePathBuilder.NormalizeBaseSegments(
            "com.simple.xiaoguan.common", ResolvedNamingPolicy.PackageSegmentCaseLower);

        IReadOnlyList<string> composed = PackagePathBuilder.Compose(
            baseSegments, ResolvedNamingPolicy.PackageSegmentCaseLower, "common", "commonFile");

        Assert.Equal(new[] { "com", "simple", "xiaoguan", "common", "commonfile" }, composed);
        Assert.Equal("com/simple/xiaoguan/common/commonfile", PackagePathBuilder.ToDirectoryPath(composed));
        Assert.Equal("com.simple.xiaoguan.common.commonfile", PackagePathBuilder.ToDottedPath(composed));
    }

    /// <summary>
    /// kebab 模块名在包段处经 JavaIdent 归一去掉连字符，preserve 口径保留大小写。
    /// </summary>
    [Fact]
    public void PackagePathBuilder_Normalize_HandlesKebabAndPreserveCase()
    {
        Assert.Equal(
            new[] { "com", "simple", "usercenter" },
            PackagePathBuilder.NormalizeBaseSegments("com.simple.user-center", ResolvedNamingPolicy.PackageSegmentCaseLower));
        Assert.Equal(
            new[] { "com", "Simple" },
            PackagePathBuilder.NormalizeBaseSegments("com.Simple", ResolvedNamingPolicy.PackageSegmentCasePreserve));
    }

    /// <summary>
    /// 权限模块优先级：请求 vars.permissionModule &gt; manifest.permissionModule &gt;
    /// Hump3Underline(modules[0].name) &gt; 基础包名末段 kebab 派生。
    /// </summary>
    [Fact]
    public void PermissionModuleResolver_PriorityOrder()
    {
        var variables = new Dictionary<string, object?> { ["permissionModule"] = "asset-center" };
        var modules = new[] { new ModuleContext("user-center") };

        Assert.Equal("asset-center", PermissionModuleResolver.Resolve("com.simple.usercenter", "manifest-module", variables, modules));
        Assert.Equal("manifest-module", PermissionModuleResolver.Resolve("com.simple.usercenter", "manifest-module", variables: null, modules));
        Assert.Equal("user-center", PermissionModuleResolver.Resolve("com.simple.usercenter", null, variables: null, modules));
        Assert.Equal("usercenter", PermissionModuleResolver.Resolve("com.simple.usercenter", null, variables: null, modules: null));
        Assert.Equal("user-center", PermissionModuleResolver.Resolve("com.simple.UserCenter", null, variables: null));
        Assert.Equal("usercenter:view", PermissionModuleResolver.BuildKey("usercenter", "view"));
        Assert.Equal("usercenter", PermissionModuleResolver.BuildKey("usercenter", null));
    }

    /// <summary>
    /// 模板渲染上下文提供 tool.packagePath / package.permissionModule / tool.permissionKey（单参与双参形态），
    /// 且传了 modules 时 package.permissionModule 自动等于模块名 kebab 形态（零配置统一）。
    /// </summary>
    [Fact]
    public async Task TemplateEngine_ExposesPackagePathAndPermissionKey()
    {
        TemplatePackageInfo package = await CreatePackageAsync(new TemplateManifest
        {
            Name = "naming-pkg",
            Description = "命名契约测试包",
            Engine = "scriban",
            BasePackage = "com.simple.xiaoguan.common",
            Files = new List<TemplateFileEntry>(),
            Naming = new NamingPolicyOptions
            {
                StripTablePrefix = true,
                TablePrefixes = new List<string> { "t_" },
                Singularize = true,
                LayerPrefix = "common"
            }
        });
        var table = new TableInfo
        {
            RawName = "t_common_file",
            EntityName = "CommonFile",
            ClassName = "CommonFile",
            VariableName = "commonFile"
        };

        TemplatePackageContext packageContext = TemplatePackageContext.From(
            package, basePackageOverride: null, variables: null, modules: new[] { new ModuleContext("user-center") });
        var engine = new TemplateEngine();
        PreviewResult result = engine.Render(
            "{{ tool.packagePath(package.naming.layerPrefix, table.variableName) }}|{{ package.permissionModule }}|{{ tool.permissionKey(\"view\") }}|{{ tool.permissionKey(\"asset-center\", \"edit\") }}|{{ package.dir }}",
            table,
            column: null,
            packageContext,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(
            "com/simple/xiaoguan/common/commonfile|user-center|user-center:view|asset-center:edit|com/simple/xiaoguan/common",
            result.Output);
    }

    /// <summary>
    /// dry-run 命名报告逐表给出 rawName/entityName/variableName/packagePath/tableNameInCode/permissionKey，
    /// 平台表不进入命名报告（被排除）。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_NamingReport_ReflectsResolvedContract()
    {
        TemplatePackageInfo package = await CreatePackageAsync(new TemplateManifest
        {
            Name = "report-pkg",
            Description = "命名报告测试包",
            Engine = "scriban",
            BasePackage = "com.simple.xiaoguan.common",
            Files = new List<TemplateFileEntry>
            {
                new() { Template = "entity.java.scriban", Output = "out/{{table.className}}.java", Enabled = true }
            },
            PlatformTables = new TableFilterRules { Prefixes = new List<string> { "sys_" } },
            Naming = new NamingPolicyOptions
            {
                StripTablePrefix = true,
                TablePrefixes = new List<string> { "t_" },
                Singularize = true,
                LayerPrefix = "common"
            }
        });
        (string workspaceRoot, _) = CreateWorkspace();
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out/{{table.className}}.java", true) };
        CodeGenerator generator = CreateGenerator();

        GenerationPreview preview = await generator.BuildPreviewAsync(
            new GenerationRequest(
                package,
                new[] { CreateTable("t_common_file"), CreateTable("sys_user"), CreateTable("t_order_items") },
                files,
                workspaceRoot,
                "gen"),
            null,
            CancellationToken.None);

        Assert.Equal(2, preview.NamingReport.Count);
        NamingReportItem first = preview.NamingReport[0];
        Assert.Equal("t_common_file", first.RawName);
        Assert.Equal("CommonFile", first.EntityName);
        Assert.Equal("commonFile", first.VariableName);
        Assert.Equal("com.simple.xiaoguan.common.commonfile", first.PackagePath);
        Assert.Equal("t_common_file", first.TableNameInCode);
        Assert.Equal("common", first.PermissionKey);

        NamingReportItem second = preview.NamingReport[1];
        Assert.Equal("t_order_items", second.RawName);
        Assert.Equal("OrderItem", second.EntityName);
        Assert.Equal("orderItem", second.VariableName);

        PlatformTableExclusion exclusion = Assert.Single(preview.PlatformTableExcluded);
        Assert.Equal("sys_user", exclusion.TableName);
        Assert.True(exclusion.Excluded);
        Assert.StartsWith("platform:", exclusion.Rule);
    }

    /// <summary>
    /// 在临时目录创建测试模板包并加载。
    /// </summary>
    /// <param name="manifest">清单模型。</param>
    /// <returns>加载后的模板包信息。</returns>
    private async Task<TemplatePackageInfo> CreatePackageAsync(TemplateManifest manifest)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
        _tempRoots.Add(tempRoot);
        string packageRoot = Path.Combine(tempRoot, manifest.Name);
        Directory.CreateDirectory(packageRoot);
        await File.WriteAllTextAsync(
            Path.Combine(packageRoot, TemplatePackageLoader.ManifestFileName),
            JsonSerializer.Serialize(manifest, TemplatePackageLoader.JsonOptions));
        foreach (TemplateFileEntry entry in manifest.Files)
        {
            await File.WriteAllTextAsync(Path.Combine(packageRoot, entry.Template), "class {{table.className}} {}\n");
        }

        return await TemplatePackageLoader.LoadFromDirectoryAsync(packageRoot, isBuiltin: false, CancellationToken.None);
    }

    /// <summary>
    /// 创建测试工作区根。
    /// </summary>
    /// <returns>工作区根与输出根。</returns>
    private (string WorkspaceRoot, string OutputRoot) CreateWorkspace()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
        _tempRoots.Add(tempRoot);
        string workspaceRoot = Path.Combine(tempRoot, "workspace");
        Directory.CreateDirectory(workspaceRoot);
        return (workspaceRoot, Path.Combine(workspaceRoot, "gen"));
    }

    /// <summary>
    /// 构造样例表元数据。
    /// </summary>
    /// <param name="rawName">原始表名。</param>
    /// <returns>样例表。</returns>
    private static TableInfo CreateTable(string rawName)
    {
        return new TableInfo
        {
            RawName = rawName,
            ClassName = TableInfo.ToPascalCase(rawName),
            VariableName = TableInfo.ToCamelCase(rawName)
        };
    }

    /// <summary>
    /// 构造生成服务：渲染引擎与写盘服务直构，配置服务用内存替身。
    /// </summary>
    /// <returns>生成服务。</returns>
    private static CodeGenerator CreateGenerator()
    {
        return new CodeGenerator(
            new TemplateEngine(),
            new TemplateFileWriter(),
            new FileWriter(NullLogger<FileWriter>.Instance),
            new FakeConfigService(),
            NullLogger<CodeGenerator>.Instance);
    }

    /// <summary>
    /// 配置服务测试替身，仅满足构造依赖。
    /// </summary>
    private sealed class FakeConfigService : IConfigService
    {
        /// <summary>
        /// 内存配置快照。
        /// </summary>
        public AppConfig Current { get; } = new AppConfig();

        /// <summary>
        /// 配置文件路径，测试内不落盘消费。
        /// </summary>
        public string ConfigFilePath => Path.Combine(Path.GetTempPath(), "DbCodeGenTests", "config.json");

        /// <summary>
        /// 配置变更事件，测试内无订阅方。
        /// </summary>
        public event EventHandler? ConfigChanged;

        /// <inheritdoc />
        public AppConfig Load() => Current;

        /// <inheritdoc />
        public void Save() => ConfigChanged?.Invoke(this, EventArgs.Empty);

        /// <inheritdoc />
        public GenerationDefaults GetGenerationDefaults() => new(Current.WorkspaceRoot, Current.LastRelativeOutputRoot);

        /// <inheritdoc />
        public string? GetLlmApiKey() => null;

        /// <inheritdoc />
        public void ChangeDataDirectory(string dataDirectory) => throw new NotSupportedException("测试替身不支持切换数据目录。");
    }
}
