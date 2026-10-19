using System.Reflection;
using System.Text.Json;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates.Packages;
using DbCodeGen.McpServer.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbCodeGen.Core.Tests.Templates.Packages;

/// <summary>
/// 模板预设单元测试：manifest.presets 解析与归一（enabled/all/files 三口径、说明、变量、名称校验）、
/// 未知 preset 错误文案、生成请求工厂的 preset 解析与变量合并优先级、preset 与 files 互斥、向后兼容。
/// </summary>
public sealed class TemplatePresetResolverTests : IDisposable
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
    /// manifest.presets 正常加载：include 口径、显式 files、变量类型保真，摘要文件数按口径解析。
    /// </summary>
    [Fact]
    public async Task LoadFromDirectoryAsync_Presets_LoadedAndSummarized()
    {
        TemplatePackageInfo package = await CreatePackageAsync(FullManifestJson);

        Assert.Equal(4, package.Presets.Count);
        Assert.Equal("enabled", package.Presets["remote"].Include);
        Assert.Null(package.Presets["remote"].Files);
        Assert.Equal("all", package.Presets["embedded"].Include);
        Assert.Equal(new[] { "a.java.scriban", "p.java.scriban" }, package.Presets["fileset"].Files);

        JsonElement boostFlag = package.Presets["boost"].Variables!["flag"];
        Assert.Equal(JsonValueKind.True, boostFlag.ValueKind);
        JsonElement boostList = package.Presets["boost"].Variables!["list"];
        Assert.Equal(JsonValueKind.Array, boostList.ValueKind);

        IReadOnlyList<TemplatePresetSummary> summaries = TemplatePresetResolver.GetSummaries(package);
        Assert.Equal(4, summaries.Count);
        Assert.Equal(("remote", 2), (summaries[0].Name, summaries[0].FileCount));
        Assert.Equal(("embedded", 5), (summaries[1].Name, summaries[1].FileCount));
        Assert.Equal(("fileset", 2), (summaries[2].Name, summaries[2].FileCount));
        Assert.Equal(("boost", 2), (summaries[3].Name, summaries[3].FileCount));
    }

    /// <summary>
    /// 三口径解析：enabled 取启用条目、all 取全部条目、显式 files 可取 disabled 条目且保持清单顺序。
    /// </summary>
    [Fact]
    public async Task ResolveFiles_ThreeModes_ResolveExpectedEntries()
    {
        TemplatePackageInfo package = await CreatePackageAsync(FullManifestJson);

        IReadOnlyList<TemplateFileInfo> remote = TemplatePresetResolver.ResolveFiles(package, "remote");
        Assert.Equal(new[] { "a.java.scriban", "b.ts.scriban" }, remote.Select(file => file.RelativeTemplatePath));

        IReadOnlyList<TemplateFileInfo> embedded = TemplatePresetResolver.ResolveFiles(package, "embedded");
        Assert.Equal(5, embedded.Count);

        IReadOnlyList<TemplateFileInfo> fileset = TemplatePresetResolver.ResolveFiles(package, "fileset");
        Assert.Equal(new[] { "a.java.scriban", "p.java.scriban" }, fileset.Select(file => file.RelativeTemplatePath));
        Assert.False(fileset[1].IsEnabled);
    }

    /// <summary>
    /// 预设名称大小写不敏感；未知 preset 抛参数异常并列出全部可用 preset。
    /// </summary>
    [Fact]
    public async Task ResolveFiles_UnknownPreset_ThrowsWithAvailableNames()
    {
        TemplatePackageInfo package = await CreatePackageAsync(FullManifestJson);

        IReadOnlyList<TemplateFileInfo> upperCase = TemplatePresetResolver.ResolveFiles(package, "REMOTE");
        Assert.Equal(2, upperCase.Count);

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => TemplatePresetResolver.ResolveFiles(package, "missing"));
        Assert.Contains("未知 preset", exception.Message);
        Assert.Contains("remote", exception.Message);
        Assert.Contains("embedded", exception.Message);
        Assert.Contains("list_template_packages", exception.Message);
    }

    /// <summary>
    /// 非法 presets 声明在包加载阶段结构化拒绝：include/files 互斥、说明缺失、文件不存在、名称非法、变量空键。
    /// </summary>
    [Theory]
    [InlineData("\"bad\":{\"description\":\"x\",\"include\":\"all\",\"files\":[\"a.java.scriban\"]}")]
    [InlineData("\"bad\":{\"include\":\"enabled\"}")]
    [InlineData("\"bad\":{\"description\":\"x\",\"files\":[\"missing.java.scriban\"]}")]
    [InlineData("\"bad name\":{\"description\":\"x\"}")]
    [InlineData("\"bad\":{\"description\":\"x\",\"variables\":{\" \":1}}")]
    [InlineData("\"bad\":{\"description\":\"x\",\"include\":\"unknown\"}")]
    public async Task LoadFromDirectoryAsync_InvalidPreset_Rejected(string presetJson)
    {
        string manifest = BuildManifest(presetJson);
        string packageRoot = await WritePackageAsync(manifest);

        await Assert.ThrowsAsync<TemplatePackageException>(
            () => TemplatePackageLoader.LoadFromDirectoryAsync(packageRoot, isBuiltin: false, CancellationToken.None));
    }

    /// <summary>
    /// 选择口径解析不出任何模板（包内无启用条目且 presets 用 enabled）时加载期拒绝，避免列出不可用预设。
    /// </summary>
    [Fact]
    public async Task LoadFromDirectoryAsync_PresetResolvesNoTemplate_Rejected()
    {
        const string manifest = """
            {
              "name": "empty-preset-pkg",
              "description": "空预设",
              "engine": "scriban",
              "files": [
                { "template": "a.java.scriban", "output": "a/Out.java", "enabled": false }
              ],
              "presets": {
                "enabledOnly": { "description": "启用集", "include": "enabled" }
              }
            }
            """;
        string packageRoot = await WritePackageAsync(manifest);

        TemplatePackageException exception = await Assert.ThrowsAsync<TemplatePackageException>(
            () => TemplatePackageLoader.LoadFromDirectoryAsync(packageRoot, isBuiltin: false, CancellationToken.None));
        Assert.Contains("解析不出任何模板", exception.Message);
    }

    /// <summary>
    /// 无 presets 声明的包经模板编辑回写清单时不新增空 presets 字段（避免既有包清单产生无意义 diff）。
    /// </summary>
    [Fact]
    public async Task SaveTemplateContentAsync_ManifestWithoutPresets_DoesNotAddEmptyPresets()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
        _tempRoots.Add(tempRoot);
        string userRoot = Path.Combine(tempRoot, "user");
        string builtinRoot = Path.Combine(tempRoot, "builtin");
        string packageRoot = Path.Combine(userRoot, "plain-pkg");
        Directory.CreateDirectory(packageRoot);
        Directory.CreateDirectory(builtinRoot);
        await File.WriteAllTextAsync(
            Path.Combine(packageRoot, TemplatePackageLoader.ManifestFileName),
            """
            {
              "name": "plain-pkg",
              "description": "无预设包",
              "engine": "scriban",
              "files": [
                { "template": "a.java.scriban", "output": "a/Out.java", "enabled": true }
              ]
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(packageRoot, "a.java.scriban"), "old\n");

        var service = new TemplatePackageService(
            CreateConfigService(userRoot), NullLogger<TemplatePackageService>.Instance, builtinRoot);
        TemplatePackageOperationResult result = await service.SaveTemplateContentAsync(
            "plain-pkg", "a.java.scriban", "new content\n", CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Succeeded, result.Status);
        string manifestText = await File.ReadAllTextAsync(
            Path.Combine(packageRoot, TemplatePackageLoader.ManifestFileName));
        Assert.DoesNotContain("\"presets\"", manifestText);
    }

    /// <summary>
    /// 含 presets 的包经模板编辑回写清单时保留预设定义，且 include/files 互斥口径不写 null 字段。
    /// </summary>
    [Fact]
    public async Task SaveTemplateContentAsync_ManifestWithPresets_RoundTripsWithoutNullFields()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
        _tempRoots.Add(tempRoot);
        string userRoot = Path.Combine(tempRoot, "user");
        string builtinRoot = Path.Combine(tempRoot, "builtin");
        string packageRoot = Path.Combine(userRoot, "preset-roundtrip");
        Directory.CreateDirectory(packageRoot);
        Directory.CreateDirectory(builtinRoot);
        await File.WriteAllTextAsync(
            Path.Combine(packageRoot, TemplatePackageLoader.ManifestFileName),
            """
            {
              "name": "preset-roundtrip",
              "description": "预设回写包",
              "engine": "scriban",
              "files": [
                { "template": "a.java.scriban", "output": "a/Out.java", "enabled": true }
              ],
              "presets": {
                "remote": { "description": "启用集", "include": "enabled", "variables": { "platformUi": "remote" } },
                "fileset": { "description": "显式清单", "files": ["a.java.scriban"] }
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(packageRoot, "a.java.scriban"), "old\n");

        var service = new TemplatePackageService(
            CreateConfigService(userRoot), NullLogger<TemplatePackageService>.Instance, builtinRoot);
        TemplatePackageOperationResult result = await service.SaveTemplateContentAsync(
            "preset-roundtrip", "a.java.scriban", "new content\n", CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Succeeded, result.Status);
        string manifestText = await File.ReadAllTextAsync(
            Path.Combine(packageRoot, TemplatePackageLoader.ManifestFileName));
        Assert.Contains("\"presets\"", manifestText);
        Assert.Contains("\"include\": \"enabled\"", manifestText);
        Assert.DoesNotContain("\"files\": null", manifestText);
        Assert.DoesNotContain("\"variables\": null", manifestText);
    }

    /// <summary>
    /// 生成请求工厂：preset 解析为模板选择集合，并把预设默认变量注入请求。
    /// </summary>
    [Fact]
    public async Task CreateAsync_WithPreset_ResolvesSelectionsAndVariables()
    {
        TemplatePackageInfo package = await CreatePackageAsync(FullManifestJson);
        var packageService = new FakeTemplatePackageService(package);

        GenerationRequest request = await GenerationRequestFactory.CreateAsync(
            packageService, "preset-pkg",
            tables: new[] { new SyntheticTableInput { Name = "t_asset" } },
            files: null,
            workspaceRoot: @"C:\ws",
            codeDirectory: "src/main/java/com/example",
            basePackageOverride: null,
            duplicateStrategy: "skip",
            persistLastUsedPaths: false,
            variables: null,
            modules: null,
            CancellationToken.None,
            preset: "boost");

        Assert.Equal(2, request.SelectedFiles.Count);
        Assert.NotNull(request.Variables);
        Assert.Equal("embedded", ((JsonElement)request.Variables!["platformUi"]!).GetString());
        Assert.Equal(JsonValueKind.True, ((JsonElement)request.Variables["flag"]!).ValueKind);
    }

    /// <summary>
    /// 变量合并优先级：调用方显式 variables 同名键覆盖预设默认值，其余预设键保留。
    /// </summary>
    [Fact]
    public async Task CreateAsync_PresetVariables_CallerOverridesPerKey()
    {
        TemplatePackageInfo package = await CreatePackageAsync(FullManifestJson);
        var packageService = new FakeTemplatePackageService(package);
        var callerVariables = new Dictionary<string, JsonElement>
        {
            ["platformUi"] = JsonSerializer.SerializeToElement("custom")
        };

        GenerationRequest request = await GenerationRequestFactory.CreateAsync(
            packageService, "preset-pkg",
            tables: new[] { new SyntheticTableInput { Name = "t_asset" } },
            files: null,
            workspaceRoot: @"C:\ws",
            codeDirectory: "src/main/java/com/example",
            basePackageOverride: null,
            duplicateStrategy: "skip",
            persistLastUsedPaths: false,
            variables: callerVariables,
            modules: null,
            CancellationToken.None,
            preset: "boost");

        Assert.Equal("custom", ((JsonElement)request.Variables!["platformUi"]!).GetString());
        Assert.True(request.Variables.ContainsKey("flag"));
    }

    /// <summary>
    /// preset 与 files 互斥：同时提供结构化拒绝（InvalidArgument 语义）；空 files 清单同样按互斥拒绝。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateAsync_PresetAndFiles_ThrowsMutualExclusion(bool useExplicitFile)
    {
        TemplatePackageInfo package = await CreatePackageAsync(FullManifestJson);
        var packageService = new FakeTemplatePackageService(package);
        IReadOnlyList<string> files = useExplicitFile ? new[] { "a.java.scriban" } : Array.Empty<string>();

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => GenerationRequestFactory.CreateAsync(
            packageService, "preset-pkg",
            tables: new[] { new SyntheticTableInput { Name = "t_asset" } },
            files: files,
            workspaceRoot: @"C:\ws",
            codeDirectory: "src/main/java/com/example",
            basePackageOverride: null,
            duplicateStrategy: "skip",
            persistLastUsedPaths: false,
            variables: null,
            modules: null,
            CancellationToken.None,
            preset: "remote"));

        Assert.Contains("互斥", exception.Message);
    }

    /// <summary>
    /// 向后兼容：不传 preset/files 时仍取包内全部启用条目；未知 preset 报可用清单。
    /// </summary>
    [Fact]
    public async Task CreateAsync_NoPresetOrFiles_KeepsEnabledDefault()
    {
        TemplatePackageInfo package = await CreatePackageAsync(FullManifestJson);
        var packageService = new FakeTemplatePackageService(package);

        GenerationRequest baseline = await GenerationRequestFactory.CreateAsync(
            packageService, "preset-pkg",
            tables: new[] { new SyntheticTableInput { Name = "t_asset" } },
            files: null,
            workspaceRoot: @"C:\ws",
            codeDirectory: "src/main/java/com/example",
            basePackageOverride: null,
            duplicateStrategy: "skip",
            persistLastUsedPaths: false,
            variables: null,
            modules: null,
            CancellationToken.None);

        Assert.Equal(2, baseline.SelectedFiles.Count);
        Assert.Null(baseline.Variables);

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => GenerationRequestFactory.CreateAsync(
            packageService, "preset-pkg",
            tables: new[] { new SyntheticTableInput { Name = "t_asset" } },
            files: null,
            workspaceRoot: @"C:\ws",
            codeDirectory: "src/main/java/com/example",
            basePackageOverride: null,
            duplicateStrategy: "skip",
            persistLastUsedPaths: false,
            variables: null,
            modules: null,
            CancellationToken.None,
            preset: "unknown"));
        Assert.Contains("未知 preset", exception.Message);
    }

    /// <summary>
    /// MCP 工具面：preview_generation / generate 均暴露 preset 参数（string，默认 null）。
    /// </summary>
    [Fact]
    public void GenerationTools_ExposePresetParameter()
    {
        MethodInfo previewMethod = typeof(PreviewTools).GetMethod(nameof(PreviewTools.PreviewGenerationAsync))!;
        ParameterInfo[] previewParameters = previewMethod.GetParameters();
        ParameterInfo previewPreset = previewParameters.Single(parameter => parameter.Name == "preset");
        MethodInfo generateMethod = typeof(GenerationTools).GetMethod(nameof(GenerationTools.GenerateAsync))!;
        ParameterInfo[] generateParameters = generateMethod.GetParameters();
        ParameterInfo generatePreset = generateParameters.Single(parameter => parameter.Name == "preset");

        Assert.Equal(typeof(string), previewPreset.ParameterType);
        Assert.Null(previewPreset.DefaultValue);
        Assert.Equal(typeof(string), generatePreset.ParameterType);
        Assert.Null(generatePreset.DefaultValue);
    }

    /// <summary>
    /// 测试包清单：5 个模板（2 启用 / 3 未启用）与 4 个预设。
    /// </summary>
    private const string FullManifestJson = """
        {
          "name": "preset-pkg",
          "description": "预设测试包",
          "engine": "scriban",
          "basePackage": "com.example",
          "files": [
            { "template": "a.java.scriban", "output": "a/{{table.className}}.java", "enabled": true },
            { "template": "b.ts.scriban", "output": "web/b.ts", "enabled": true, "root": "web" },
            { "template": "p.java.scriban", "output": "p/{{table.className}}.java", "enabled": false },
            { "template": "m.ts.scriban", "output": "m/{{module.name}}.ts", "enabled": false, "scope": "module" },
            { "template": "app.ts.scriban", "output": "app.ts", "enabled": false, "scope": "project" }
          ],
          "presets": {
            "remote": { "description": "基础启用集", "include": "enabled", "variables": { "platformUi": "remote" } },
            "embedded": { "description": "全部条目", "include": "all" },
            "fileset": { "description": "显式清单", "files": ["a.java.scriban", "p.java.scriban"] },
            "boost": { "description": "变量注入", "include": "enabled", "variables": { "platformUi": "embedded", "flag": true, "list": [1, 2] } }
          }
        }
        """;

    /// <summary>
    /// 在完整清单基础上替换 presets 段为单个非法预设，构造非法清单文本。
    /// </summary>
    /// <param name="presetJson">单条预设 JSON 片段（形如 "bad":{...}）。</param>
    /// <returns>清单 JSON 文本。</returns>
    private static string BuildManifest(string presetJson)
    {
        return $$"""
            {
              "name": "bad-preset-pkg",
              "description": "非法预设",
              "engine": "scriban",
              "files": [
                { "template": "a.java.scriban", "output": "a/Out.java", "enabled": true }
              ],
              "presets": { {{presetJson}} }
            }
            """;
    }

    /// <summary>
    /// 在临时目录落包并加载；模板文件正文按路径生成。
    /// </summary>
    /// <param name="manifestJson">清单 JSON 文本。</param>
    /// <returns>加载后的模板包信息。</returns>
    private async Task<TemplatePackageInfo> CreatePackageAsync(string manifestJson)
    {
        string packageRoot = await WritePackageAsync(manifestJson);
        return await TemplatePackageLoader.LoadFromDirectoryAsync(packageRoot, isBuiltin: false, CancellationToken.None);
    }

    /// <summary>
    /// 在临时目录写入清单与 5 个模板文件。
    /// </summary>
    /// <param name="manifestJson">清单 JSON 文本。</param>
    /// <returns>包根目录。</returns>
    private async Task<string> WritePackageAsync(string manifestJson)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
        _tempRoots.Add(tempRoot);
        string packageRoot = Path.Combine(tempRoot, "pkg");
        Directory.CreateDirectory(packageRoot);
        await File.WriteAllTextAsync(Path.Combine(packageRoot, TemplatePackageLoader.ManifestFileName), manifestJson);

        foreach (string template in new[]
                 {
                     "a.java.scriban", "b.ts.scriban", "p.java.scriban", "m.ts.scriban", "app.ts.scriban"
                 })
        {
            await File.WriteAllTextAsync(Path.Combine(packageRoot, template), "content\n");
        }

        return packageRoot;
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

    /// <summary>
    /// 模板包服务测试替身：仅承载按名加载预建包。
    /// </summary>
    private sealed class FakeTemplatePackageService : ITemplatePackageService
    {
        private readonly TemplatePackageInfo _package;

        /// <summary>
        /// 以预加载包构造替身。
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
        public Task<TemplatePackageOperationResult> RenamePackageAsync(string packageName, string newName, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> RenameTemplateFileAsync(string packageName, string templateRelativePath, string newRelativePath, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task DeletePackageAsync(string packageName, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> CreatePackageAsync(
            string packageName, string description, string? firstTemplatePath, string? firstOutputPath,
            CancellationToken cancellationToken, string? basePackage = null,
            IReadOnlyDictionary<string, string>? typeMap = null)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> AddTemplateFileAsync(
            string packageName, string templateRelativePath, string outputPath, CancellationToken cancellationToken,
            string? scope = null)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> DeleteTemplateFileAsync(string packageName, string templateRelativePath, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> AppendTemplateFilesAsync(string packageName, IReadOnlyList<TemplateFileWriteEntry> files, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> UpdatePackageMetadataAsync(
            string packageName, string? description, string? basePackage, CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? typeMap = null)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> SaveTemplateContentAsync(string packageName, string templateRelativePath, string content, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> EditTemplateContentAsync(
            string packageName, string templateRelativePath, string oldText, string newText,
            string? expectedContentHash, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
