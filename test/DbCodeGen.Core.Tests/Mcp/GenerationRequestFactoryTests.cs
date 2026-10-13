using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;
using DbCodeGen.McpServer.Tools;

namespace DbCodeGen.Core.Tests.Mcp;

/// <summary>
/// 生成请求组装工厂单元测试：tables 按所选模板作用域分型——
/// 纯 module/project 作用域集合 tables 省略可过、含 table 作用域条目时 tables 空仍拒绝（中文可读指引）、
/// files 过滤改变生效模板集合时分型判定跟随过滤结果。
/// </summary>
public sealed class GenerationRequestFactoryTests : IDisposable
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
    /// 所选模板全部为 module/project 作用域（files 省略 = 包内全部启用条目）时，
    /// tables 省略应可过并构建出空表请求，代码目录派生包名与输出根照常生效。
    /// </summary>
    [Fact]
    public async Task CreateAsync_ProjectAndModuleScopeOnly_TablesOmitted_BuildsRequest()
    {
        TemplatePackageInfo package = await CreatePackageAsync("scoped-pkg", new (string, string, string, string?)[]
        {
            ("module-info.cs.scriban", "gen/Modules/{{module.name}}/Info.cs", "module {{module.name}}\n", "module"),
            ("app-context.cs.scriban", "src/Data/AppDbContext.cs", "app\n", "project")
        });
        var packageService = new FakeTemplatePackageService(package);

        GenerationRequest request = await GenerationRequestFactory.CreateAsync(
            packageService, "scoped-pkg", null, null, @"C:\ws", "src/main/java/com/example",
            basePackageOverride: null, "overwrite", persistLastUsedPaths: false,
            variables: null, modules: null, CancellationToken.None);

        Assert.Empty(request.Tables);
        Assert.Equal("src/main/java", request.RelativeOutputRoot);
        Assert.Equal("com.example", request.BasePackageOverride);
        Assert.Equal(2, request.SelectedFiles.Count);
    }

    /// <summary>
    /// 生效模板集合含 table 作用域条目（含缺省 scope 条目）而 tables 省略或传空集合时，
    /// 应结构化拒绝并给出中文可读指引。
    /// </summary>
    [Fact]
    public async Task CreateAsync_TableScopeSelection_TablesEmpty_ThrowsWithReadableGuidance()
    {
        TemplatePackageInfo package = await CreatePackageAsync("table-pkg", new (string, string, string, string?)[]
        {
            ("entity.java.scriban", "out/{{table.variableName}}.java", "class {{table.className}}\n", null)
        });
        var packageService = new FakeTemplatePackageService(package);

        ArgumentException nullException = await Assert.ThrowsAsync<ArgumentException>(
            () => GenerationRequestFactory.CreateAsync(
                packageService, "table-pkg", null, null, @"C:\ws", "src/main/java/com/example",
                basePackageOverride: null, "overwrite", persistLastUsedPaths: false,
                variables: null, modules: null, CancellationToken.None));
        Assert.Contains("table 作用域", nullException.Message);
        Assert.Contains("至少一张", nullException.Message);

        ArgumentException emptyException = await Assert.ThrowsAsync<ArgumentException>(
            () => GenerationRequestFactory.CreateAsync(
                packageService, "table-pkg", new List<SyntheticTableInput>(), null, @"C:\ws", "src/main/java/com/example",
                basePackageOverride: null, "overwrite", persistLastUsedPaths: false,
                variables: null, modules: null, CancellationToken.None));
        Assert.Contains("list_templates", emptyException.Message);
    }

    /// <summary>
    /// 混合作用域包按 files 过滤为纯 project 作用域子集时，tables 省略应可过（分型判定跟随过滤结果）。
    /// </summary>
    [Fact]
    public async Task CreateAsync_FilesFilteredToProjectScope_TablesOmitted_Passes()
    {
        TemplatePackageInfo package = await CreatePackageAsync("mixed-pkg", new (string, string, string, string?)[]
        {
            ("entity.java.scriban", "out/{{table.variableName}}.java", "class {{table.className}}\n", null),
            ("app-context.cs.scriban", "src/Data/AppDbContext.cs", "app\n", "project")
        });
        var packageService = new FakeTemplatePackageService(package);

        GenerationRequest request = await GenerationRequestFactory.CreateAsync(
            packageService, "mixed-pkg", null, new[] { "app-context.cs.scriban" }, @"C:\ws", "src/main/java/com/example",
            basePackageOverride: null, "overwrite", persistLastUsedPaths: false,
            variables: null, modules: null, CancellationToken.None);

        Assert.Empty(request.Tables);
        TemplateFileSelection selection = Assert.Single(request.SelectedFiles);
        Assert.Equal(TemplateScope.Project, selection.Scope);
    }

    /// <summary>
    /// 混合作用域包按 files 过滤为 table 作用域子集时，tables 省略仍应拒绝。
    /// </summary>
    [Fact]
    public async Task CreateAsync_FilesFilteredToTableScope_TablesOmitted_Throws()
    {
        TemplatePackageInfo package = await CreatePackageAsync("mixed-pkg", new (string, string, string, string?)[]
        {
            ("entity.java.scriban", "out/{{table.variableName}}.java", "class {{table.className}}\n", null),
            ("app-context.cs.scriban", "src/Data/AppDbContext.cs", "app\n", "project")
        });
        var packageService = new FakeTemplatePackageService(package);

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
            () => GenerationRequestFactory.CreateAsync(
                packageService, "mixed-pkg", null, new[] { "entity.java.scriban" }, @"C:\ws", "src/main/java/com/example",
                basePackageOverride: null, "overwrite", persistLastUsedPaths: false,
                variables: null, modules: null, CancellationToken.None));

        Assert.Contains("table 作用域", exception.Message);
    }

    /// <summary>
    /// 含 table 作用域条目且正常提供合成表时请求照常构建，合成表映射为 Core 表元数据（回归守卫）。
    /// </summary>
    [Fact]
    public async Task CreateAsync_TableScopeWithProvidedTables_PassesAndMapsTables()
    {
        TemplatePackageInfo package = await CreatePackageAsync("table-pkg", new (string, string, string, string?)[]
        {
            ("entity.java.scriban", "out/{{table.variableName}}.java", "class {{table.className}}\n", null)
        });
        var packageService = new FakeTemplatePackageService(package);
        var tables = new List<SyntheticTableInput>
        {
            new() { Name = "t_user_info" }
        };

        GenerationRequest request = await GenerationRequestFactory.CreateAsync(
            packageService, "table-pkg", tables, null, @"C:\ws", "src/main/java/com/example",
            basePackageOverride: null, "overwrite", persistLastUsedPaths: false,
            variables: null, modules: null, CancellationToken.None);

        TableInfo mapped = Assert.Single(request.Tables);
        Assert.Equal("t_user_info", mapped.RawName);
        Assert.Equal("TUserInfo", mapped.ClassName);
    }

    /// <summary>
    /// 在临时目录下按文件清单创建测试模板包并加载；scope 传 null 表示缺省 table 作用域。
    /// </summary>
    /// <param name="packageName">包名。</param>
    /// <param name="files">模板相对路径、输出路径模板、模板内容与 scope 文本四元组清单。</param>
    /// <returns>加载后的模板包信息。</returns>
    private async Task<TemplatePackageInfo> CreatePackageAsync(
        string packageName,
        IReadOnlyList<(string Template, string Output, string Content, string? Scope)> files)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
        _tempRoots.Add(tempRoot);
        string packageDir = Path.Combine(tempRoot, packageName);
        Directory.CreateDirectory(packageDir);

        var manifest = new TemplateManifest
        {
            Name = packageName,
            Description = "工厂分型测试包",
            Engine = "scriban",
            BasePackage = "com.example",
            TypeMap = new Dictionary<string, string>(),
            Files = files.Select(file => new TemplateFileEntry
            {
                Template = file.Template,
                Output = file.Output,
                Scope = file.Scope,
                Enabled = true
            }).ToList()
        };

        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };
        await File.WriteAllTextAsync(Path.Combine(packageDir, TemplatePackageLoader.ManifestFileName), System.Text.Json.JsonSerializer.Serialize(manifest, jsonOptions));
        foreach ((string template, _, string content, _) in files)
        {
            await File.WriteAllTextAsync(Path.Combine(packageDir, template), content);
        }

        return await TemplatePackageLoader.LoadFromDirectoryAsync(packageDir, isBuiltin: false, CancellationToken.None);
    }

    /// <summary>
    /// 模板包服务测试替身：仅承载按名加载预建包，变更类能力不参与本测试。
    /// </summary>
    private sealed class FakeTemplatePackageService : ITemplatePackageService
    {
        private readonly Dictionary<string, TemplatePackageInfo> _packages;

        /// <summary>
        /// 以预加载的包集合构造替身。
        /// </summary>
        /// <param name="packages">可按名加载的模板包集合。</param>
        public FakeTemplatePackageService(params TemplatePackageInfo[] packages)
        {
            _packages = packages.ToDictionary(package => package.Name, StringComparer.Ordinal);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<TemplatePackageInfo>> ListPackagesAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<TemplatePackageInfo> packages = _packages.Values.ToList();
            return Task.FromResult(packages);
        }

        /// <inheritdoc />
        public Task<TemplatePackageInfo> LoadPackageAsync(string packageName, CancellationToken cancellationToken)
        {
            if (_packages.TryGetValue(packageName, out TemplatePackageInfo? package))
            {
                return Task.FromResult(package);
            }

            throw new TemplatePackageException($"模板包不存在：{packageName}。");
        }

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> ImportFromZipAsync(string zipPath, bool overwrite, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("测试替身不支持 zip 导入。");
        }

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> ImportFromFolderAsync(string folderPath, bool overwrite, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("测试替身不支持文件夹导入。");
        }

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> CopyPackageAsync(string sourceName, string newName, bool overwrite, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("测试替身不支持复制包。");
        }

        /// <inheritdoc />
        public Task<string> ExportToZipAsync(string packageName, string targetZipPath, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("测试替身不支持导出 zip。");
        }

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> RenamePackageAsync(string packageName, string newName, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("测试替身不支持重命名包。");
        }

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> RenameTemplateFileAsync(
            string packageName, string templateRelativePath, string newRelativePath, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("测试替身不支持重命名模板文件。");
        }

        /// <inheritdoc />
        public Task DeletePackageAsync(string packageName, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("测试替身不支持删除包。");
        }

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> CreatePackageAsync(
            string packageName, string description, string? firstTemplatePath, string? firstOutputPath,
            CancellationToken cancellationToken, string? basePackage = null,
            IReadOnlyDictionary<string, string>? typeMap = null)
        {
            throw new NotSupportedException("测试替身不支持新建包。");
        }

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> AddTemplateFileAsync(
            string packageName, string templateRelativePath, string outputPath, CancellationToken cancellationToken,
            string? scope = null)
        {
            throw new NotSupportedException("测试替身不支持新增模板文件。");
        }

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> DeleteTemplateFileAsync(
            string packageName, string templateRelativePath, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("测试替身不支持删除模板文件。");
        }

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> AppendTemplateFilesAsync(
            string packageName, IReadOnlyList<TemplateFileWriteEntry> files, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("测试替身不支持批量追加模板文件。");
        }

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> UpdatePackageMetadataAsync(
            string packageName, string? description, string? basePackage, CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? typeMap = null)
        {
            throw new NotSupportedException("测试替身不支持更新包元数据。");
        }

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> SaveTemplateContentAsync(
            string packageName, string templateRelativePath, string content, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("测试替身不支持保存模板正文。");
        }

        /// <inheritdoc />
        public Task<TemplatePackageOperationResult> EditTemplateContentAsync(
            string packageName, string templateRelativePath, string oldText, string newText,
            string? expectedContentHash, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("测试替身不支持局部编辑模板正文。");
        }
    }
}
