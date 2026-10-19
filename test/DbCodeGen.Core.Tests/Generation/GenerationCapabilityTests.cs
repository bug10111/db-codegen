using System.Text.Json;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Generation;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbCodeGen.Core.Tests.Generation;

/// <summary>
/// 生成能力面单元测试：平台表/排除表过滤与 includePlatformTables 覆盖、输出根三态（workspace/web）与 .. 逃逸拒绝、
/// 新增路径告警（重复包段/非小写包段/物理表名反推/权限键派生不一致/工作区根疑似聚合根）与覆盖告警。
/// </summary>
public sealed class GenerationCapabilityTests : IDisposable
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
    /// 平台表规则命中默认排除并列入回执；includePlatformTables=true 时参与生成且记录 Excluded=false。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_PlatformTables_ExcludedByDefaultAndOverridable()
    {
        TemplatePackageInfo package = await CreatePackageAsync(new TemplateManifest
        {
            Name = "platform-pkg",
            Description = "平台表测试包",
            Engine = "scriban",
            BasePackage = "com.example",
            Files = new List<TemplateFileEntry>
            {
                new() { Template = "entity.java.scriban", Output = "out/{{table.className}}.java", Enabled = true }
            },
            PlatformTables = new TableFilterRules { Prefixes = new List<string> { "sys_" } }
        });
        (string workspaceRoot, _) = CreateWorkspace();
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out/{{table.className}}.java", true) };
        CodeGenerator generator = CreateGenerator();
        var tables = new[] { CreateTable("t_asset"), CreateTable("sys_user"), CreateTable("sys_dict_type") };

        GenerationPreview excluded = await generator.BuildPreviewAsync(
            new GenerationRequest(package, tables, files, workspaceRoot, "out"),
            null,
            CancellationToken.None);

        Assert.Equal(1, excluded.NewCount);
        Assert.Equal(2, excluded.PlatformTableExcluded.Count);
        Assert.All(excluded.PlatformTableExcluded, exclusion => Assert.True(exclusion.Excluded));

        GenerationPreview included = await generator.BuildPreviewAsync(
            new GenerationRequest(package, tables, files, workspaceRoot, "out", includePlatformTables: true),
            null,
            CancellationToken.None);

        Assert.Equal(3, included.NewCount);
        Assert.Equal(2, included.PlatformTableExcluded.Count);
        Assert.All(included.PlatformTableExcluded, exclusion => Assert.False(exclusion.Excluded));
    }

    /// <summary>
    /// generate 写盘回执携带结构化诊断：平台表排除记录、命名报告与排除日志同 preview 口径一致；
    /// includePlatformTables=true 时平台表参与生成且记录 Excluded=false。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_ReturnsStructuredDiagnostics()
    {
        TemplatePackageInfo package = await CreatePackageAsync(new TemplateManifest
        {
            Name = "generate-diagnostics-pkg",
            Description = "生成诊断测试包",
            Engine = "scriban",
            BasePackage = "com.example",
            Files = new List<TemplateFileEntry>
            {
                new() { Template = "entity.java.scriban", Output = "out/{{table.className}}.java", Enabled = true }
            },
            PlatformTables = new TableFilterRules { Prefixes = new List<string> { "sys_" } }
        });
        (string workspaceRoot, _) = CreateWorkspace();
        var files = new[]
        {
            new TemplateFileSelection(
                "entity.java.scriban", "out/{{table.className}}.java", true, TemplateScope.Table, null, "backendEntity")
        };
        CodeGenerator generator = CreateGenerator();
        var tables = new[] { CreateTable("t_asset"), CreateTable("sys_user"), CreateTable("sys_dict_type") };

        GenerationResult excluded = await generator.GenerateAsync(
            new GenerationRequest(package, tables, files, workspaceRoot, "out", persistLastUsedPaths: false),
            null,
            CancellationToken.None);

        Assert.Equal(1, excluded.Generated);
        Assert.Equal(2, excluded.PlatformTableExcluded.Count);
        Assert.All(excluded.PlatformTableExcluded, exclusion => Assert.True(exclusion.Excluded));
        Assert.Single(excluded.NamingReport);
        Assert.Equal("t_asset", excluded.NamingReport[0].RawName);
        Assert.Contains(excluded.Logs, log => log.Message.Contains("sys_user", StringComparison.Ordinal)
            && log.Message.Contains("被排除", StringComparison.Ordinal));

        GenerationResult included = await generator.GenerateAsync(
            new GenerationRequest(
                package, tables, files, workspaceRoot, "out", persistLastUsedPaths: false, includePlatformTables: true),
            null,
            CancellationToken.None);

        // 第二次生成复用同一工作区：t_asset 内容相同被跳过，平台表两张新增写盘
        Assert.Equal(2, included.Generated);
        Assert.Equal(1, included.Skipped);
        Assert.Equal(2, included.PlatformTableExcluded.Count);
        Assert.All(included.PlatformTableExcluded, exclusion => Assert.False(exclusion.Excluded));
    }

    /// <summary>
    /// 未声明输出根的 project 作用域沿用既有行为：.. 段允许存在，折叠后仍落在工作区根内即可。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_LegacyProjectScope_AllowsParentTraversalWithinWorkspace()
    {
        TemplatePackageInfo package = await CreatePackageAsync(
            new TemplateManifest
            {
                Name = "legacy-project-pkg",
                Description = "legacy project 作用域测试包",
                Engine = "scriban",
                BasePackage = "com.example",
                Files = new List<TemplateFileEntry>
                {
                    new() { Template = "pom.xml.scriban", Output = "sub/../pom.xml", Scope = "project", Enabled = true }
                }
            },
            ("pom.xml.scriban", "<project/>\n"));
        (string workspaceRoot, _) = CreateWorkspace();
        var files = new[] { new TemplateFileSelection("pom.xml.scriban", "sub/../pom.xml", true, TemplateScope.Project) };

        GenerationPreview preview = await CreateGenerator().BuildPreviewAsync(
            new GenerationRequest(package, Array.Empty<TableInfo>(), files, workspaceRoot, "out"),
            null,
            CancellationToken.None);

        GenerationFileEntry entry = Assert.Single(preview.Entries);
        Assert.Equal(
            Path.Combine(workspaceRoot, "pom.xml"),
            entry.AbsolutePath,
            ignoreCase: true);
    }

    /// <summary>
    /// 渲染期表达式产出绝对路径或盘符前缀时整单拒绝（不得绕过静态输出骨架校验）。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_RenderedAbsolutePath_Rejected()
    {
        TemplatePackageInfo package = await CreatePackageAsync(
            new TemplateManifest
            {
                Name = "absolute-path-pkg",
                Description = "绝对路径渲染测试包",
                Engine = "scriban",
                BasePackage = "com.example",
                Files = new List<TemplateFileEntry>
                {
                    new() { Template = "abs.txt.scriban", Output = "{{vars.abs}}/x.txt", Enabled = true }
                }
            },
            ("abs.txt.scriban", "x\n"));
        (string workspaceRoot, _) = CreateWorkspace();
        var files = new[] { new TemplateFileSelection("abs.txt.scriban", "{{vars.abs}}/x.txt", true) };
        var variables = new Dictionary<string, object?> { ["abs"] = "C:/outside" };

        await Assert.ThrowsAsync<GenerationException>(() => CreateGenerator().BuildPreviewAsync(
            new GenerationRequest(
                package,
                new[] { CreateTable("t_asset") },
                files,
                workspaceRoot,
                "out",
                variables: variables),
            null,
            CancellationToken.None));
    }

    /// <summary>
    /// 排除表规则命中一律不生成，includePlatformTables=true 也不解除排除。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_ExcludeTables_AlwaysExcluded()
    {
        TemplatePackageInfo package = await CreatePackageAsync(new TemplateManifest
        {
            Name = "exclude-pkg",
            Description = "排除表测试包",
            Engine = "scriban",
            BasePackage = "com.example",
            Files = new List<TemplateFileEntry>
            {
                new() { Template = "entity.java.scriban", Output = "out/{{table.className}}.java", Enabled = true }
            },
            PlatformTables = new TableFilterRules { Prefixes = new List<string> { "sys_" } },
            ExcludeTables = new TableFilterRules { Exact = new List<string> { "t_secret" } }
        });
        (string workspaceRoot, _) = CreateWorkspace();
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out/{{table.className}}.java", true) };
        CodeGenerator generator = CreateGenerator();

        GenerationPreview preview = await generator.BuildPreviewAsync(
            new GenerationRequest(
                package,
                new[] { CreateTable("t_secret"), CreateTable("sys_user"), CreateTable("t_asset") },
                files,
                workspaceRoot,
                "out",
                includePlatformTables: true),
            null,
            CancellationToken.None);

        Assert.Equal(2, preview.NewCount);
        PlatformTableExclusion secret = Assert.Single(preview.PlatformTableExcluded, item => item.TableName == "t_secret");
        Assert.True(secret.Excluded);
        Assert.StartsWith("exclude:", secret.Rule);
        Assert.DoesNotContain(preview.Entries, entry => entry.TableName == "t_secret");
    }

    /// <summary>
    /// root=web 的条目以 workspaceRoot/webDirectory 为基准解析；root=workspace 的条目以工作区根解析。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_WorkspaceAndWebRoots_ResolveExpectedAbsolutePaths()
    {
        TemplatePackageInfo package = await CreatePackageAsync(new TemplateManifest
        {
            Name = "root-pkg",
            Description = "输出根测试包",
            Engine = "scriban",
            BasePackage = "com.example",
            Files = new List<TemplateFileEntry>
            {
                new() { Template = "web-api.ts.scriban", Output = "src/api/{{table.variableName}}.ts", Root = "web", Enabled = true },
                new() { Template = "pom.xml.scriban", Output = "pom.xml", Root = "workspace", Enabled = true }
            }
        });
        (string workspaceRoot, _) = CreateWorkspace();
        var files = new[]
        {
            new TemplateFileSelection("web-api.ts.scriban", "src/api/{{table.variableName}}.ts", true, TemplateScope.Table, TemplateOutputRoot.Web),
            new TemplateFileSelection("pom.xml.scriban", "pom.xml", true, TemplateScope.Table, TemplateOutputRoot.Workspace)
        };
        CodeGenerator generator = CreateGenerator();

        GenerationPreview preview = await generator.BuildPreviewAsync(
            new GenerationRequest(package, new[] { CreateTable("t_asset") }, files, workspaceRoot, "gen", webDirectory: "frontend"),
            null,
            CancellationToken.None);

        Assert.Contains(preview.Entries, entry => entry.AbsolutePath == Path.Combine(workspaceRoot, "frontend", "src", "api", "tAsset.ts"));
        Assert.Contains(preview.Entries, entry => entry.AbsolutePath == Path.Combine(workspaceRoot, "pom.xml"));
    }

    /// <summary>
    /// root=web / workspace 的条目在渲染期出现 .. 段（路径模板经变量渲出）时整单结构化拒绝（禁止目录穿越）；
    /// 静态字面量 .. 已在包加载阶段拒绝（见 TemplateCapabilityManifestTests）。
    /// </summary>
    [Theory]
    [InlineData("web")]
    [InlineData("workspace")]
    public async Task BuildPreviewAsync_ParentTraversalUnderFixedRoot_Rejected(string root)
    {
        TemplateOutputRoot outputRoot = TemplateOutputRootParser.Parse(root, "evil.scriban")!.Value;
        TemplatePackageInfo package = await CreatePackageAsync(new TemplateManifest
        {
            Name = "escape-pkg",
            Description = "逃逸拒绝测试包",
            Engine = "scriban",
            BasePackage = "com.example",
            Files = new List<TemplateFileEntry>
            {
                new() { Template = "evil.scriban", Output = "{{vars.evil}}/evil.txt", Root = root, Scope = "module", Enabled = true }
            }
        },
        ("evil.scriban", "module {{ module.name }}\n"));
        (string workspaceRoot, _) = CreateWorkspace();
        var files = new[] { new TemplateFileSelection("evil.scriban", "{{vars.evil}}/evil.txt", true, TemplateScope.Module, outputRoot) };
        var variables = new Dictionary<string, object?> { ["evil"] = ".." };
        CodeGenerator generator = CreateGenerator();

        GenerationException exception = await Assert.ThrowsAsync<GenerationException>(
            () => generator.BuildPreviewAsync(
                new GenerationRequest(
                    package,
                    Array.Empty<TableInfo>(),
                    files,
                    workspaceRoot,
                    "gen",
                    variables: variables,
                    modules: new[] { new ModuleContext("app") }),
                null,
                CancellationToken.None));
        Assert.Contains("禁止越出基准目录", exception.Message);
    }

    /// <summary>
    /// 包段连续重复与非小写时分别产出 DuplicatePackageSegment / NonLowercasePackageSegment 告警。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_PackageSegmentShapeWarnings_Emitted()
    {
        TemplatePackageInfo package = await CreatePackageAsync(new TemplateManifest
        {
            Name = "segment-pkg",
            Description = "包段形态测试包",
            Engine = "scriban",
            BasePackage = "com.simple.common",
            Files = new List<TemplateFileEntry>
            {
                new() { Template = "dup.java.scriban", Output = "src/main/java/{{package.dir}}/common/entity/{{table.className}}.java", Enabled = true },
                new() { Template = "case.java.scriban", Output = "src/main/java/{{package.dir}}/{{table.variableName}}/{{table.className}}.java", Enabled = true }
            }
        });
        (string workspaceRoot, _) = CreateWorkspace();
        var files = new[]
        {
            new TemplateFileSelection("dup.java.scriban", "src/main/java/{{package.dir}}/common/entity/{{table.className}}.java", true),
            new TemplateFileSelection("case.java.scriban", "src/main/java/{{package.dir}}/{{table.variableName}}/{{table.className}}.java", true)
        };
        CodeGenerator generator = CreateGenerator();

        GenerationPreview preview = await generator.BuildPreviewAsync(
            new GenerationRequest(package, new[] { CreateTable("t_asset") }, files, workspaceRoot, "gen"),
            null,
            CancellationToken.None);

        PathWarningInfo duplicate = Assert.Single(preview.PathWarnings, warning => warning.Kind == PathWarningInfo.DuplicatePackageSegment);
        Assert.Equal("dup.java.scriban", duplicate.TemplateName);
        PathWarningInfo nonLowercase = Assert.Single(preview.PathWarnings, warning => warning.Kind == PathWarningInfo.NonLowercasePackageSegment);
        Assert.Equal("case.java.scriban", nonLowercase.TemplateName);
    }

    /// <summary>
    /// 模板正文静态用 hump2Underline(table.className) 反推物理表名时产出 TableNameRoundTripMismatch 告警。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_TableNameRoundTrip_WarningEmitted()
    {
        TemplatePackageInfo package = await CreatePackageAsync(
            new TemplateManifest
            {
                Name = "roundtrip-pkg",
                Description = "物理表名反推测试包",
                Engine = "scriban",
                BasePackage = "com.example",
                Files = new List<TemplateFileEntry>
                {
                    new() { Template = "rls.sql.scriban", Output = "sql/{{table.rawName}}.sql", Enabled = true }
                }
            },
            ("rls.sql.scriban", "ALTER TABLE {{ tool.hump2Underline(table.className) }} ENABLE ROW LEVEL SECURITY;\n"));
        (string workspaceRoot, _) = CreateWorkspace();
        var files = new[] { new TemplateFileSelection("rls.sql.scriban", "sql/{{table.rawName}}.sql", true) };
        CodeGenerator generator = CreateGenerator();

        GenerationPreview preview = await generator.BuildPreviewAsync(
            new GenerationRequest(package, new[] { CreateTable("sys_user") }, files, workspaceRoot, "gen"),
            null,
            CancellationToken.None);

        PathWarningInfo warning = Assert.Single(preview.PathWarnings, item => item.Kind == PathWarningInfo.TableNameRoundTripMismatch);
        Assert.Equal("rls.sql.scriban", warning.TemplateName);
        Assert.Contains("table.rawName", warning.Message);
    }

    /// <summary>
    /// 权限模块告警口径：传了 modules 时自动派生为模块名 kebab 形态，不告警；
    /// 仅显式 variables.permissionModule / manifest.permissionModule 与模块名不一致时产出 PermissionKeyMismatch。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_PermissionModule_ExplicitMismatchOnly()
    {
        TemplatePackageInfo package = await CreatePackageAsync(new TemplateManifest
        {
            Name = "permission-pkg",
            Description = "权限键测试包",
            Engine = "scriban",
            BasePackage = "com.simple.usercenter",
            Files = new List<TemplateFileEntry>
            {
                new() { Template = "entity.java.scriban", Output = "out/{{table.className}}.java", Enabled = true }
            }
        });
        (string workspaceRoot, _) = CreateWorkspace();
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out/{{table.className}}.java", true) };
        var modules = new[] { new ModuleContext("user-center") };
        CodeGenerator generator = CreateGenerator();

        // 未显式声明：modules 自动派生 user-center，与权限点清单同源，不告警
        GenerationPreview autoDerived = await generator.BuildPreviewAsync(
            new GenerationRequest(package, new[] { CreateTable("t_asset") }, files, workspaceRoot, "gen", modules: modules),
            null,
            CancellationToken.None);
        Assert.DoesNotContain(autoDerived.PathWarnings, item => item.Kind == PathWarningInfo.PermissionKeyMismatch);
        Assert.Equal("user-center", autoDerived.NamingReport[0].PermissionKey);

        // 显式不一致：告警
        var mismatchedVariables = new Dictionary<string, object?> { ["permissionModule"] = "other-module" };
        GenerationPreview mismatched = await generator.BuildPreviewAsync(
            new GenerationRequest(
                package,
                new[] { CreateTable("t_asset") },
                files,
                workspaceRoot,
                "gen",
                variables: mismatchedVariables,
                modules: modules),
            null,
            CancellationToken.None);
        PathWarningInfo warning = Assert.Single(mismatched.PathWarnings, item => item.Kind == PathWarningInfo.PermissionKeyMismatch);
        Assert.Contains("other-module", warning.Message);
        Assert.Contains("user-center", warning.Message);

        // 显式一致：不告警
        var matchedVariables = new Dictionary<string, object?> { ["permissionModule"] = "user-center" };
        GenerationPreview matched = await generator.BuildPreviewAsync(
            new GenerationRequest(
                package,
                new[] { CreateTable("t_asset") },
                files,
                workspaceRoot,
                "gen",
                variables: matchedVariables,
                modules: modules),
            null,
            CancellationToken.None);
        Assert.DoesNotContain(matched.PathWarnings, item => item.Kind == PathWarningInfo.PermissionKeyMismatch);
    }

    /// <summary>
    /// workspaceRoot 含带 modules 的 pom.xml 且 codeDirectory 未落入模块目录时产出 WorkspaceRootMayBeAggregate。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_AggregateWorkspaceRoot_WarningEmitted()
    {
        TemplatePackageInfo package = await CreatePackageAsync(new TemplateManifest
        {
            Name = "aggregate-pkg",
            Description = "聚合根测试包",
            Engine = "scriban",
            BasePackage = "com.example",
            Files = new List<TemplateFileEntry>
            {
                new() { Template = "entity.java.scriban", Output = "out/{{table.className}}.java", Enabled = true }
            }
        });
        (string workspaceRoot, _) = CreateWorkspace();
        await File.WriteAllTextAsync(
            Path.Combine(workspaceRoot, "pom.xml"),
            "<project><modules><module>app-server</module><module>app-asset</module></modules></project>");
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out/{{table.className}}.java", true) };
        CodeGenerator generator = CreateGenerator();

        GenerationPreview preview = await generator.BuildPreviewAsync(
            new GenerationRequest(
                package,
                new[] { CreateTable("t_asset") },
                files,
                workspaceRoot,
                "gen",
                codeDirectory: "src/main/java/com/example"),
            null,
            CancellationToken.None);

        Assert.Single(preview.PathWarnings, item => item.Kind == PathWarningInfo.WorkspaceRootMayBeAggregate);

        // codeDirectory 落入模块目录时不告警
        GenerationPreview insideModule = await generator.BuildPreviewAsync(
            new GenerationRequest(
                package,
                new[] { CreateTable("t_asset") },
                files,
                workspaceRoot,
                "gen",
                codeDirectory: "app-asset/src/main/java/com/example"),
            null,
            CancellationToken.None);
        Assert.DoesNotContain(insideModule.PathWarnings, item => item.Kind == PathWarningInfo.WorkspaceRootMayBeAggregate);
    }

    /// <summary>
    /// 覆盖告警：仅选后端且缺 View 层时同时产出 NoBackendView 与 OnlyBackendGenerated；
    /// 选中 View 层后 NoBackendView 消失。
    /// </summary>
    [Fact]
    public async Task BuildCoverageWarnings_BackendSelection_ReportsMissingViewAndWeb()
    {
        TemplatePackageInfo package = await CreatePackageAsync(new TemplateManifest
        {
            Name = "coverage-pkg",
            Description = "覆盖告警测试包",
            Engine = "scriban",
            BasePackage = "com.example",
            Files = new List<TemplateFileEntry>
            {
                new() { Template = "controller.java.scriban", Output = "src/main/java/x/controller/{{table.className}}Controller.java", Enabled = true },
                new() { Template = "view.java.scriban", Output = "src/main/java/x/view/{{table.className}}View.java", Enabled = true }
            }
        });
        (string workspaceRoot, _) = CreateWorkspace();
        CodeGenerator generator = CreateGenerator();

        GenerationPreview controllerOnly = await generator.BuildPreviewAsync(
            new GenerationRequest(
                package,
                new[] { CreateTable("t_asset") },
                new[] { new TemplateFileSelection("controller.java.scriban", "src/main/java/x/controller/{{table.className}}Controller.java", true) },
                workspaceRoot,
                "gen"),
            null,
            CancellationToken.None);

        Assert.Contains(controllerOnly.CoverageWarnings, warning => warning.Kind == CoverageWarningInfo.NoBackendView);
        Assert.Contains(controllerOnly.CoverageWarnings, warning => warning.Kind == CoverageWarningInfo.OnlyBackendGenerated);

        GenerationPreview withView = await generator.BuildPreviewAsync(
            new GenerationRequest(
                package,
                new[] { CreateTable("t_asset") },
                new[]
                {
                    new TemplateFileSelection("controller.java.scriban", "src/main/java/x/controller/{{table.className}}Controller.java", true),
                    new TemplateFileSelection("view.java.scriban", "src/main/java/x/view/{{table.className}}View.java", true)
                },
                workspaceRoot,
                "gen"),
            null,
            CancellationToken.None);

        Assert.DoesNotContain(withView.CoverageWarnings, warning => warning.Kind == CoverageWarningInfo.NoBackendView);
        Assert.Contains(withView.CoverageWarnings, warning => warning.Kind == CoverageWarningInfo.OnlyBackendGenerated);
    }

    /// <summary>
    /// 在临时目录创建测试模板包并加载；模板正文缺省为按类名渲染的 Java 类壳。
    /// </summary>
    /// <param name="manifest">清单模型。</param>
    /// <param name="templateOverrides">模板正文覆盖项（模板相对路径, 正文）。</param>
    /// <returns>加载后的模板包信息。</returns>
    private async Task<TemplatePackageInfo> CreatePackageAsync(
        TemplateManifest manifest,
        params (string Template, string Content)[] templateOverrides)
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
            string content = templateOverrides
                .Where(item => item.Template == entry.Template)
                .Select(item => item.Content)
                .FirstOrDefault() ?? "public class {{table.className}} {}\n";
            await File.WriteAllTextAsync(Path.Combine(packageRoot, entry.Template), content);
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
