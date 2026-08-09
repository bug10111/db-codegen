using System.Text.Json;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Generation;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbCodeGen.Core.Tests.Generation;

/// <summary>
/// 批量生成服务单元测试，覆盖 dry-run 分类（新增/跳过/覆盖）、行尾归一化、防目录穿越、
/// 渲染失败整单失败、覆盖确认安全线、内部重算 dry-run、进度报告与最近输出根回写。
/// </summary>
public sealed class CodeGeneratorTests : IDisposable
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
    /// 目标文件不存在应分类为新增，绝对路径落在工作区根/相对输出根下。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_NewTarget_ClassifiesNew()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateDefaultPackageAsync();
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out/{{table.variableName}}.java", true) };
        (CodeGenerator generator, _) = CreateGenerator();

        GenerationPreview preview = await generator.BuildPreviewAsync(CreateRequest(package, workspaceRoot, "gen", files), null, CancellationToken.None);

        Assert.Equal(1, preview.NewCount);
        Assert.Equal(0, preview.SkipCount);
        GenerationFileEntry entry = Assert.Single(preview.Entries);
        Assert.Equal(GenerationAction.New, entry.Action);
        Assert.Equal("out/sysUser.java", entry.RelativePath);
    }

    /// <summary>
    /// 表列含未映射类型时 dry-run 预览应返回未映射预检清单，全部命中时清单为空。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_UnmappedTypes_ReturnsPrecheckList()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateDefaultPackageAsync();
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out/{{table.variableName}}.java", true) };

        // 全局映射仅含 bigint，jsonb 未映射，预检应收集到 jsonb 且 bigint 不出现
        var configService = new FakeConfigService();
        configService.Current.TypeMappings = new List<TypeMappingEntry>
        {
            new() { DbType = "bigint", TargetType = "Long" }
        };
        var mappingService = new TypeMappingService(configService, NullLogger<TypeMappingService>.Instance);
        var generator = new CodeGenerator(
            new TemplateEngine(mappingService),
            new TemplateFileWriter(),
            new FileWriter(NullLogger<FileWriter>.Instance),
            configService,
            NullLogger<CodeGenerator>.Instance,
            mappingService);

        TableInfo table = CreateTable();
        table.SetColumns(new[]
        {
            new ColumnInfo { RawName = "id", PropertyName = "id", RawDbType = "bigint" },
            new ColumnInfo { RawName = "meta", PropertyName = "meta", RawDbType = "jsonb" }
        });

        GenerationPreview preview = await generator.BuildPreviewAsync(
            new GenerationRequest(package, new[] { table }, files, workspaceRoot, "gen"),
            null,
            CancellationToken.None);

        UnmappedTypeInfo unmapped = Assert.Single(preview.UnmappedTypes);
        Assert.Equal("jsonb", unmapped.DbType);
        Assert.Equal("sys_user", unmapped.TableName);
        Assert.Equal("meta", unmapped.ColumnName);
    }

    /// <summary>
    /// 既有文件内容与渲染结果仅行尾不同（CRLF 与 LF）应归一化后分类为跳过。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_LineEndingNormalized_ClassifiesSkip()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateDefaultPackageAsync();
        (string workspaceRoot, string outputRoot) = CreateWorkspace(tempRoot);
        string target = Path.Combine(outputRoot, "out", "sysUser.java");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "public class SysUser {}\r\n");
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out/{{table.variableName}}.java", true) };
        (CodeGenerator generator, _) = CreateGenerator();

        GenerationPreview preview = await generator.BuildPreviewAsync(CreateRequest(package, workspaceRoot, "gen", files), null, CancellationToken.None);

        Assert.Equal(1, preview.SkipCount);
        Assert.Equal(0, preview.OverwriteCount);
        Assert.Equal(GenerationAction.Skip, Assert.Single(preview.Entries).Action);
    }

    /// <summary>
    /// 既有文件内容与渲染结果不同应分类为覆盖。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_DifferentContent_ClassifiesOverwrite()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateDefaultPackageAsync();
        (string workspaceRoot, string outputRoot) = CreateWorkspace(tempRoot);
        string target = Path.Combine(outputRoot, "out", "sysUser.java");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "OLD CONTENT");
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out/{{table.variableName}}.java", true) };
        (CodeGenerator generator, _) = CreateGenerator();

        GenerationPreview preview = await generator.BuildPreviewAsync(CreateRequest(package, workspaceRoot, "gen", files), null, CancellationToken.None);

        Assert.Equal(1, preview.OverwriteCount);
        Assert.Equal(GenerationAction.Overwrite, Assert.Single(preview.Entries).Action);
    }

    /// <summary>
    /// 渲染后相对路径越出输出根应抛目录穿越异常并整单失败。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_PathTraversal_Throws()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateTempPackageAsync(new[]
        {
            ("entity.java.scriban", "{{table.variableName}}/evil.java", "public class {{table.className}} {}")
        });
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        TableInfo table = CreateTable();
        table.VariableName = "..";
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "{{table.variableName}}/evil.java", true) };
        (CodeGenerator generator, _) = CreateGenerator();

        var exception = await Assert.ThrowsAsync<GenerationException>(
            () => generator.BuildPreviewAsync(new GenerationRequest(package, new[] { table }, files, workspaceRoot, "gen"), null, CancellationToken.None));

        Assert.Contains("目录穿越", exception.Message);
    }

    /// <summary>
    /// 模板内容渲染失败应抛整单失败异常，携带模板文件名定位。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_RenderError_Throws()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateTempPackageAsync(new[]
        {
            ("broken.scriban", "out/broken.txt", "{{ if }}")
        });
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("broken.scriban", "out/broken.txt", true) };
        (CodeGenerator generator, _) = CreateGenerator();

        var exception = await Assert.ThrowsAsync<GenerationException>(
            () => generator.BuildPreviewAsync(CreateRequest(package, workspaceRoot, "gen", files), null, CancellationToken.None));

        Assert.Contains("模板渲染失败", exception.Message);
        Assert.Contains("broken.scriban", exception.Message);
    }

    /// <summary>
    /// 未勾选的模板文件应被排除在渲染与分类之外。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_UnselectedFile_Excluded()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateDefaultPackageAsync();
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[]
        {
            new TemplateFileSelection("entity.java.scriban", "out/{{table.variableName}}.java", false),
            new TemplateFileSelection("mapper.xml.scriban", "out/{{table.variableName}}.xml", true)
        };
        (CodeGenerator generator, _) = CreateGenerator();

        GenerationPreview preview = await generator.BuildPreviewAsync(CreateRequest(package, workspaceRoot, "gen", files), null, CancellationToken.None);

        GenerationFileEntry entry = Assert.Single(preview.Entries);
        Assert.Equal("out/sysUser.xml", entry.RelativePath);
    }

    /// <summary>
    /// 预览应报告渲染与 dry-run 分类两个阶段的进度。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_ReportsRenderingAndPreviewingProgress()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateDefaultPackageAsync();
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var progressValues = new List<GenerationProgress>();
        IProgress<GenerationProgress> progress = new SyncProgress<GenerationProgress>(progressValues.Add);
        var files = new[]
        {
            new TemplateFileSelection("entity.java.scriban", "out/{{table.variableName}}.java", true),
            new TemplateFileSelection("mapper.xml.scriban", "out/{{table.variableName}}.xml", true)
        };
        (CodeGenerator generator, _) = CreateGenerator();

        await generator.BuildPreviewAsync(CreateRequest(package, workspaceRoot, "gen", files), progress, CancellationToken.None);

        Assert.Contains(progressValues, value => value.Stage == GenerationStage.Rendering);
        Assert.Contains(progressValues, value => value.Stage == GenerationStage.Previewing);
        Assert.Equal(2, progressValues[^1].Total);
    }

    /// <summary>
    /// 无覆盖项时生成应写新增文件并计入跳过，且一并回写工作区根与最近相对输出根。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_NoOverwrite_WritesNewAndCountsSkip()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateDefaultPackageAsync();
        (string workspaceRoot, string outputRoot) = CreateWorkspace(tempRoot);
        string xmlTarget = Path.Combine(outputRoot, "out", "sysUser.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(xmlTarget)!);
        await File.WriteAllTextAsync(xmlTarget, "<mapper>SysUser</mapper>\n");
        var files = new[]
        {
            new TemplateFileSelection("entity.java.scriban", "out/{{table.variableName}}.java", true),
            new TemplateFileSelection("mapper.xml.scriban", "out/{{table.variableName}}.xml", true)
        };
        (CodeGenerator generator, FakeConfigService configService) = CreateGenerator();

        GenerationResult result = await generator.GenerateAsync(CreateRequest(package, workspaceRoot, "gen", files), null, null, CancellationToken.None);

        Assert.Equal(1, result.Generated);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Overwritten);
        Assert.Equal(0, result.Failed);
        Assert.False(result.IsCancelled);
        Assert.True(File.Exists(Path.Combine(outputRoot, "out", "sysUser.java")));
        Assert.Equal("public class SysUser {}\n", await File.ReadAllTextAsync(Path.Combine(outputRoot, "out", "sysUser.java")));
        Assert.Equal(workspaceRoot, configService.Current.WorkspaceRoot);
        Assert.Equal("gen", configService.Current.LastRelativeOutputRoot);
        Assert.True(configService.SaveCount >= 1);
    }

    /// <summary>
    /// 覆盖项经确认回调确认后应写盘覆盖，计数为覆盖。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_OverwriteConfirmed_WritesOverwrite()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateDefaultPackageAsync();
        (string workspaceRoot, string outputRoot) = CreateWorkspace(tempRoot);
        string target = Path.Combine(outputRoot, "out", "sysUser.java");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "OLD CONTENT");
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out/{{table.variableName}}.java", true) };
        (CodeGenerator generator, _) = CreateGenerator();

        bool confirmInvoked = false;
        GenerationResult result = await generator.GenerateAsync(
            CreateRequest(package, workspaceRoot, "gen", files),
            entries =>
            {
                confirmInvoked = true;
                Assert.Contains(entries, entry => entry.Action == GenerationAction.Overwrite);
                return Task.FromResult(true);
            },
            null,
            CancellationToken.None);

        Assert.True(confirmInvoked);
        Assert.Equal(1, result.Overwritten);
        Assert.False(result.IsCancelled);
        Assert.Equal("public class SysUser {}\n", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// 覆盖项经确认回调拒绝后应整单取消，不写任何文件。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_OverwriteCancelled_NoWrite()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateDefaultPackageAsync();
        (string workspaceRoot, string outputRoot) = CreateWorkspace(tempRoot);
        string target = Path.Combine(outputRoot, "out", "sysUser.java");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "OLD CONTENT");
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out/{{table.variableName}}.java", true) };
        (CodeGenerator generator, _) = CreateGenerator();

        GenerationResult result = await generator.GenerateAsync(
            CreateRequest(package, workspaceRoot, "gen", files),
            _ => Task.FromResult(false),
            null,
            CancellationToken.None);

        Assert.True(result.IsCancelled);
        Assert.Equal(0, result.Generated);
        Assert.Equal(0, result.Overwritten);
        Assert.Equal("OLD CONTENT", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// 存在覆盖项但未提供确认回调应整单取消，保证覆盖动作必经确认的安全线。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_NoConfirmCallbackWithOverwrite_Cancelled()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateDefaultPackageAsync();
        (string workspaceRoot, string outputRoot) = CreateWorkspace(tempRoot);
        string target = Path.Combine(outputRoot, "out", "sysUser.java");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "OLD CONTENT");
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out/{{table.variableName}}.java", true) };
        (CodeGenerator generator, _) = CreateGenerator();

        GenerationResult result = await generator.GenerateAsync(CreateRequest(package, workspaceRoot, "gen", files), null, null, CancellationToken.None);

        Assert.True(result.IsCancelled);
        Assert.Equal("OLD CONTENT", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// 不先预览直接生成应内部重算 dry-run 后写盘，验证写盘前必走 dry-run 的安全线。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_InternalDryRunRecomputed_WritesFiles()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateDefaultPackageAsync();
        (string workspaceRoot, string outputRoot) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out/{{table.variableName}}.java", true) };
        (CodeGenerator generator, _) = CreateGenerator();

        GenerationResult result = await generator.GenerateAsync(CreateRequest(package, workspaceRoot, "gen", files), null, null, CancellationToken.None);

        Assert.Equal(1, result.Generated);
        Assert.False(result.IsCancelled);
        Assert.True(File.Exists(Path.Combine(outputRoot, "out", "sysUser.java")));
    }

    /// <summary>
    /// 生成全流程应报告渲染、dry-run 分类与写盘三个阶段进度。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_ReportsAllStageProgress()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateDefaultPackageAsync();
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var progressValues = new List<GenerationProgress>();
        IProgress<GenerationProgress> progress = new SyncProgress<GenerationProgress>(progressValues.Add);
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out/{{table.variableName}}.java", true) };
        (CodeGenerator generator, _) = CreateGenerator();

        await generator.GenerateAsync(CreateRequest(package, workspaceRoot, "gen", files), null, progress, CancellationToken.None);

        Assert.Contains(progressValues, value => value.Stage == GenerationStage.Rendering);
        Assert.Contains(progressValues, value => value.Stage == GenerationStage.Previewing);
        Assert.Contains(progressValues, value => value.Stage == GenerationStage.Writing);
    }

    /// <summary>
    /// 预览在取消标记已触发时应抛取消异常。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_CancelledToken_Throws()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateDefaultPackageAsync();
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out/{{table.variableName}}.java", true) };
        (CodeGenerator generator, _) = CreateGenerator();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => generator.BuildPreviewAsync(CreateRequest(package, workspaceRoot, "gen", files), null, cts.Token));
    }

    /// <summary>
    /// 生成在取消标记已触发时应返回取消结果，不写任何文件。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_CancelledToken_ReturnsCancelledResult()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateDefaultPackageAsync();
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out/{{table.variableName}}.java", true) };
        (CodeGenerator generator, _) = CreateGenerator();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        GenerationResult result = await generator.GenerateAsync(CreateRequest(package, workspaceRoot, "gen", files), null, null, cts.Token);

        Assert.True(result.IsCancelled);
        Assert.Equal(0, result.Generated);
    }

    /// <summary>
    /// 构造生成服务与配置服务测试替身。
    /// </summary>
    /// <returns>生成服务与可断言的配置服务。</returns>
    private static (CodeGenerator Generator, FakeConfigService ConfigService) CreateGenerator()
    {
        var configService = new FakeConfigService();
        var generator = new CodeGenerator(
            new TemplateEngine(),
            new TemplateFileWriter(),
            new FileWriter(NullLogger<FileWriter>.Instance),
            configService,
            NullLogger<CodeGenerator>.Instance);
        return (generator, configService);
    }

    /// <summary>
    /// 在临时目录下创建含两个模板文件（entity.java 与 mapper.xml）的默认测试包。
    /// </summary>
    /// <returns>加载后的模板包信息与临时根目录。</returns>
    private Task<(TemplatePackageInfo Package, string TempRoot)> CreateDefaultPackageAsync()
    {
        return CreateTempPackageAsync(new[]
        {
            ("entity.java.scriban", "out/{{table.variableName}}.java", "public class {{table.className}} {}\n"),
            ("mapper.xml.scriban", "out/{{table.variableName}}.xml", "<mapper>{{table.className}}</mapper>\n")
        });
    }

    /// <summary>
    /// 在临时目录下按指定文件清单创建测试模板包并加载。
    /// </summary>
    /// <param name="files">模板相对路径、输出路径模板与模板内容三元组清单。</param>
    /// <returns>加载后的模板包信息与临时根目录。</returns>
    private async Task<(TemplatePackageInfo Package, string TempRoot)> CreateTempPackageAsync(
        IReadOnlyList<(string Template, string Output, string Content)> files)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
        _tempRoots.Add(tempRoot);
        string packageDir = Path.Combine(tempRoot, "user-pkg");
        Directory.CreateDirectory(packageDir);

        var manifest = new TemplateManifest
        {
            Name = "user-pkg",
            Description = "测试包",
            Engine = "scriban",
            BasePackage = "com.example",
            TypeMap = new Dictionary<string, string> { ["bigint"] = "Long" },
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
    /// 在临时根目录下创建工作区根与相对输出根对应的输出目录。
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
    /// 构造带默认勾选文件集合的生成请求。
    /// </summary>
    /// <param name="package">模板包信息。</param>
    /// <param name="workspaceRoot">工作区根。</param>
    /// <param name="relativeOutputRoot">相对输出根。</param>
    /// <param name="files">勾选模板文件集合。</param>
    /// <returns>生成请求。</returns>
    private static GenerationRequest CreateRequest(
        TemplatePackageInfo package,
        string workspaceRoot,
        string relativeOutputRoot,
        IReadOnlyList<TemplateFileSelection> files)
    {
        return new GenerationRequest(package, new[] { CreateTable() }, files, workspaceRoot, relativeOutputRoot);
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
            SchemaName = "shop",
            Comment = "系统用户表",
            ClassName = TableInfo.ToPascalCase("sys_user"),
            VariableName = TableInfo.ToCamelCase("sys_user")
        };
    }

    /// <summary>
    /// 同步进度回调封装，避免 Progress&lt;T&gt; 异步派发导致的测试竞态。
    /// </summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SyncProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value)
        {
            _handler(value);
        }
    }

    /// <summary>
    /// 配置服务测试替身，仅记录生成回写行为，其余配置能力不参与本测试。
    /// </summary>
    private sealed class FakeConfigService : IConfigService
    {
        /// <summary>
        /// 内存配置快照，生成回写直接作用于该实例。
        /// </summary>
        public AppConfig Current { get; } = new AppConfig();

        /// <summary>
        /// 保存调用次数，供断言生成完成后回写发生。
        /// </summary>
        public int SaveCount { get; private set; }

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
            SaveCount++;
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
    }
}
