using System.Reflection;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Generation;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;
using DbCodeGen.McpServer.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbCodeGen.Core.Tests.Mcp;

/// <summary>
/// MCP 能力面单元测试：preview_generation/generate 的 duplicateStrategy 缺省 skip、webDirectory/includePlatformTables
/// 入参贯通与非法值拒绝、请求组装透传，以及 list_template_packages/list_templates 的能力元数据与契约发现。
/// </summary>
public sealed class McpCapabilitySurfaceTests : IDisposable
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
    /// preview_generation 与 generate 的 duplicateStrategy 缺省值为 skip（含手写代码目录默认不覆盖）。
    /// </summary>
    [Fact]
    public void GenerationTools_DuplicateStrategy_DefaultsToSkip()
    {
        ParameterInfo previewParameter = typeof(PreviewTools)
            .GetMethod(nameof(PreviewTools.PreviewGenerationAsync))!
            .GetParameters()
            .Single(parameter => parameter.Name == "duplicateStrategy");
        ParameterInfo generateParameter = typeof(GenerationTools)
            .GetMethod(nameof(GenerationTools.GenerateAsync))!
            .GetParameters()
            .Single(parameter => parameter.Name == "duplicateStrategy");

        Assert.Equal(DuplicateFileStrategy.Skip, previewParameter.DefaultValue);
        Assert.Equal(DuplicateFileStrategy.Skip, generateParameter.DefaultValue);
    }

    /// <summary>
    /// generate 工具回执与 preview_generation 同源透出结构化诊断：平台表排除记录、命名报告与覆盖告警。
    /// </summary>
    [Fact]
    public async Task GenerationTools_Generate_ReturnsStructuredDiagnostics()
    {
        TemplatePackageInfo package = await CreatePackageAsync(
            """
            {
              "name": "generate-surface-pkg",
              "description": "生成回执测试包",
              "engine": "scriban",
              "basePackage": "com.example",
              "platformTables": { "prefixes": ["sys_"] },
              "files": [{ "template": "entity.java.scriban", "output": "out/{{table.className}}.java", "enabled": true }]
            }
            """,
            ("entity.java.scriban", "class {{table.className}} {}\n"));
        var packageService = new FakeTemplatePackageService(package);

        string workspaceRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"), "workspace");
        _tempRoots.Add(Path.GetDirectoryName(workspaceRoot)!);
        Directory.CreateDirectory(workspaceRoot);

        var generator = new CodeGenerator(
            new TemplateEngine(),
            new TemplateFileWriter(),
            new FileWriter(NullLogger<FileWriter>.Instance),
            CreateConfigService(workspaceRoot),
            NullLogger<CodeGenerator>.Instance);
        var tools = new GenerationTools(packageService, generator, NullLogger<GenerationTools>.Instance);

        object response = await tools.GenerateAsync(
            "generate-surface-pkg",
            workspaceRoot,
            "src/main/java/com/example",
            tables: new[]
            {
                new SyntheticTableInput { Name = "t_asset" },
                new SyntheticTableInput { Name = "sys_user" }
            },
            files: null,
            duplicateStrategy: DuplicateFileStrategy.Skip,
            variables: null,
            modules: null,
            webDirectory: null,
            includePlatformTables: false,
            cancellationToken: CancellationToken.None);

        GenerateResult result = Assert.IsType<GenerateResult>(response);
        Assert.Equal(1, result.Generated);
        PlatformTableExcludedItem exclusion = Assert.Single(result.PlatformTableExcluded);
        Assert.Equal("sys_user", exclusion.TableName);
        Assert.True(exclusion.Excluded);
        NamingReportEntry naming = Assert.Single(result.NamingReport);
        Assert.Equal("t_asset", naming.RawName);
        Assert.Contains(result.CoverageWarnings, warning => warning.Kind == CoverageWarningInfo.NoBackendView);
    }

    /// <summary>
    /// webDirectory 与 includePlatformTables 入参贯通到 GenerationRequest；非法 webDirectory 结构化拒绝。
    /// </summary>
    [Fact]
    public async Task GenerationRequestFactory_WebDirectoryAndPlatformSwitch_PassedThrough()
    {
        TemplatePackageInfo package = await CreatePackageAsync(
            """
            {
              "name": "surface-pkg",
              "description": "能力面测试包",
              "engine": "scriban",
              "basePackage": "com.example",
              "files": [{ "template": "entity.java.scriban", "output": "out/{{table.className}}.java", "enabled": true }]
            }
            """,
            ("entity.java.scriban", "class {{table.className}} {}\n"));
        var packageService = new FakeTemplatePackageService(package);

        GenerationRequest request = await GenerationRequestFactory.CreateAsync(
            packageService, "surface-pkg",
            new[] { new SyntheticTableInput { Name = "t_asset" } },
            files: null,
            workspaceRoot: @"C:\ws",
            codeDirectory: "src/main/java/com/example",
            basePackageOverride: null,
            duplicateStrategy: "skip",
            persistLastUsedPaths: false,
            variables: null,
            modules: null,
            CancellationToken.None,
            webDirectory: "frontend",
            includePlatformTables: true);

        Assert.Equal("frontend", request.WebDirectory);
        Assert.True(request.IncludePlatformTables);

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => GenerationRequestFactory.CreateAsync(
            packageService, "surface-pkg",
            new[] { new SyntheticTableInput { Name = "t_asset" } },
            files: null,
            workspaceRoot: @"C:\ws",
            codeDirectory: "src/main/java/com/example",
            basePackageOverride: null,
            duplicateStrategy: "skip",
            persistLastUsedPaths: false,
            variables: null,
            modules: null,
            CancellationToken.None,
            webDirectory: "../outside"));
        Assert.Contains("webDirectory", exception.Message);
    }

    /// <summary>
    /// list_template_packages 返回能力元数据与契约缺失告警；list_templates 标记契约条目；
    /// 契约路径支持 manifest 自定义（contractPath）且可经 read_template 读取。
    /// </summary>
    [Fact]
    public async Task TemplateQueryTools_ExposeCapabilitiesAndContractDiscovery()
    {
        TemplateQueryTools tools = await CreateToolsAsync(
            """
            {
              "name": "simple",
              "description": "能力面包",
              "engine": "scriban",
              "basePackage": "com.simple",
              "role": "backend",
              "stack": "java",
              "hasWebPage": false,
              "contractPath": "docs/contract.md.scriban",
              "deployModes": ["standalone", "cloud"],
              "presets": {
                "base": { "description": "基础启用集", "include": "enabled" }
              },
              "files": [
                { "template": "entity.java.scriban", "output": "src/main/java/{{package.dir}}/entity/{{table.className}}.java", "enabled": true },
                { "template": "view.java.scriban", "output": "src/main/java/{{package.dir}}/view/{{table.className}}View.java", "enabled": true }
              ]
            }
            """,
            contractRelativePath: "docs/contract.md.scriban",
            contractContent: "# 契约\n\nnaming：命名策略。\n");

        ListTemplatePackagesResult packages = await tools.ListTemplatePackagesAsync(CancellationToken.None);
        TemplatePackageSummary summary = Assert.Single(packages.Packages);
        Assert.Equal("backend", summary.Role);
        Assert.Equal("java", summary.Stack);
        Assert.Equal(new[] { "table" }, summary.Scopes);
        Assert.True(summary.HasBackendView);
        Assert.False(summary.HasWebPage);
        Assert.Equal("docs/contract.md.scriban", summary.ContractPath);
        Assert.Equal(new[] { "standalone", "cloud" }, summary.DeployModes);
        Assert.True(summary.HasContract);
        Assert.Empty(summary.Warnings);
        TemplatePresetSummary packagePreset = Assert.Single(summary.Presets);
        Assert.Equal(("base", 2), (packagePreset.Name, packagePreset.FileCount));

        ListTemplatesResult templates = await tools.ListTemplatesAsync("simple", null, null, CancellationToken.None);
        Assert.Equal(3, templates.Templates.Count);
        Assert.Equal("base", Assert.Single(templates.Presets).Name);
        TemplateEntry contractEntry = Assert.Single(templates.Templates, entry => entry.IsContract);
        Assert.Equal("docs/contract.md.scriban", contractEntry.Path);
        Assert.False(contractEntry.Enabled);
        Assert.Empty(templates.Warnings);

        object readResult = await tools.ReadTemplateAsync("simple", "docs/contract.md.scriban", CancellationToken.None);
        ReadTemplateResult contract = Assert.IsType<ReadTemplateResult>(readResult);
        Assert.Equal("docs/contract.md.scriban", contract.Path);
        Assert.Contains("命名策略", contract.Content);
        Assert.Equal("base", Assert.Single(contract.Presets).Name);
    }

    /// <summary>
    /// 包根无契约文档时 list_template_packages/list_templates 回执 MissingContract 告警（非阻断）。
    /// </summary>
    [Fact]
    public async Task TemplateQueryTools_MissingContract_NonBlockingWarning()
    {
        TemplateQueryTools tools = await CreateToolsAsync(
            """
            {
              "name": "simple",
              "description": "无契约包",
              "engine": "scriban",
              "basePackage": "com.simple",
              "files": [{ "template": "entity.java.scriban", "output": "out/{{table.className}}.java", "enabled": true }]
            }
            """,
            contractRelativePath: null,
            contractContent: null);

        ListTemplatePackagesResult packages = await tools.ListTemplatePackagesAsync(CancellationToken.None);
        TemplatePackageSummary summary = Assert.Single(packages.Packages);
        Assert.False(summary.HasContract);
        Assert.Contains(summary.Warnings, warning => warning.StartsWith("MissingContract", StringComparison.Ordinal));

        ListTemplatesResult templates = await tools.ListTemplatesAsync("simple", null, null, CancellationToken.None);
        Assert.Contains(templates.Warnings, warning => warning.StartsWith("MissingContract", StringComparison.Ordinal));
    }

    /// <summary>
    /// 创建测试工具组：临时目录内落一个 simple 用户包与可选契约文档。
    /// </summary>
    /// <param name="manifestJson">清单 JSON 文本。</param>
    /// <param name="contractRelativePath">契约文档相对路径；null 表示不落契约。</param>
    /// <param name="contractContent">契约正文。</param>
    /// <returns>只读查询工具组。</returns>
    private async Task<TemplateQueryTools> CreateToolsAsync(
        string manifestJson,
        string? contractRelativePath,
        string? contractContent)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
        _tempRoots.Add(tempRoot);
        string userRoot = Path.Combine(tempRoot, "user");
        string builtinRoot = Path.Combine(tempRoot, "builtin");
        string packageRoot = Path.Combine(userRoot, "simple");
        Directory.CreateDirectory(packageRoot);
        Directory.CreateDirectory(builtinRoot);
        await File.WriteAllTextAsync(Path.Combine(packageRoot, TemplatePackageLoader.ManifestFileName), manifestJson);
        await File.WriteAllTextAsync(Path.Combine(packageRoot, "entity.java.scriban"), "class {{table.className}} {}\n");
        await File.WriteAllTextAsync(Path.Combine(packageRoot, "view.java.scriban"), "class {{table.className}}View {}\n");

        if (contractRelativePath is not null && contractContent is not null)
        {
            string contractPath = Path.Combine(packageRoot, contractRelativePath.Replace('/', Path.DirectorySeparatorChar));
            string? directory = Path.GetDirectoryName(contractPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(contractPath, contractContent);
        }

        var packageService = new TemplatePackageService(
            CreateConfigService(userRoot),
            NullLogger<TemplatePackageService>.Instance,
            builtinRoot);
        return new TemplateQueryTools(packageService, new TemplateFileWriter(), NullLogger<TemplateQueryTools>.Instance);
    }

    /// <summary>
    /// 在临时目录创建测试模板包并加载。
    /// </summary>
    /// <param name="manifestJson">清单 JSON 文本。</param>
    /// <param name="files">模板相对路径与正文。</param>
    /// <returns>加载后的模板包信息。</returns>
    private async Task<TemplatePackageInfo> CreatePackageAsync(string manifestJson, params (string Template, string Content)[] files)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
        _tempRoots.Add(tempRoot);
        string packageRoot = Path.Combine(tempRoot, "pkg");
        Directory.CreateDirectory(packageRoot);
        await File.WriteAllTextAsync(Path.Combine(packageRoot, TemplatePackageLoader.ManifestFileName), manifestJson);
        foreach ((string template, string content) in files)
        {
            await File.WriteAllTextAsync(Path.Combine(packageRoot, template), content);
        }

        return await TemplatePackageLoader.LoadFromDirectoryAsync(packageRoot, isBuiltin: false, CancellationToken.None);
    }

    /// <summary>
    /// 构造配置服务测试替身：模板搜索目录指向指定用户模板目录。
    /// </summary>
    /// <param name="templateDirectory">用户模板目录。</param>
    /// <returns>配置服务替身。</returns>
    private static StubConfigService CreateConfigService(string templateDirectory)
    {
        var config = new AppConfig
        {
            TemplateSearchDirectories = new List<string> { templateDirectory }
        };
        return new StubConfigService { Current = config };
    }

    /// <summary>
    /// 模板包服务测试替身：固定返回同一包运行时信息。
    /// </summary>
    private sealed class FakeTemplatePackageService : ITemplatePackageService
    {
        private readonly TemplatePackageInfo _package;

        /// <summary>
        /// 使用固定包信息创建替身。
        /// </summary>
        /// <param name="package">固定包信息。</param>
        public FakeTemplatePackageService(TemplatePackageInfo package)
        {
            _package = package;
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<TemplatePackageInfo>> ListPackagesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<TemplatePackageInfo>>(new[] { _package });

        /// <inheritdoc />
        public Task<TemplatePackageInfo> LoadPackageAsync(string packageName, CancellationToken cancellationToken)
            => Task.FromResult(_package);

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> ImportFromZipAsync(string zipPath, bool overwrite, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> ImportFromFolderAsync(string folderPath, bool overwrite, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> CopyPackageAsync(string sourceName, string newName, bool overwrite, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<string> ExportToZipAsync(string packageName, string targetZipPath, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task DeletePackageAsync(string packageName, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> CreatePackageAsync(string packageName, string description, string? firstTemplatePath, string? firstOutputPath, CancellationToken cancellationToken, string? basePackage = null, IReadOnlyDictionary<string, string>? typeMap = null)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> AddTemplateFileAsync(string packageName, string templateRelativePath, string outputPath, CancellationToken cancellationToken, string? scope = null)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> DeleteTemplateFileAsync(string packageName, string templateRelativePath, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> AppendTemplateFilesAsync(string packageName, IReadOnlyList<TemplateFileWriteEntry> files, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> SaveTemplateContentAsync(string packageName, string templateRelativePath, string content, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> EditTemplateContentAsync(string packageName, string templateRelativePath, string oldText, string newText, string? expectedContentHash, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> UpdatePackageMetadataAsync(string packageName, string? description, string? basePackage, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? typeMap = null)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> RenamePackageAsync(string packageName, string newName, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> RenameTemplateFileAsync(string packageName, string templateRelativePath, string newRelativePath, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// 配置服务测试替身，仅承载内存配置快照供模板包服务读取搜索目录。
    /// </summary>
    private sealed class StubConfigService : IConfigService
    {
        /// <summary>
        /// 内存配置快照。
        /// </summary>
        public AppConfig Current { get; set; } = new();

        /// <inheritdoc />
        public string ConfigFilePath => "stub";

        /// <inheritdoc />
        public event EventHandler? ConfigChanged;

        /// <inheritdoc />
        public AppConfig Load() => Current;

        /// <inheritdoc />
        public void Save() => ConfigChanged?.Invoke(this, EventArgs.Empty);

        /// <inheritdoc />
        public GenerationDefaults GetGenerationDefaults() => new(string.Empty, string.Empty);

        /// <inheritdoc />
        public string? GetLlmApiKey() => null;

        /// <inheritdoc />
        public void ChangeDataDirectory(string dataDirectory) => throw new NotSupportedException();
    }
}
