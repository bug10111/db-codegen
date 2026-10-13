using DbCodeGen.Core.Config;
using DbCodeGen.Core.Generation;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbCodeGen.Core.Tests.Generation;

/// <summary>
/// 空迭代告警生成链路集成测试与输出路径产物级校验测试：
/// 覆盖空集合/未提供变量零迭代的 pathWarnings 归并（preview 与 generate 日志双入口）、
/// 守卫模式降噪文案、非空集合零告警，以及 outputPath 管道表达式放行与渲染后字面量非法字符拒绝。
/// </summary>
public sealed class EmptyIterationGenerationTests : IDisposable
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
    /// project 模板循环未提供的 modules 根变量时，dry-run 预览应归并出 EmptyIteration 告警（非阻断）。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_ProjectLoopMissingModules_ProducesEmptyIterationWarning()
    {
        (TemplatePackageInfo package, string workspaceRoot) = await CreateProjectPackageAsync(
            "{{ for m in modules }}module:{{ m.name }};{{ end }}\n");
        var generator = CreateGenerator();
        GenerationRequest request = CreateProjectRequest(package, workspaceRoot);

        GenerationPreview preview = await generator.BuildPreviewAsync(request, null, CancellationToken.None);

        PathWarningInfo warning = Assert.Single(preview.PathWarnings);
        Assert.Equal(PathWarningInfo.EmptyIteration, warning.Kind);
        Assert.Contains("modules", warning.Message);
        Assert.Contains("零迭代", warning.Message);
    }

    /// <summary>
    /// project 模板空循环由同变量条件守卫包裹时，告警文案应含守卫模式可忽略降噪指引。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_GuardedEmptyLoop_MessageMentionsIgnorable()
    {
        (TemplatePackageInfo package, string workspaceRoot) = await CreateProjectPackageAsync(
            "{{ if modules }}守卫文案占位\n{{ for m in modules }}module:{{ m.name }};{{ end }}\n{{ end }}\n");
        var generator = CreateGenerator();
        GenerationRequest request = CreateProjectRequest(package, workspaceRoot);

        GenerationPreview preview = await generator.BuildPreviewAsync(request, null, CancellationToken.None);

        PathWarningInfo warning = Assert.Single(preview.PathWarnings);
        Assert.Equal(PathWarningInfo.EmptyIteration, warning.Kind);
        Assert.Contains("可忽略", warning.Message);
    }

    /// <summary>
    /// project 模板循环非空 modules 清单时不应产出 EmptyIteration 告警。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_NonEmptyModulesLoop_NoIterationWarning()
    {
        (TemplatePackageInfo package, string workspaceRoot) = await CreateProjectPackageAsync(
            "{{ for m in modules }}module:{{ m.name }};{{ end }}\n");
        var generator = CreateGenerator();
        GenerationRequest request = CreateProjectRequest(
            package, workspaceRoot,
            variables: new Dictionary<string, object?>(),
            modules: new[] { new ModuleContext("account") });

        GenerationPreview preview = await generator.BuildPreviewAsync(request, null, CancellationToken.None);

        Assert.DoesNotContain(preview.PathWarnings, warning => warning.Kind == PathWarningInfo.EmptyIteration);
        Assert.Contains("module:account;", Assert.Single(preview.Entries).Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// generate 写盘入口应把空迭代告警并入生成日志（warning 级），保证不预览直生成场景信号同样可见。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_EmptyIteration_EmitsWarningLog()
    {
        (TemplatePackageInfo package, string workspaceRoot) = await CreateProjectPackageAsync(
            "{{ for m in modules }}module:{{ m.name }};{{ end }}\n");
        var generator = CreateGenerator();
        GenerationRequest request = CreateProjectRequest(package, workspaceRoot);

        GenerationResult result = await generator.GenerateAsync(request, null, CancellationToken.None);

        Assert.Contains(
            result.Logs,
            log => log.Level == GenerationLogLevel.Warning && log.Message.Contains("EmptyIteration"));
    }

    /// <summary>
    /// outputPath 模板含管道表达式（如 string.upcase 过滤器）时应放行渲染，产物路径按表达式求值结果落位。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_OutputPathWithPipelineExpression_Renders()
    {
        (TemplatePackageInfo package, string workspaceRoot, TemplateFileSelection selection) =
            await CreateTablePackageAsync("out/{{ table.variableName | string.upcase }}.java", "public class {{ table.className }} {}\n");
        var generator = CreateGenerator();
        GenerationRequest request = new(
            package, new[] { CreateSysUserTable() }, new[] { selection }, workspaceRoot, "gen");

        GenerationPreview preview = await generator.BuildPreviewAsync(request, null, CancellationToken.None);

        GenerationFileEntry entry = Assert.Single(preview.Entries);
        Assert.Equal("out/SYSUSER.java", entry.RelativePath);
    }

    /// <summary>
    /// 渲染后产物路径含字面量非法字符（如 |）时应在产物路径单点结构化拒绝，非法路径不进入分类与写盘。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_RenderedPathWithInvalidChar_Rejected()
    {
        (TemplatePackageInfo package, string workspaceRoot, TemplateFileSelection selection) =
            await CreateTablePackageAsync("out/bad|name.java", "public class {{ table.className }} {}\n");
        var generator = CreateGenerator();
        GenerationRequest request = new(
            package, new[] { CreateSysUserTable() }, new[] { selection }, workspaceRoot, "gen");

        GenerationException exception = await Assert.ThrowsAsync<GenerationException>(
            () => generator.BuildPreviewAsync(request, null, CancellationToken.None));

        Assert.Contains("非法字符", exception.Message);
        Assert.Contains("bad|name.java", exception.Message);
    }

    /// <summary>
    /// 构造含单个 project 作用域模板的测试包并返回工作区根。
    /// </summary>
    /// <param name="templateBody">project 模板正文。</param>
    /// <returns>加载后的模板包与工作区根。</returns>
    private async Task<(TemplatePackageInfo Package, string WorkspaceRoot)> CreateProjectPackageAsync(string templateBody)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
        _tempRoots.Add(tempRoot);
        string packageDir = Path.Combine(tempRoot, "user-pkg");
        Directory.CreateDirectory(packageDir);
        await File.WriteAllTextAsync(Path.Combine(packageDir, "project-info.cs.scriban"), templateBody);
        await File.WriteAllTextAsync(
            Path.Combine(packageDir, TemplatePackageLoader.ManifestFileName),
            """
            {
              "name": "user-pkg",
              "description": "iteration warning test package",
              "engine": "scriban",
              "basePackage": "com.example",
              "files": [
                { "template": "project-info.cs.scriban", "output": "Generated.cs", "scope": "project" }
              ]
            }
            """);

        TemplatePackageInfo package = await TemplatePackageLoader.LoadFromDirectoryAsync(packageDir, isBuiltin: false, CancellationToken.None);
        string workspaceRoot = Path.Combine(tempRoot, "workspace");
        Directory.CreateDirectory(workspaceRoot);
        return (package, workspaceRoot);
    }

    /// <summary>
    /// 构造含单个 table 作用域模板的测试包，返回模板包、工作区根与勾选条目。
    /// </summary>
    /// <param name="outputPath">模板 outputPath 原文（承载管道表达式或非法字符场景）。</param>
    /// <param name="templateBody">模板正文。</param>
    /// <returns>模板包、工作区根与勾选条目。</returns>
    private async Task<(TemplatePackageInfo Package, string WorkspaceRoot, TemplateFileSelection Selection)> CreateTablePackageAsync(
        string outputPath, string templateBody)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
        _tempRoots.Add(tempRoot);
        string packageDir = Path.Combine(tempRoot, "user-pkg");
        Directory.CreateDirectory(packageDir);
        await File.WriteAllTextAsync(Path.Combine(packageDir, "entity.cs.scriban"), templateBody);
        await File.WriteAllTextAsync(
            Path.Combine(packageDir, TemplatePackageLoader.ManifestFileName),
            $$"""
            {
              "name": "user-pkg",
              "description": "output path test package",
              "engine": "scriban",
              "basePackage": "com.example",
              "files": [
                { "template": "entity.cs.scriban", "output": {{ System.Text.Json.JsonSerializer.Serialize(outputPath) }} }
              ]
            }
            """);

        TemplatePackageInfo package = await TemplatePackageLoader.LoadFromDirectoryAsync(packageDir, isBuiltin: false, CancellationToken.None);
        string workspaceRoot = Path.Combine(tempRoot, "workspace");
        Directory.CreateDirectory(workspaceRoot);
        return (package, workspaceRoot, new TemplateFileSelection("entity.cs.scriban", outputPath, true));
    }

    /// <summary>
    /// 构造 project 作用域生成请求，可选携带变量与模块清单。
    /// </summary>
    /// <param name="package">模板包。</param>
    /// <param name="workspaceRoot">工作区根。</param>
    /// <param name="variables">调用方变量集合，可为空。</param>
    /// <param name="modules">模块清单，可为空。</param>
    /// <returns>生成请求。</returns>
    private static GenerationRequest CreateProjectRequest(
        TemplatePackageInfo package,
        string workspaceRoot,
        IReadOnlyDictionary<string, object?>? variables = null,
        IReadOnlyList<ModuleContext>? modules = null)
    {
        var files = new[] { new TemplateFileSelection("project-info.cs.scriban", "Generated.cs", true, TemplateScope.Project) };
        return new GenerationRequest(
            package,
            Array.Empty<TableInfo>(),
            files,
            workspaceRoot,
            "gen",
            basePackageOverride: null,
            dataSource: null,
            codeDirectory: null,
            duplicateFileStrategy: DuplicateFileStrategy.Overwrite,
            persistLastUsedPaths: false,
            variables: variables,
            modules: modules);
    }

    /// <summary>
    /// 构造样例表元数据，表名 sys_user。
    /// </summary>
    /// <returns>样例表。</returns>
    private static TableInfo CreateSysUserTable()
    {
        return new TableInfo
        {
            RawName = "sys_user",
            ClassName = TableInfo.ToPascalCase("sys_user"),
            VariableName = TableInfo.ToCamelCase("sys_user")
        };
    }

    /// <summary>
    /// 构造批量生成服务（无映射与表目录依赖，配置服务为内存桩）。
    /// </summary>
    /// <returns>批量生成服务。</returns>
    private static CodeGenerator CreateGenerator()
    {
        return new CodeGenerator(
            new TemplateEngine(),
            new TemplateFileWriter(),
            new FileWriter(NullLogger<FileWriter>.Instance),
            new FakeConfigServiceStub(),
            NullLogger<CodeGenerator>.Instance);
    }

    /// <summary>
    /// 配置服务测试替身，仅承载内存配置快照供生成服务构造依赖。
    /// </summary>
    private sealed class FakeConfigServiceStub : IConfigService
    {
        /// <summary>
        /// 内存配置快照。
        /// </summary>
        public AppConfig Current { get; } = new();

        /// <inheritdoc />
        public string ConfigFilePath => Path.Combine(Path.GetTempPath(), "DbCodeGenTests", "config.json");

        /// <inheritdoc />
        public event EventHandler? ConfigChanged;

        /// <inheritdoc />
        public AppConfig Load()
        {
            return Current;
        }

        /// <inheritdoc />
        public void Save()
        {
            ConfigChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <inheritdoc />
        public GenerationDefaults GetGenerationDefaults()
        {
            return new GenerationDefaults(Current.WorkspaceRoot, Current.LastRelativeOutputRoot);
        }

        /// <inheritdoc />
        public string? GetLlmApiKey()
        {
            return null;
        }

        /// <inheritdoc />
        public void ChangeDataDirectory(string dataDirectory)
        {
            throw new NotSupportedException("测试替身不支持切换数据目录。");
        }
    }
}
