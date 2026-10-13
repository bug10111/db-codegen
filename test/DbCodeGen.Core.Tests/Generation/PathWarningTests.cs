using System.Text.Json;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Generation;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbCodeGen.Core.Tests.Generation;

/// <summary>
/// 输出路径坏路径预检单元测试：dry-run 回执结构化告警（包段为空 / 连续分隔符）的触发、
/// 归并计数、非阻断性与生成日志可见性。
/// </summary>
public sealed class PathWarningTests : IDisposable
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
    /// 输出路径引用 package.dir 占位但基础包名解析为空时，dry-run 回执应产出
    /// EmptyPackageDir 与 ConsecutiveSeparators 两条结构化告警，且不阻断文件计划产出。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_EmptyPackageDir_EmitsStructuredWarningsWithoutBlocking()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreatePackageAsync(
            basePackage: null,
            new[] { ("entity.java.scriban", "src/main/java/{{package.dir}}/{{table.variableName}}.java", "public class {{table.className}} {}\n") });
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "src/main/java/{{package.dir}}/{{table.variableName}}.java", true) };
        var generator = CreateGenerator();

        GenerationPreview preview = await generator.BuildPreviewAsync(
            new GenerationRequest(package, new[] { CreateTable() }, files, workspaceRoot, "gen"),
            null,
            CancellationToken.None);

        // 非阻断：坏路径仅告警，文件计划照常产出
        Assert.Equal(1, preview.NewCount);
        Assert.Equal("src/main/java//sysUser.java", Assert.Single(preview.Entries).RelativePath);

        PathWarningInfo emptyDir = Assert.Single(preview.PathWarnings, warning => warning.Kind == PathWarningInfo.EmptyPackageDir);
        Assert.Equal("entity.java.scriban", emptyDir.TemplateName);
        Assert.Equal(1, emptyDir.Occurrences);
        Assert.Contains("package.dir", emptyDir.Message);
        Assert.Contains("基础包名", emptyDir.Message);

        PathWarningInfo separators = Assert.Single(preview.PathWarnings, warning => warning.Kind == PathWarningInfo.ConsecutiveSeparators);
        Assert.Contains("//", separators.SamplePath);
    }

    /// <summary>
    /// 同一模板同种告警跨表多次出现时应归并为一条并累计次数（与未映射类型清单同款归并口径）。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_SameTemplateWarningAcrossTables_AggregatesOccurrences()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreatePackageAsync(
            basePackage: null,
            new[] { ("entity.java.scriban", "src/main/java/{{package.dir}}/{{table.variableName}}.java", "public class {{table.className}} {}\n") });
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "src/main/java/{{package.dir}}/{{table.variableName}}.java", true) };
        var generator = CreateGenerator();
        var tables = new[] { CreateTable(), CreateSecondTable() };

        GenerationPreview preview = await generator.BuildPreviewAsync(
            new GenerationRequest(package, tables, files, workspaceRoot, "gen"),
            null,
            CancellationToken.None);

        Assert.Equal(2, preview.Entries.Count);
        PathWarningInfo emptyDir = Assert.Single(preview.PathWarnings, warning => warning.Kind == PathWarningInfo.EmptyPackageDir);
        Assert.Equal(2, emptyDir.Occurrences);
        PathWarningInfo separators = Assert.Single(preview.PathWarnings, warning => warning.Kind == PathWarningInfo.ConsecutiveSeparators);
        Assert.Equal(2, separators.Occurrences);
        Assert.Equal(2, preview.PathWarnings.Count);
    }

    /// <summary>
    /// 路径模板含字面双斜杠而包段正常时，应仅产出连续分隔符告警、不误报包段为空。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_LiteralConsecutiveSeparators_EmitsOnlySeparatorWarning()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreatePackageAsync(
            basePackage: "com.example",
            new[] { ("entity.java.scriban", "out//{{table.variableName}}.java", "public class {{table.className}} {}\n") });
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out//{{table.variableName}}.java", true) };
        var generator = CreateGenerator();

        GenerationPreview preview = await generator.BuildPreviewAsync(
            new GenerationRequest(package, new[] { CreateTable() }, files, workspaceRoot, "gen"),
            null,
            CancellationToken.None);

        Assert.Equal(1, preview.NewCount);
        PathWarningInfo separators = Assert.Single(preview.PathWarnings);
        Assert.Equal(PathWarningInfo.ConsecutiveSeparators, separators.Kind);
        Assert.Equal("out//sysUser.java", separators.SamplePath);
        Assert.DoesNotContain(preview.PathWarnings, warning => warning.Kind == PathWarningInfo.EmptyPackageDir);
    }

    /// <summary>
    /// 输出路径模板合法且包段有效时，dry-run 回执不应产出任何路径告警。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_ValidOutputPath_ProducesNoPathWarnings()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreatePackageAsync(
            basePackage: "com.example",
            new[] { ("entity.java.scriban", "src/main/java/{{package.dir}}/{{table.variableName}}.java", "public class {{table.className}} {}\n") });
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "src/main/java/{{package.dir}}/{{table.variableName}}.java", true) };
        var generator = CreateGenerator();

        GenerationPreview preview = await generator.BuildPreviewAsync(
            new GenerationRequest(package, new[] { CreateTable() }, files, workspaceRoot, "gen"),
            null,
            CancellationToken.None);

        Assert.Empty(preview.PathWarnings);
        Assert.Equal("src/main/java/com/example/sysUser.java", Assert.Single(preview.Entries).RelativePath);
    }

    /// <summary>
    /// generate 内部重算 dry-run 时检出坏路径，应在生成日志中以警告级条目可见（不阻断写盘）。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_PathWarningDetected_WarningVisibleInLogs()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreatePackageAsync(
            basePackage: null,
            new[] { ("entity.java.scriban", "src/main/java/{{package.dir}}/{{table.variableName}}.java", "public class {{table.className}} {}\n") });
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "src/main/java/{{package.dir}}/{{table.variableName}}.java", true) };
        var generator = CreateGenerator();

        GenerationResult result = await generator.GenerateAsync(
            new GenerationRequest(package, new[] { CreateTable() }, files, workspaceRoot, "gen"),
            null,
            CancellationToken.None);

        Assert.Equal(1, result.Generated);

        // 两条告警（空包段 + 连续分隔符）均应以警告级日志可见，且定位到模板
        List<GenerationLogEntry> warningLogs = result.Logs
            .Where(log => log.Level == GenerationLogLevel.Warning && log.Message.Contains("输出路径告警"))
            .ToList();
        Assert.Equal(2, warningLogs.Count);
        Assert.Contains(warningLogs, log => log.Message.Contains("EmptyPackageDir"));
        Assert.Contains(warningLogs, log => log.Message.Contains("ConsecutiveSeparators"));
        Assert.Contains(warningLogs, log => log.Message.Contains("entity.java.scriban"));
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
    /// 在临时目录下按指定基础包名与文件清单创建测试模板包并加载。
    /// </summary>
    /// <param name="basePackage">清单基础包名，null 表示不配置（package.dir 解析为空）。</param>
    /// <param name="files">模板相对路径、输出路径模板与模板内容三元组清单。</param>
    /// <returns>加载后的模板包信息与临时根目录。</returns>
    private async Task<(TemplatePackageInfo Package, string TempRoot)> CreatePackageAsync(
        string? basePackage,
        IReadOnlyList<(string Template, string Output, string Content)> files)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
        _tempRoots.Add(tempRoot);
        string packageDir = Path.Combine(tempRoot, "user-pkg");
        Directory.CreateDirectory(packageDir);

        var manifest = new TemplateManifest
        {
            Name = "user-pkg",
            Description = "路径告警测试包",
            Engine = "scriban",
            BasePackage = basePackage,
            TypeMap = new Dictionary<string, string>(),
            Files = files.Select(file => new TemplateFileEntry
            {
                Template = file.Template,
                Output = file.Output,
                Enabled = true
            }).ToList()
        };

        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        await File.WriteAllTextAsync(Path.Combine(packageDir, TemplatePackageLoader.ManifestFileName), JsonSerializer.Serialize(manifest, jsonOptions));
        foreach ((string template, _, string content) in files)
        {
            await File.WriteAllTextAsync(Path.Combine(packageDir, template), content);
        }

        TemplatePackageInfo package = await TemplatePackageLoader.LoadFromDirectoryAsync(packageDir, isBuiltin: false, CancellationToken.None);
        return (package, tempRoot);
    }

    /// <summary>
    /// 在临时根目录下创建工作区根与相对输出根目录。
    /// </summary>
    /// <param name="tempRoot">测试临时根目录。</param>
    /// <returns>工作区根与输出根目录。</returns>
    private static (string WorkspaceRoot, string OutputRoot) CreateWorkspace(string tempRoot)
    {
        string workspaceRoot = Path.Combine(tempRoot, "workspace");
        Directory.CreateDirectory(workspaceRoot);
        string outputRoot = Path.Combine(workspaceRoot, "gen");
        return (workspaceRoot, outputRoot);
    }

    /// <summary>
    /// 构造样例表元数据，表名 sys_user。
    /// </summary>
    /// <returns>样例表。</returns>
    private static TableInfo CreateTable()
    {
        return new TableInfo
        {
            RawName = "sys_user",
            ClassName = TableInfo.ToPascalCase("sys_user"),
            VariableName = TableInfo.ToCamelCase("sys_user")
        };
    }

    /// <summary>
    /// 构造第二张样例表元数据，表名 sys_order，供归并计数测试使用。
    /// </summary>
    /// <returns>样例表。</returns>
    private static TableInfo CreateSecondTable()
    {
        return new TableInfo
        {
            RawName = "sys_order",
            ClassName = TableInfo.ToPascalCase("sys_order"),
            VariableName = TableInfo.ToCamelCase("sys_order")
        };
    }

    /// <summary>
    /// 配置服务测试替身，仅满足构造依赖，配置能力不参与本测试。
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
