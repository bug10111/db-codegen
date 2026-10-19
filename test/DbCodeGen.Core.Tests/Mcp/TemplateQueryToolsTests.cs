using DbCodeGen.Core.Config;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;
using DbCodeGen.McpServer.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace DbCodeGen.Core.Tests.Mcp;

/// <summary>
/// 模板只读查询工具单元测试：包根 README.md.scriban 窄例外（未在 template.json files[] 声明仍可读、
/// 文档型元数据、防穿越拒绝面）、list_templates 合成文档型条目与过滤约束、README 不进入文件清单与生成选择。
/// </summary>
public sealed class TemplateQueryToolsTests : IDisposable
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
    /// 包根 README 未在清单声明时 read_template 仍可读取：返回正文、contentHash 与文档型元数据
    /// （output 空串、enabled=false、language=Plain），且大小写不敏感命中。
    /// </summary>
    [Fact]
    public async Task ReadTemplateAsync_UndeclaredRootReadme_ReturnsDocumentMetadata()
    {
        const string readmeContent = "# 传参契约\n\nmodules：业务模块清单。\n";
        TemplateQueryTools tools = await CreateToolsAsync(withRootReadme: true);

        object result = await tools.ReadTemplateAsync("simple", "README.md.scriban", CancellationToken.None);

        ReadTemplateResult dto = Assert.IsType<ReadTemplateResult>(result);
        Assert.Equal("simple", dto.Package);
        Assert.Equal("README.md.scriban", dto.Path);
        Assert.Equal(string.Empty, dto.Output);
        Assert.False(dto.Enabled);
        Assert.Equal("Plain", dto.Language);
        Assert.Equal(readmeContent, dto.Content);
        Assert.Equal(TemplateContentHash.Compute(readmeContent), dto.ContentHash);

        // 大小写不敏感：同一窄例外命中，返回规范化路径
        object lowerCaseResult = await tools.ReadTemplateAsync("simple", "readme.MD.scriban", CancellationToken.None);
        ReadTemplateResult lowerCaseDto = Assert.IsType<ReadTemplateResult>(lowerCaseResult);
        Assert.Equal("README.md.scriban", lowerCaseDto.Path);
        Assert.Equal(readmeContent, lowerCaseDto.Content);
    }

    /// <summary>
    /// 窄例外不放大：子目录形态、父级跳转、绝对路径与其它未声明文件仍按「模板文件不存在」拒绝；
    /// 包根无 README 时该路径同样拒绝。
    /// </summary>
    [Fact]
    public async Task ReadTemplateAsync_NonRootOrUndeclaredPaths_StillRejected()
    {
        TemplateQueryTools tools = await CreateToolsAsync(withRootReadme: true);
        string absolutePath = Path.Combine(GetCurrentPackageRoot(), "README.md.scriban");

        string[] rejectedPaths =
        {
            "sub/README.md.scriban",
            "../README.md.scriban",
            absolutePath,
            "other.md.scriban",
            "template.json"
        };

        foreach (string rejectedPath in rejectedPaths)
        {
            McpException error = await Assert.ThrowsAsync<McpException>(
                () => tools.ReadTemplateAsync("simple", rejectedPath, CancellationToken.None));
            Assert.StartsWith("[TemplatePackageError]", error.Message);
            Assert.Contains("模板文件不存在", error.Message);
        }

        TemplateQueryTools emptyReadmeTools = await CreateToolsAsync(withRootReadme: false);
        McpException missingError = await Assert.ThrowsAsync<McpException>(
            () => emptyReadmeTools.ReadTemplateAsync("simple", "README.md.scriban", CancellationToken.None));
        Assert.StartsWith("[TemplatePackageError]", missingError.Message);
        Assert.Contains("模板文件不存在", missingError.Message);
    }

    /// <summary>
    /// list_templates 在包根存在 README 时附带文档型条目（output 空串、enabled=false、language=Plain、scope=project），
    /// 受分组/语言过滤约束；该条目不进入包文件清单，fileCount 不变。
    /// </summary>
    [Fact]
    public async Task ListTemplatesAsync_RootReadme_AddsDocumentEntryWithoutChangingFileCount()
    {
        TemplateQueryTools tools = await CreateToolsAsync(withRootReadme: true);

        ListTemplatesResult allResult = await tools.ListTemplatesAsync("simple", null, null, CancellationToken.None);
        TemplateEntry documentEntry = Assert.Single(allResult.Templates.Where(entry => entry.Path == "README.md.scriban"));
        Assert.Equal(string.Empty, documentEntry.Output);
        Assert.False(documentEntry.Enabled);
        Assert.Equal("Plain", documentEntry.Language);
        Assert.Equal("project", documentEntry.Scope);
        Assert.Equal(2, allResult.Templates.Count);

        // 分组过滤非空时根级文档不命中（即使分组文本与文档路径全等）
        ListTemplatesResult groupedResult = await tools.ListTemplatesAsync("simple", "README.md.scriban", null, CancellationToken.None);
        Assert.DoesNotContain(groupedResult.Templates, entry => entry.Path == "README.md.scriban");

        // 语言过滤：plain 命中文档条目，java 不命中
        ListTemplatesResult plainResult = await tools.ListTemplatesAsync("simple", null, "plain", CancellationToken.None);
        Assert.Single(plainResult.Templates);
        Assert.Equal("README.md.scriban", plainResult.Templates[0].Path);

        ListTemplatesResult javaResult = await tools.ListTemplatesAsync("simple", null, "java", CancellationToken.None);
        Assert.DoesNotContain(javaResult.Templates, entry => entry.Path == "README.md.scriban");

        // 文档条目不进包文件清单：fileCount 仍为清单声明数
        ListTemplatePackagesResult packagesResult = await tools.ListTemplatePackagesAsync(CancellationToken.None);
        TemplatePackageSummary summary = Assert.Single(packagesResult.Packages);
        Assert.Equal(1, summary.FileCount);
    }

    /// <summary>
    /// 生成请求组装只消费包清单文件：README 不在 selections 中，证明文档型条目零参与生成。
    /// </summary>
    [Fact]
    public async Task GenerationRequest_RootReadme_NeverSelected()
    {
        await CreateToolsAsync(withRootReadme: true);
        var packageService = new TemplatePackageService(
            CreateConfigService(GetPackageParentDirectory()),
            NullLogger<TemplatePackageService>.Instance,
            GetBuiltinRoot());

        var request = await GenerationRequestFactory.CreateAsync(
            packageService, "simple", tables: null, files: null, workspaceRoot: @"C:\ws",
            codeDirectory: "src/main/java/com/example", basePackageOverride: null,
            duplicateStrategy: "overwrite", persistLastUsedPaths: false,
            variables: null, modules: null, CancellationToken.None);

        Assert.Single(request.SelectedFiles);
        Assert.DoesNotContain(request.SelectedFiles, selection => selection.TemplateFileName == "README.md.scriban");
    }

    /// <summary>
    /// 创建测试工具组：临时目录内落一个声明单模板（project 作用域）的 simple 用户包，
    /// 可选在包根落 README.md.scriban；返回经真实包服务装配的只读查询工具组。
    /// </summary>
    private async Task<TemplateQueryTools> CreateToolsAsync(bool withRootReadme)
    {
        string tempRoot = CreateTempRoot();
        string userRoot = Path.Combine(tempRoot, "user");
        string builtinRoot = Path.Combine(tempRoot, "builtin");
        string packageRoot = Path.Combine(userRoot, "simple");
        Directory.CreateDirectory(packageRoot);
        Directory.CreateDirectory(builtinRoot);

        string manifest = "{\"name\":\"simple\",\"description\":\"测试包\",\"engine\":\"scriban\","
            + "\"files\":[{\"template\":\"entity.java.scriban\",\"output\":\"gen/Entity.java\",\"enabled\":true,\"scope\":\"project\"}]}";
        await File.WriteAllTextAsync(Path.Combine(packageRoot, "template.json"), manifest);
        await File.WriteAllTextAsync(Path.Combine(packageRoot, "entity.java.scriban"), "class Entity {}\n");

        if (withRootReadme)
        {
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "README.md.scriban"), "# 传参契约\n\nmodules：业务模块清单。\n");
            Directory.CreateDirectory(Path.Combine(packageRoot, "sub"));
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "sub", "README.md.scriban"), "# 子目录文档\n");
        }

        var packageService = new TemplatePackageService(
            CreateConfigService(userRoot),
            NullLogger<TemplatePackageService>.Instance,
            builtinRoot);
        return new TemplateQueryTools(packageService, new TemplateFileWriter(), NullLogger<TemplateQueryTools>.Instance);
    }

    /// <summary>
    /// 创建并登记一个测试临时根目录，供 Dispose 统一清理。
    /// </summary>
    private string CreateTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
        _tempRoots.Add(root);
        return root;
    }

    /// <summary>
    /// 从已创建的临时根定位当前包的包根目录（取最近创建的根）。
    /// </summary>
    private string GetCurrentPackageRoot()
    {
        string latestRoot = _tempRoots[^1];
        return Path.Combine(latestRoot, "user", "simple");
    }

    /// <summary>
    /// 从已创建的临时根定位用户模板目录（取最近创建的根）。
    /// </summary>
    private string GetPackageParentDirectory()
    {
        string latestRoot = _tempRoots[^1];
        return Path.Combine(latestRoot, "user");
    }

    /// <summary>
    /// 从已创建的临时根定位内置包目录（取最近创建的根）。
    /// </summary>
    private string GetBuiltinRoot()
    {
        string latestRoot = _tempRoots[^1];
        return Path.Combine(latestRoot, "builtin");
    }

    /// <summary>
    /// 构造配置服务测试替身：模板搜索目录指向指定用户模板目录。
    /// </summary>
    private static StubConfigService CreateConfigService(string templateDirectory)
    {
        var config = new AppConfig
        {
            TemplateSearchDirectories = new List<string> { templateDirectory }
        };
        return new StubConfigService { Current = config };
    }

    /// <summary>
    /// 配置服务测试替身，仅承载内存配置快照供模板包服务读取搜索目录。
    /// </summary>
    private sealed class StubConfigService : IConfigService
    {
        /// <summary>
        /// 内存配置快照，默认空配置。
        /// </summary>
        public AppConfig Current { get; set; } = new();

        /// <inheritdoc />
        public string ConfigFilePath => "stub";

        /// <inheritdoc />
        public event EventHandler? ConfigChanged;

        /// <inheritdoc />
        public AppConfig Load() => Current;

        /// <inheritdoc />
        public void Save()
        {
            ConfigChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <inheritdoc />
        public GenerationDefaults GetGenerationDefaults() => new(string.Empty, string.Empty);

        /// <inheritdoc />
        public string? GetLlmApiKey() => null;

        /// <inheritdoc />
        public void ChangeDataDirectory(string dataDirectory)
        {
            throw new NotSupportedException("测试替身不支持切换数据目录。");
        }
    }
}
