using System.Text.Json;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Generation;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbCodeGen.Core.Tests.Generation;

/// <summary>
/// 批量生成服务单元测试，覆盖 dry-run 分类（新增/跳过/覆盖）、同名文件策略（覆盖/跳过）、行尾归一化、
/// 防目录穿越、渲染失败整单失败、内部重算 dry-run、进度报告与最近输出根回写。
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
    /// 渲染后相对路径越出工作区根应抛目录穿越异常并整单失败（.. 仅限工作区根内）。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_PathTraversalEscapesWorkspace_Throws()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateTempPackageAsync(new[]
        {
            ("entity.java.scriban", "../../../../evil.java", "public class {{table.className}} {}")
        });
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "../../../../evil.java", true) };
        (CodeGenerator generator, _) = CreateGenerator();

        var exception = await Assert.ThrowsAsync<GenerationException>(
            () => generator.BuildPreviewAsync(CreateRequest(package, workspaceRoot, "gen", files), null, CancellationToken.None));

        Assert.Contains("目录穿越", exception.Message);
    }

    /// <summary>
    /// 输出路径含 .. 段应从相对输出根（如代码根 src/main/java）越级到工作区根内其它目录
    /// （如 src/main/resources/mapper），放行且绝对路径正确解析。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_OutputDotDotEscapesCodeRoot_ResolvesWithinWorkspace()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateTempPackageAsync(new[]
        {
            ("mapper.xml.scriban", "../resources/mapper/{{table.className}}Dao.xml", "<mapper>{{table.className}}</mapper>\n")
        });
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("mapper.xml.scriban", "../resources/mapper/{{table.className}}Dao.xml", true) };
        (CodeGenerator generator, _) = CreateGenerator();

        GenerationPreview preview = await generator.BuildPreviewAsync(
            new GenerationRequest(package, new[] { CreateTable() }, files, workspaceRoot, "src/main/java"),
            null,
            CancellationToken.None);

        GenerationFileEntry entry = Assert.Single(preview.Entries);
        Assert.Equal("../resources/mapper/SysUserDao.xml", entry.RelativePath);
        string expected = Path.Combine(workspaceRoot, "src", "main", "resources", "mapper", "SysUserDao.xml");
        Assert.Equal(expected, entry.AbsolutePath);
        Assert.Equal(GenerationAction.New, entry.Action);
    }

    /// <summary>
    /// 输出路径模板渲染结果为空（如 {{package.dir}} 占位且基础包名为空）应在预览期抛目录穿越异常，
    /// 不落入写盘期以目录路径为目标的误导性写盘失败。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_EmptyRenderedOutput_Throws()
    {
        // 构造基础包名为空、输出路径仅为 {{package.dir}} 占位的模板包，渲染后输出路径为空串
        string tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
        _tempRoots.Add(tempRoot);
        string packageDir = Path.Combine(tempRoot, "user-pkg");
        Directory.CreateDirectory(packageDir);
        var manifest = new TemplateManifest
        {
            Name = "user-pkg",
            Description = "测试包",
            Engine = "scriban",
            BasePackage = null,
            TypeMap = new Dictionary<string, string>(),
            Files = new List<TemplateFileEntry>
            {
                new() { Template = "t.java.scriban", Output = "{{package.dir}}", Enabled = true }
            }
        };
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        await File.WriteAllTextAsync(Path.Combine(packageDir, TemplatePackageLoader.ManifestFileName), JsonSerializer.Serialize(manifest, jsonOptions));
        await File.WriteAllTextAsync(Path.Combine(packageDir, "t.java.scriban"), "class {{table.className}} {}");
        TemplatePackageInfo package = await TemplatePackageLoader.LoadFromDirectoryAsync(packageDir, isBuiltin: false, CancellationToken.None);
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("t.java.scriban", "{{package.dir}}", true) };
        (CodeGenerator generator, _) = CreateGenerator();

        var exception = await Assert.ThrowsAsync<GenerationException>(
            () => generator.BuildPreviewAsync(CreateRequest(package, workspaceRoot, "gen", files), null, CancellationToken.None));

        Assert.Contains("渲染结果为空", exception.Message);
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

        GenerationResult result = await generator.GenerateAsync(CreateRequest(package, workspaceRoot, "gen", files), null, CancellationToken.None);

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
    /// 默认覆盖策略下内容不同的既有文件应无确认弹窗直接覆盖写盘，计数为覆盖。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_OverwriteStrategy_OverwritesExisting()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateDefaultPackageAsync();
        (string workspaceRoot, string outputRoot) = CreateWorkspace(tempRoot);
        string target = Path.Combine(outputRoot, "out", "sysUser.java");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "OLD CONTENT");
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out/{{table.variableName}}.java", true) };
        (CodeGenerator generator, _) = CreateGenerator();

        GenerationResult result = await generator.GenerateAsync(CreateRequest(package, workspaceRoot, "gen", files), null, CancellationToken.None);

        Assert.Equal(1, result.Overwritten);
        Assert.Equal(0, result.Generated);
        Assert.False(result.IsCancelled);
        Assert.Equal("public class SysUser {}\n", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// 生成请求默认（不设置回写开关）应保持桌面端既有行为：成功后回写工作区根与最近输出根并保存。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_PersistLastUsedPathsByDefault_WritesBackMemory()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateDefaultPackageAsync();
        (string workspaceRoot, string outputRoot) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out/{{table.variableName}}.java", true) };
        (CodeGenerator generator, FakeConfigService configService) = CreateGenerator();

        GenerationResult result = await generator.GenerateAsync(
            CreateRequest(package, workspaceRoot, "gen", files), null, CancellationToken.None);

        Assert.False(result.IsCancelled);
        Assert.Equal(workspaceRoot, configService.Current.WorkspaceRoot);
        Assert.Equal("gen", configService.Current.LastRelativeOutputRoot);
        Assert.True(configService.SaveCount >= 1);
    }

    /// <summary>
    /// 生成请求显式关闭记忆回写开关时，成功生成后不应触碰配置（不回写最近路径、不触发保存），
    /// 供 MCP 等无记忆态调用方实现零配置副作用。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_PersistLastUsedPathsDisabled_SkipsMemoryWriteBack()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateDefaultPackageAsync();
        (string workspaceRoot, string outputRoot) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out/{{table.variableName}}.java", true) };
        (CodeGenerator generator, FakeConfigService configService) = CreateGenerator();
        configService.Current.WorkspaceRoot = "preserved-root";
        configService.Current.LastRelativeOutputRoot = "preserved-output";

        GenerationRequest request = CreateRequest(
            package, workspaceRoot, "gen", files, persistLastUsedPaths: false);
        GenerationResult result = await generator.GenerateAsync(request, null, CancellationToken.None);

        // 写盘照常完成，但配置记忆保持原值且零保存调用
        Assert.False(result.IsCancelled);
        Assert.Equal(1, result.Generated);
        Assert.True(File.Exists(Path.Combine(outputRoot, "out", "sysUser.java")));
        Assert.Equal("preserved-root", configService.Current.WorkspaceRoot);
        Assert.Equal("preserved-output", configService.Current.LastRelativeOutputRoot);
        Assert.Equal(0, configService.SaveCount);
    }

    /// <summary>
    /// 跳过策略下内容不同的既有文件应分类为跳过，dry-run 只保留新增与跳过计数。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_SkipStrategy_ExistingDifferentClassifiesSkip()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateDefaultPackageAsync();
        (string workspaceRoot, string outputRoot) = CreateWorkspace(tempRoot);
        string target = Path.Combine(outputRoot, "out", "sysUser.java");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "OLD CONTENT");
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out/{{table.variableName}}.java", true) };
        (CodeGenerator generator, _) = CreateGenerator();

        GenerationPreview preview = await generator.BuildPreviewAsync(
            CreateRequest(package, workspaceRoot, "gen", files, DuplicateFileStrategy.Skip),
            null,
            CancellationToken.None);

        Assert.Equal(1, preview.SkipCount);
        Assert.Equal(0, preview.OverwriteCount);
        Assert.Equal(0, preview.NewCount);
        Assert.Equal(GenerationAction.Skip, Assert.Single(preview.Entries).Action);
    }

    /// <summary>
    /// 跳过策略下生成应只写新增文件，内容不同的既有文件原样保留不被覆盖。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_SkipStrategy_ExistingUntouchedOnlyNewWritten()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateDefaultPackageAsync();
        (string workspaceRoot, string outputRoot) = CreateWorkspace(tempRoot);
        string existingTarget = Path.Combine(outputRoot, "out", "sysUser.java");
        Directory.CreateDirectory(Path.GetDirectoryName(existingTarget)!);
        await File.WriteAllTextAsync(existingTarget, "OLD CONTENT");
        var files = new[]
        {
            new TemplateFileSelection("entity.java.scriban", "out/{{table.variableName}}.java", true),
            new TemplateFileSelection("mapper.xml.scriban", "out/{{table.variableName}}.xml", true)
        };
        (CodeGenerator generator, _) = CreateGenerator();

        GenerationResult result = await generator.GenerateAsync(
            CreateRequest(package, workspaceRoot, "gen", files, DuplicateFileStrategy.Skip),
            null,
            CancellationToken.None);

        Assert.Equal(1, result.Generated);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Overwritten);
        Assert.False(result.IsCancelled);
        Assert.Equal("OLD CONTENT", await File.ReadAllTextAsync(existingTarget));
        Assert.True(File.Exists(Path.Combine(outputRoot, "out", "sysUser.xml")));
    }

    /// <summary>
    /// 覆盖策略下内容相同的既有文件应分类为跳过，不做无意义写盘。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_OverwriteStrategy_ExistingSameContentClassifiesSkip()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateDefaultPackageAsync();
        (string workspaceRoot, string outputRoot) = CreateWorkspace(tempRoot);
        string target = Path.Combine(outputRoot, "out", "sysUser.java");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "public class SysUser {}\n");
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out/{{table.variableName}}.java", true) };
        (CodeGenerator generator, _) = CreateGenerator();

        GenerationPreview preview = await generator.BuildPreviewAsync(CreateRequest(package, workspaceRoot, "gen", files), null, CancellationToken.None);

        Assert.Equal(1, preview.SkipCount);
        Assert.Equal(0, preview.OverwriteCount);
        Assert.Equal(GenerationAction.Skip, Assert.Single(preview.Entries).Action);
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

        GenerationResult result = await generator.GenerateAsync(CreateRequest(package, workspaceRoot, "gen", files), null, CancellationToken.None);

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

        await generator.GenerateAsync(CreateRequest(package, workspaceRoot, "gen", files), progress, CancellationToken.None);

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

        GenerationResult result = await generator.GenerateAsync(CreateRequest(package, workspaceRoot, "gen", files), null, cts.Token);

        Assert.True(result.IsCancelled);
        Assert.Equal(0, result.Generated);
    }

    /// <summary>
    /// 三作用域混跑应产出正确文件计划：table 组按表展开、module 组按模块清单展开、project 组单文件，
    /// 同次调用全部产出且条目归属正确。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_MixedScopes_ProducesAllScopeEntries()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateScopedPackageAsync();
        (string workspaceRoot, string outputRoot) = CreateWorkspace(tempRoot);
        var files = package.Files.Select(file => new TemplateFileSelection(file.RelativeTemplatePath, file.OutputPath, true, file.Scope)).ToList();
        (CodeGenerator generator, _) = CreateGenerator();
        GenerationRequest request = CreateRequest(package, workspaceRoot, "gen", files);
        var modules = new[] { new ModuleContext("account"), new ModuleContext("billing") };
        request = WithModules(request, variables: new Dictionary<string, object?> { ["appName"] = "demo" }, modules: modules);

        GenerationPreview preview = await generator.BuildPreviewAsync(request, null, CancellationToken.None);

        // table 组：1 表 × 1 模板；module 组：2 模块 × 1 模板；project 组：1 模板
        Assert.Equal(4, preview.Entries.Count);
        Assert.Equal(4, preview.NewCount);

        GenerationFileEntry tableEntry = Assert.Single(preview.Entries, entry => entry.RelativePath == "out/sysUser.java");
        Assert.Equal("sys_user", tableEntry.TableName);

        List<GenerationFileEntry> moduleEntries = preview.Entries.Where(entry => entry.RelativePath.Contains("Modules/") && entry.RelativePath.EndsWith("Info.cs", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, moduleEntries.Count);
        Assert.Contains(moduleEntries, entry => entry.TableName == "account" && entry.RelativePath == "gen/Modules/account/Info.cs");
        Assert.Contains(moduleEntries, entry => entry.TableName == "billing" && entry.RelativePath == "gen/Modules/billing/Info.cs");

        GenerationFileEntry projectEntry = Assert.Single(preview.Entries, entry => entry.RelativePath == "AppDbContext.cs");
        Assert.Equal(string.Empty, projectEntry.TableName);
        Assert.Equal(Path.Combine(workspaceRoot, "AppDbContext.cs"), projectEntry.AbsolutePath);
    }

    /// <summary>
    /// 三作用域混跑 generate 应按路径基准写盘：table/module 落输出根下，project 落工作区根下，
    /// module 模板的 vars 与 tables 循环渲染进文件内容。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_MixedScopes_WritesFilesByPathBaseline()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateScopedPackageAsync();
        (string workspaceRoot, string outputRoot) = CreateWorkspace(tempRoot);
        var files = package.Files.Select(file => new TemplateFileSelection(file.RelativeTemplatePath, file.OutputPath, true, file.Scope)).ToList();
        (CodeGenerator generator, _) = CreateGenerator();
        var request = WithModules(
            CreateRequest(package, workspaceRoot, "gen", files),
            variables: new Dictionary<string, object?> { ["appName"] = "demo" },
            modules: new[] { new ModuleContext("account") });

        GenerationResult result = await generator.GenerateAsync(request, null, CancellationToken.None);

        Assert.Equal(0, result.Failed);
        Assert.False(result.IsCancelled);
        // table 组 1 + module 组 1 + project 组 1
        Assert.Equal(3, result.Generated);

        // table/module 相对输出根；project 相对工作区根
        Assert.True(File.Exists(Path.Combine(outputRoot, "out", "sysUser.java")));
        Assert.True(File.Exists(Path.Combine(outputRoot, "gen", "Modules", "account", "Info.cs")));
        Assert.True(File.Exists(Path.Combine(workspaceRoot, "AppDbContext.cs")));

        // project 模板的 vars 注入与 tables 循环（2 张表：默认 sys_user + module 场景同一表集合）渲染核对
        string contextContent = File.ReadAllText(Path.Combine(workspaceRoot, "AppDbContext.cs"));
        Assert.Contains("app=demo", contextContent);
        Assert.Contains("DbSet<SysUser>", contextContent);

        // module 模板的 module.* 与 vars.* 渲染核对
        string moduleContent = File.ReadAllText(Path.Combine(outputRoot, "gen", "Modules", "account", "Info.cs"));
        Assert.Contains("module=account app=demo", moduleContent);
    }

    /// <summary>
    /// module 模板应可循环 tables 全表汇总（元素字段与 table.* 同构），多表时逐表展开。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_TablesLoopInModuleTemplate_RendersPerTable()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateTempPackageAsync(new[]
        {
            ("module-glue.cs.scriban", "Modules/{{module.name}}/Glue.cs", "{{ for t in tables }}entity:{{ t.variableName }};{{ end }}\n")
        }, moduleScope: true);
        (string workspaceRoot, string outputRoot) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("module-glue.cs.scriban", "Modules/{{module.name}}/Glue.cs", true, TemplateScope.Module) };
        (CodeGenerator generator, _) = CreateGenerator();
        var tableA = CreateTable();
        var tableB = new TableInfo
        {
            RawName = "sys_order",
            ClassName = TableInfo.ToPascalCase("sys_order"),
            VariableName = TableInfo.ToCamelCase("sys_order")
        };
        GenerationRequest request = CreateRequest(package, workspaceRoot, "gen", files);
        request = WithTablesAndModules(request, new[] { tableA, tableB }, modules: new[] { new ModuleContext("account") });

        GenerationPreview preview = await generator.BuildPreviewAsync(request, null, CancellationToken.None);

        GenerationFileEntry entry = Assert.Single(preview.Entries);
        Assert.Equal("account", entry.TableName);
        Assert.Contains("entity:sysUser;entity:sysOrder;", entry.Content);
    }

    /// <summary>
    /// 勾选 module 作用域模板但未传 modules 清单时应结构化报错并整单失败。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_ModuleScopeWithoutModules_Throws()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateTempPackageAsync(new[]
        {
            ("module-info.cs.scriban", "Modules/{{module.name}}/Info.cs", "module {{module.name}}\n")
        }, moduleScope: true);
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("module-info.cs.scriban", "Modules/{{module.name}}/Info.cs", true, TemplateScope.Module) };
        (CodeGenerator generator, _) = CreateGenerator();

        GenerationException exception = await Assert.ThrowsAsync<GenerationException>(
            () => generator.BuildPreviewAsync(CreateRequest(package, workspaceRoot, "gen", files), null, CancellationToken.None));

        Assert.Contains("modules", exception.Message);
    }

    /// <summary>
    /// modules 清单存在重复模块名时应结构化拒绝。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_DuplicateModuleNames_Throws()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateTempPackageAsync(new[]
        {
            ("module-info.cs.scriban", "Modules/{{module.name}}/Info.cs", "module {{module.name}}\n")
        }, moduleScope: true);
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("module-info.cs.scriban", "Modules/{{module.name}}/Info.cs", true, TemplateScope.Module) };
        (CodeGenerator generator, _) = CreateGenerator();
        GenerationRequest request = CreateRequest(package, workspaceRoot, "gen", files);
        request = WithModules(request, variables: null, modules: new[] { new ModuleContext("account"), new ModuleContext("account") });

        GenerationException exception = await Assert.ThrowsAsync<GenerationException>(
            () => generator.BuildPreviewAsync(request, null, CancellationToken.None));

        Assert.Contains("重复模块名", exception.Message);
    }

    /// <summary>
    /// variables 键与保留命名空间冲突时生成编排应结构化拒绝。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_VariablesConflictReservedNamespace_Throws()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateDefaultPackageAsync();
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("entity.java.scriban", "out/{{table.variableName}}.java", true) };
        (CodeGenerator generator, _) = CreateGenerator();
        GenerationRequest request = CreateRequest(package, workspaceRoot, "gen", files);
        request = WithModules(request, variables: new Dictionary<string, object?> { ["table"] = "bad" }, modules: null);

        GenerationException exception = await Assert.ThrowsAsync<GenerationException>(
            () => generator.BuildPreviewAsync(request, null, CancellationToken.None));

        Assert.Contains("table", exception.Message);
    }

    /// <summary>
    /// project 作用域输出路径相对工作区根解析（B 路径基准），渲染后路径允许子目录层级。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_ProjectScopeOutputRelativeWorkspaceRoot_ResolvesBaseline()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateTempPackageAsync(new[]
        {
            ("app-context.cs.scriban", "src/Data/AppDbContext.cs", "context {{vars.appName}}\n")
        }, projectScope: true);
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("app-context.cs.scriban", "src/Data/AppDbContext.cs", true, TemplateScope.Project) };
        (CodeGenerator generator, _) = CreateGenerator();
        GenerationRequest request = CreateRequest(package, workspaceRoot, "gen", files);
        request = WithModules(request, variables: new Dictionary<string, object?> { ["appName"] = "demo" }, modules: null);

        GenerationPreview preview = await generator.BuildPreviewAsync(request, null, CancellationToken.None);

        GenerationFileEntry entry = Assert.Single(preview.Entries);
        // project 条目相对工作区根（不拼相对输出根 gen）
        Assert.Equal(Path.Combine(workspaceRoot, "src", "Data", "AppDbContext.cs"), entry.AbsolutePath);
        Assert.Contains("context demo", entry.Content);
    }

    /// <summary>
    /// project 作用域渲染后路径越出工作区根应拒绝（防目录穿越不放松）。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_ProjectScopePathEscapesWorkspace_Throws()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateTempPackageAsync(new[]
        {
            ("evil.cs.scriban", "../../evil.cs", "evil\n")
        }, projectScope: true);
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("evil.cs.scriban", "../../evil.cs", true, TemplateScope.Project) };
        (CodeGenerator generator, _) = CreateGenerator();
        GenerationRequest request = CreateRequest(package, workspaceRoot, "gen", files);
        request = WithModules(request, variables: null, modules: null);

        await Assert.ThrowsAsync<GenerationException>(
            () => generator.BuildPreviewAsync(request, null, CancellationToken.None));
    }

    /// <summary>
    /// 纯 project 作用域生成且未传任何表时应正常产出工程条目（表仅作为 tables 汇总注入，空集合合法），
    /// 支撑 MCP 面纯 module/project 作用域包省略 tables 的分型语义。
    /// </summary>
    [Fact]
    public async Task BuildPreviewAsync_ProjectScopeOnlyWithEmptyTables_ProducesProjectEntries()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateTempPackageAsync(new[]
        {
            ("app-context.cs.scriban", "src/Data/AppDbContext.cs", "context {{vars.appName}}\n")
        }, projectScope: true);
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("app-context.cs.scriban", "src/Data/AppDbContext.cs", true, TemplateScope.Project) };
        (CodeGenerator generator, _) = CreateGenerator();
        GenerationRequest request = new GenerationRequest(
            package, Array.Empty<TableInfo>(), files, workspaceRoot, "gen");
        request = WithModules(request, variables: new Dictionary<string, object?> { ["appName"] = "demo" }, modules: null);

        GenerationPreview preview = await generator.BuildPreviewAsync(request, null, CancellationToken.None);

        GenerationFileEntry entry = Assert.Single(preview.Entries);
        Assert.Equal(string.Empty, entry.TableName);
        Assert.Equal(Path.Combine(workspaceRoot, "src", "Data", "AppDbContext.cs"), entry.AbsolutePath);
    }

    /// <summary>
    /// project 模板循环 modules 清单渲染的聚合段应与 module 作用域按同一清单产出的目录名逐字一致
    /// （同一数据源同值，目录名与聚合段大小写天然解耦）。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_ProjectScopeModulesLoop_MatchesModuleDirectoryNames()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
        _tempRoots.Add(tempRoot);
        string packageDir = Path.Combine(tempRoot, "user-pkg");
        Directory.CreateDirectory(packageDir);
        await File.WriteAllTextAsync(Path.Combine(packageDir, "module-info.cs.scriban"), "module={{module.name}}\n");
        await File.WriteAllTextAsync(Path.Combine(packageDir, "project-aggregate.cs.scriban"), "{{ for m in modules }}module={{ m.name }};{{ end }}\n");
        await File.WriteAllTextAsync(
            Path.Combine(packageDir, TemplatePackageLoader.ManifestFileName),
            """
            {
              "name": "user-pkg",
              "description": "mixed scope test package",
              "engine": "scriban",
              "basePackage": "com.example",
              "typeMap": { "bigint": "Long" },
              "files": [
                { "template": "module-info.cs.scriban", "output": "gen/Modules/{{module.name}}/Info.cs", "scope": "module" },
                { "template": "project-aggregate.cs.scriban", "output": "aggregate.cs", "scope": "project" }
              ]
            }
            """);
        TemplatePackageInfo package = await TemplatePackageLoader.LoadFromDirectoryAsync(packageDir, isBuiltin: false, CancellationToken.None);
        (string workspaceRoot, string outputRoot) = CreateWorkspace(tempRoot);
        var files = package.Files.Select(file => new TemplateFileSelection(file.RelativeTemplatePath, file.OutputPath, true, file.Scope)).ToList();
        (CodeGenerator generator, _) = CreateGenerator();
        var request = WithModules(
            CreateRequest(package, workspaceRoot, "gen", files),
            variables: null,
            modules: new[] { new ModuleContext("asset"), new ModuleContext("billing") });

        GenerationResult result = await generator.GenerateAsync(request, null, CancellationToken.None);

        Assert.Equal(0, result.Failed);

        // module 作用域按清单产出目录：gen/Modules/asset/Info.cs 与 gen/Modules/billing/Info.cs
        Assert.True(File.Exists(Path.Combine(outputRoot, "gen", "Modules", "asset", "Info.cs")));
        Assert.True(File.Exists(Path.Combine(outputRoot, "gen", "Modules", "billing", "Info.cs")));

        // project 模板循环注入的 modules 清单，聚合段与目录名逐字一致
        string projectContent = File.ReadAllText(Path.Combine(workspaceRoot, "aggregate.cs"));
        Assert.Equal("module=asset;module=billing;", projectContent.TrimEnd('\r', '\n'));
    }

    /// <summary>
    /// 仅勾选 project 作用域模板且未传 modules 时应走模板空值守卫分支（配置缺失注释在场，不抛运行期错误、不产聚合条目）。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_ProjectScopeOnlyWithoutModules_RendersGuardBranch()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateTempPackageAsync(new[]
        {
            ("project-aggregate.cs.scriban", "aggregate.cs", "{{ if modules == null }}<!-- modules missing -->{{ else }}{{ for m in modules }}{{ m.name }}{{ end }}{{ end }}\n")
        }, projectScope: true);
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("project-aggregate.cs.scriban", "aggregate.cs", true, TemplateScope.Project) };
        (CodeGenerator generator, _) = CreateGenerator();
        GenerationRequest request = WithModules(
            CreateRequest(package, workspaceRoot, "gen", files),
            variables: null,
            modules: null);

        GenerationResult result = await generator.GenerateAsync(request, null, CancellationToken.None);

        Assert.Equal(0, result.Failed);
        string content = File.ReadAllText(Path.Combine(workspaceRoot, "aggregate.cs"));
        Assert.Contains("<!-- modules missing -->", content);
    }

    /// <summary>
    /// project 作用域注入 modules 的元素名应原样保留大小写：modules=["Asset"] 时聚合段与 module 作用域目录
    /// 同取 "Asset"，大小写由唯一数据源决定。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_ProjectScopeModulesCasePreserved()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateTempPackageAsync(new[]
        {
            ("project-aggregate.cs.scriban", "aggregate.cs", "{{ for m in modules }}module={{ m.name }};{{ end }}\n")
        }, projectScope: true);
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("project-aggregate.cs.scriban", "aggregate.cs", true, TemplateScope.Project) };
        (CodeGenerator generator, _) = CreateGenerator();
        var request = WithModules(
            CreateRequest(package, workspaceRoot, "gen", files),
            variables: null,
            modules: new[] { new ModuleContext("Asset") });

        GenerationResult result = await generator.GenerateAsync(request, null, CancellationToken.None);

        Assert.Equal(0, result.Failed);
        string projectContent = File.ReadAllText(Path.Combine(workspaceRoot, "aggregate.cs"));
        Assert.Contains("module=Asset;", projectContent);
    }

    /// <summary>
    /// project 作用域同时显式传 variables.modules 与 modules 时以注入 modules 为准：
    /// 聚合循环渲染注入清单，显式镜像键仅在 vars 命名空间内可见、不遮蔽注入源。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_ProjectScope_ExplicitVarsModules_InjectedModulesWin()
    {
        (TemplatePackageInfo package, string tempRoot) = await CreateTempPackageAsync(new[]
        {
            ("project-aggregate.cs.scriban", "aggregate.cs", "{{ for m in modules }}{{ m.name }};{{ end }}\n")
        }, projectScope: true);
        (string workspaceRoot, _) = CreateWorkspace(tempRoot);
        var files = new[] { new TemplateFileSelection("project-aggregate.cs.scriban", "aggregate.cs", true, TemplateScope.Project) };
        (CodeGenerator generator, _) = CreateGenerator();
        GenerationRequest request = WithModules(
            CreateRequest(package, workspaceRoot, "gen", files),
            variables: new Dictionary<string, object?> { ["modules"] = new[] { new Dictionary<string, object?> { ["name"] = "ghost" } } },
            modules: new[] { new ModuleContext("real") });

        GenerationResult result = await generator.GenerateAsync(request, null, CancellationToken.None);

        Assert.Equal(0, result.Failed);
        string content = File.ReadAllText(Path.Combine(workspaceRoot, "aggregate.cs"));
        Assert.Contains("real;", content);
        Assert.DoesNotContain("ghost", content);
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
    /// <param name="moduleScope">清单内全部文件声明为 module 作用域。</param>
    /// <param name="projectScope">清单内全部文件声明为 project 作用域（与 moduleScope 互斥使用）。</param>
    /// <returns>加载后的模板包信息与临时根目录。</returns>
    private async Task<(TemplatePackageInfo Package, string TempRoot)> CreateTempPackageAsync(
        IReadOnlyList<(string Template, string Output, string Content)> files,
        bool moduleScope = false,
        bool projectScope = false)
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
                Scope = moduleScope ? "module" : projectScope ? "project" : null,
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
    /// 创建三作用域混跑测试包：table 模板 1 个、module 模板 1 个、project 模板 1 个，
    /// 输出路径覆盖 table（相对输出根）、module（相对输出根含 module.name）、project（相对工作区根）三种基准。
    /// </summary>
    /// <returns>加载后的模板包信息与临时根目录。</returns>
    private async Task<(TemplatePackageInfo Package, string TempRoot)> CreateScopedPackageAsync()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
        _tempRoots.Add(tempRoot);
        string packageDir = Path.Combine(tempRoot, "user-pkg");
        Directory.CreateDirectory(packageDir);
        await File.WriteAllTextAsync(Path.Combine(packageDir, "entity.java.scriban"), "public class {{table.className}} {}\n");
        await File.WriteAllTextAsync(Path.Combine(packageDir, "module-info.cs.scriban"), "module={{module.name}} app={{vars.appName}}\n");
        await File.WriteAllTextAsync(Path.Combine(packageDir, "app-context.cs.scriban"), "app={{vars.appName}}\n{{ for t in tables }}DbSet<{{ t.className }}>{{ end }}\n");
        await File.WriteAllTextAsync(
            Path.Combine(packageDir, TemplatePackageLoader.ManifestFileName),
            """
            {
              "name": "user-pkg",
              "description": "scoped test package",
              "engine": "scriban",
              "basePackage": "com.example",
              "typeMap": { "bigint": "Long" },
              "files": [
                { "template": "entity.java.scriban", "output": "out/{{table.variableName}}.java" },
                { "template": "module-info.cs.scriban", "output": "gen/Modules/{{module.name}}/Info.cs", "scope": "module" },
                { "template": "app-context.cs.scriban", "output": "AppDbContext.cs", "scope": "project" }
              ]
            }
            """);

        TemplatePackageInfo package = await TemplatePackageLoader.LoadFromDirectoryAsync(packageDir, isBuiltin: false, CancellationToken.None);
        return (package, tempRoot);
    }

    /// <summary>
    /// 以变量与模块清单重建生成请求（请求模型不可变，按原字段拷贝重建）。
    /// </summary>
    /// <param name="request">原始生成请求。</param>
    /// <param name="variables">调用方变量集合，可为空。</param>
    /// <param name="modules">模块清单，可为空。</param>
    /// <returns>携带变量与模块清单的新请求。</returns>
    private static GenerationRequest WithModules(
        GenerationRequest request,
        IReadOnlyDictionary<string, object?>? variables,
        IReadOnlyList<ModuleContext>? modules)
    {
        return new GenerationRequest(
            request.Package,
            request.Tables,
            request.SelectedFiles,
            request.WorkspaceRoot,
            request.RelativeOutputRoot,
            request.BasePackageOverride,
            request.DataSource,
            request.CodeDirectory,
            request.DuplicateFileStrategy,
            request.PersistLastUsedPaths,
            variables,
            modules);
    }

    /// <summary>
    /// 以表集合与模块清单重建生成请求，供多表循环渲染测试使用。
    /// </summary>
    /// <param name="request">原始生成请求。</param>
    /// <param name="tables">替换后的表集合。</param>
    /// <param name="modules">模块清单。</param>
    /// <returns>携带新表集合与模块清单的新请求。</returns>
    private static GenerationRequest WithTablesAndModules(
        GenerationRequest request,
        IReadOnlyList<TableInfo> tables,
        IReadOnlyList<ModuleContext>? modules)
    {
        return new GenerationRequest(
            request.Package,
            tables,
            request.SelectedFiles,
            request.WorkspaceRoot,
            request.RelativeOutputRoot,
            request.BasePackageOverride,
            request.DataSource,
            request.CodeDirectory,
            request.DuplicateFileStrategy,
            request.PersistLastUsedPaths,
            request.Variables,
            modules);
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
    /// <param name="duplicateFileStrategy">同名文件处理策略，默认覆盖。</param>
    /// <param name="persistLastUsedPaths">生成后是否回写最近生成路径记忆，默认 true 与桌面端一致。</param>
    /// <returns>生成请求。</returns>
    private static GenerationRequest CreateRequest(
        TemplatePackageInfo package,
        string workspaceRoot,
        string relativeOutputRoot,
        IReadOnlyList<TemplateFileSelection> files,
        DuplicateFileStrategy duplicateFileStrategy = DuplicateFileStrategy.Overwrite,
        bool persistLastUsedPaths = true)
    {
        return new GenerationRequest(
            package, new[] { CreateTable() }, files, workspaceRoot, relativeOutputRoot,
            duplicateFileStrategy: duplicateFileStrategy,
            persistLastUsedPaths: persistLastUsedPaths);
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

        /// <inheritdoc />
        public void ChangeDataDirectory(string dataDirectory)
        {
            throw new NotSupportedException("测试替身不支持切换数据目录。");
        }
    }
}
