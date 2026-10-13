using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Security;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbCodeGen.Core.Tests.Templates.Packages;

/// <summary>
/// 模板包服务模板编辑增强单元测试：覆盖 create_template_file 管道表达式 outputPath 放行（P-B①）与
/// scope 作用域声明补设（P-B②），save_template_edit 子串级单处精确替换（唯一替换/歧义保护/未找到/
/// 条件写乐观锁/行尾落定）与 save_template_content 行尾保真（P-C）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TemplatePackageServiceEditTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly CredentialProtector _protector = new();
    private readonly List<ConfigService> _configServices = new();
    private readonly List<TemplatePackageService> _services = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// 为每个测试实例创建独立临时目录，避免用例间包目录互相污染。
    /// </summary>
    public TemplatePackageServiceEditTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// 释放配置服务与模板包服务并递归删除临时目录。
    /// </summary>
    public void Dispose()
    {
        foreach (ConfigService configService in _configServices)
        {
            configService.Dispose();
        }

        foreach (TemplatePackageService service in _services)
        {
            service.Dispose();
        }

        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// 构造绑定临时用户模板库的模板包服务。
    /// </summary>
    /// <param name="userLibraryRoot">用户模板库根目录。</param>
    /// <returns>模板包服务。</returns>
    private TemplatePackageService CreateService(string userLibraryRoot)
    {
        string configPath = Path.Combine(_tempRoot, $"config-{Guid.NewGuid():N}", "config.json");
        ConfigService config = new(_protector, NullLogger<ConfigService>.Instance, configPath);
        _configServices.Add(config);
        AppConfig appConfig = config.Load();
        appConfig.TemplateSearchDirectories.Clear();
        appConfig.TemplateSearchDirectories.Add(userLibraryRoot);
        config.Save();

        Directory.CreateDirectory(userLibraryRoot);
        TemplatePackageService service = new(
            config, NullLogger<TemplatePackageService>.Instance,
            Path.Combine(_tempRoot, "builtin"), Path.Combine(_tempRoot, "imports"));
        _services.Add(service);
        return service;
    }

    /// <summary>
    /// 在用户模板库中创建含单模板的最小用户包。
    /// </summary>
    /// <param name="userLibraryRoot">用户模板库根目录。</param>
    /// <param name="packageName">包名。</param>
    /// <param name="templateName">模板文件名。</param>
    /// <param name="content">模板正文（按字节落盘，承载行尾场景构造）。</param>
    /// <returns>包目录绝对路径。</returns>
    private static async Task<string> CreateUserPackageAsync(
        string userLibraryRoot, string packageName, string templateName = "entity.java.scriban", string content = "class {{ table.className }} {}\n")
    {
        string packageDir = Path.Combine(userLibraryRoot, packageName);
        Directory.CreateDirectory(packageDir);
        var manifest = new TemplateManifest
        {
            Name = packageName,
            Description = "编辑测试包",
            Engine = "scriban",
            Files = new List<TemplateFileEntry>
            {
                new() { Template = templateName, Output = "out/{{table.variableName}}.java", Enabled = true }
            }
        };
        await File.WriteAllTextAsync(
            Path.Combine(packageDir, TemplatePackageLoader.ManifestFileName),
            JsonSerializer.Serialize(manifest, JsonOptions));
        await File.WriteAllTextAsync(Path.Combine(packageDir, templateName), content);
        return packageDir;
    }

    /// <summary>
    /// 读取包清单原文，供清单字段级断言。
    /// </summary>
    /// <param name="packageDir">包目录。</param>
    /// <returns>清单 JSON 原文。</returns>
    private static async Task<string> ReadManifestTextAsync(string packageDir)
    {
        return await File.ReadAllTextAsync(Path.Combine(packageDir, TemplatePackageLoader.ManifestFileName));
    }

    /// <summary>
    /// P-B①：create_template_file 的 outputPath 含管道表达式（| 过滤器）时应放行并原样写入清单。
    /// </summary>
    [Fact]
    public async Task AddTemplateFileAsync_PipelineExpressionOutputPath_Succeeds()
    {
        string userLibrary = Path.Combine(_tempRoot, "user-pipeline");
        TemplatePackageService service = CreateService(userLibrary);
        await service.CreatePackageAsync("edit-pkg", "包说明", null, null, CancellationToken.None);

        TemplatePackageOperationResult result = await service.AddTemplateFileAsync(
            "edit-pkg", "upper.java.scriban", "out/{{ table.variableName | upcase }}.java", CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Succeeded, result.Status);
        Assert.Equal(
            "out/{{ table.variableName | upcase }}.java",
            Assert.Single(result.Package!.Files, file => file.RelativeTemplatePath == "upper.java.scriban").OutputPath);
    }

    /// <summary>
    /// P-B②：create_template_file 显式传 scope=module 时应写入清单条目并在重载后解析为 Module 作用域。
    /// </summary>
    [Fact]
    public async Task AddTemplateFileAsync_WithModuleScope_PersistsScope()
    {
        string userLibrary = Path.Combine(_tempRoot, "user-scope");
        TemplatePackageService service = CreateService(userLibrary);
        await service.CreatePackageAsync("edit-pkg", "包说明", null, null, CancellationToken.None);

        TemplatePackageOperationResult result = await service.AddTemplateFileAsync(
            "edit-pkg", "module-info.java.scriban", "out/{{module.name}}/Info.java", CancellationToken.None, scope: "module");

        Assert.Equal(TemplatePackageOperationStatus.Succeeded, result.Status);
        Assert.Equal(
            TemplateScope.Module,
            Assert.Single(result.Package!.Files, file => file.RelativeTemplatePath == "module-info.java.scriban").Scope);
        string manifestText = await ReadManifestTextAsync(Path.Combine(userLibrary, "edit-pkg"));
        Assert.Contains("\"scope\": \"module\"", manifestText, StringComparison.Ordinal);
    }

    /// <summary>
    /// P-B②：create_template_file 未传 scope 时清单不写 scope 字段，保持既有缺省形态。
    /// </summary>
    [Fact]
    public async Task AddTemplateFileAsync_WithoutScope_OmitsScopeField()
    {
        string userLibrary = Path.Combine(_tempRoot, "user-noscope");
        TemplatePackageService service = CreateService(userLibrary);
        await service.CreatePackageAsync("edit-pkg", "包说明", null, null, CancellationToken.None);

        TemplatePackageOperationResult result = await service.AddTemplateFileAsync(
            "edit-pkg", "plain.java.scriban", "out/X.java", CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Succeeded, result.Status);
        // 未传 scope 时清单条目不显式声明作用域（scope 序列化为 null，加载缺省 table）
        string manifestText = await ReadManifestTextAsync(Path.Combine(userLibrary, "edit-pkg"));
        using JsonDocument document = JsonDocument.Parse(manifestText);
        JsonElement plainEntry = Assert.Single(
            document.RootElement.GetProperty("files").EnumerateArray(),
            entry => entry.GetProperty("template").GetString() == "plain.java.scriban");
        Assert.Equal(JsonValueKind.Null, plainEntry.GetProperty("scope").ValueKind);
    }

    /// <summary>
    /// P-B②：create_template_file 传非法 scope 时应结构化拒绝并说明合法取值。
    /// </summary>
    [Fact]
    public async Task AddTemplateFileAsync_WithInvalidScope_Rejected()
    {
        string userLibrary = Path.Combine(_tempRoot, "user-badscope");
        TemplatePackageService service = CreateService(userLibrary);
        await service.CreatePackageAsync("edit-pkg", "包说明", null, null, CancellationToken.None);

        TemplatePackageOperationResult result = await service.AddTemplateFileAsync(
            "edit-pkg", "x.java.scriban", "out/X.java", CancellationToken.None, scope: "wrong");

        Assert.Equal(TemplatePackageOperationStatus.Invalid, result.Status);
        Assert.Contains("table / module / project", result.Message);
    }

    /// <summary>
    /// P-C：oldText 恰命中一次时应替换成功，返回新内容摘要、字符数与替换点上下文快照。
    /// </summary>
    [Fact]
    public async Task EditTemplateContentAsync_UniqueMatch_ReplacesAndReturnsEvidence()
    {
        string userLibrary = Path.Combine(_tempRoot, "user-edit");
        TemplatePackageService service = CreateService(userLibrary);
        await CreateUserPackageAsync(userLibrary, "edit-pkg", content: "class Foo {}\nclass Bar {}\n");

        TemplatePackageOperationResult result = await service.EditTemplateContentAsync(
            "edit-pkg", "entity.java.scriban", "class Bar {}", "class Baz {}", null, CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Succeeded, result.Status);
        string updated = await File.ReadAllTextAsync(Path.Combine(userLibrary, "edit-pkg", "entity.java.scriban"));
        Assert.Equal("class Foo {}\nclass Baz {}\n", updated);
        // 上下文快照取自原文本的替换点边界：之前为类 A 行尾换行，之后为原文本末尾换行
        Assert.Equal("class Foo {}\n", result.ContextBefore);
        Assert.Equal("\n", result.ContextAfter);
        Assert.Equal(TemplateContentHash.Compute("class Foo {}\nclass Baz {}\n"), result.ContentHash);
        Assert.Equal("class Foo {}\nclass Baz {}\n".Length, result.CharCount);
    }

    /// <summary>
    /// P-C：oldText 多处命中时应歧义保护拒绝并在消息中报命中次数，原文零改动。
    /// </summary>
    [Fact]
    public async Task EditTemplateContentAsync_MultipleMatches_RejectedWithCount()
    {
        string userLibrary = Path.Combine(_tempRoot, "user-ambig");
        TemplatePackageService service = CreateService(userLibrary);
        string original = "class Foo {}\nclass Foo {}\n";
        await CreateUserPackageAsync(userLibrary, "edit-pkg", content: original);

        TemplatePackageOperationResult result = await service.EditTemplateContentAsync(
            "edit-pkg", "entity.java.scriban", "class Foo {}", "class Baz {}", null, CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Invalid, result.Status);
        Assert.Contains("2", result.Message);
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(userLibrary, "edit-pkg", "entity.java.scriban")));
    }

    /// <summary>
    /// P-C：oldText 零命中时应返回未找到（NotFound），原文零改动。
    /// </summary>
    [Fact]
    public async Task EditTemplateContentAsync_ZeroMatches_NotFound()
    {
        string userLibrary = Path.Combine(_tempRoot, "user-miss");
        TemplatePackageService service = CreateService(userLibrary);
        string original = "class Foo {}\n";
        await CreateUserPackageAsync(userLibrary, "edit-pkg", content: original);

        TemplatePackageOperationResult result = await service.EditTemplateContentAsync(
            "edit-pkg", "entity.java.scriban", "class Missing {}", "class Baz {}", null, CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.NotFound, result.Status);
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(userLibrary, "edit-pkg", "entity.java.scriban")));
    }

    /// <summary>
    /// P-C 行尾保真：CRLF 宿主 + LF 形态 oldText 应命中成功，newText 按宿主 CRLF 落地，其余字节零漂移。
    /// </summary>
    [Fact]
    public async Task EditTemplateContentAsync_CRLFHostWithLFInput_WritesHostLineEndings()
    {
        string userLibrary = Path.Combine(_tempRoot, "user-crlf");
        TemplatePackageService service = CreateService(userLibrary);
        string crlfOriginal = "class A {}\r\nclass B {}\r\n";
        string packageDir = await CreateUserPackageAsync(userLibrary, "edit-pkg");
        await File.WriteAllTextAsync(Path.Combine(packageDir, "entity.java.scriban"), crlfOriginal, new UTF8Encoding(false));

        TemplatePackageOperationResult result = await service.EditTemplateContentAsync(
            "edit-pkg", "entity.java.scriban", "class B {}\n", "class C {}\n", null, CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Succeeded, result.Status);
        byte[] updatedBytes = await File.ReadAllBytesAsync(Path.Combine(packageDir, "entity.java.scriban"));
        string updated = Encoding.UTF8.GetString(updatedBytes);
        Assert.Equal("class A {}\r\nclass C {}\r\n", updated);
        Assert.DoesNotContain("\n", updated.Replace("\r\n", string.Empty, StringComparison.Ordinal));
    }

    /// <summary>
    /// P-C 条件写乐观锁：期望摘要与当前正文不一致时应拒绝（ContentConflict）并回显当前摘要；
    /// 期望摘要一致时正常替换。
    /// </summary>
    [Fact]
    public async Task EditTemplateContentAsync_ExpectedHashMismatch_ConflictsWithCurrentHash()
    {
        string userLibrary = Path.Combine(_tempRoot, "user-hash");
        TemplatePackageService service = CreateService(userLibrary);
        string original = "class Foo {}\n";
        await CreateUserPackageAsync(userLibrary, "edit-pkg", content: original);
        string currentHash = TemplateContentHash.Compute(original);

        TemplatePackageOperationResult conflict = await service.EditTemplateContentAsync(
            "edit-pkg", "entity.java.scriban", "class Foo {}", "class Baz {}", "0123456789abcdef", CancellationToken.None);
        Assert.Equal(TemplatePackageOperationStatus.ContentConflict, conflict.Status);
        Assert.Contains(currentHash, conflict.Message);

        TemplatePackageOperationResult matched = await service.EditTemplateContentAsync(
            "edit-pkg", "entity.java.scriban", "class Foo {}", "class Baz {}", currentHash, CancellationToken.None);
        Assert.Equal(TemplatePackageOperationStatus.Succeeded, matched.Status);
        Assert.Equal("class Baz {}\n", await File.ReadAllTextAsync(Path.Combine(userLibrary, "edit-pkg", "entity.java.scriban")));
    }

    /// <summary>
    /// P-C 行尾保真：save_template_content 整篇覆写时 CRLF 宿主 + LF 传入内容应自动对齐为 CRLF 落盘。
    /// </summary>
    [Fact]
    public async Task SaveTemplateContentAsync_CRLFHostWithLFContent_AlignsToHostLineEndings()
    {
        string userLibrary = Path.Combine(_tempRoot, "user-save");
        TemplatePackageService service = CreateService(userLibrary);
        string packageDir = await CreateUserPackageAsync(userLibrary, "edit-pkg");
        await File.WriteAllTextAsync(
            Path.Combine(packageDir, "entity.java.scriban"), "line one\r\nline two\r\n", new UTF8Encoding(false));

        TemplatePackageOperationResult result = await service.SaveTemplateContentAsync(
            "edit-pkg", "entity.java.scriban", "alpha\nbeta\n", CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Succeeded, result.Status);
        string updated = await File.ReadAllTextAsync(Path.Combine(packageDir, "entity.java.scriban"));
        Assert.Equal("alpha\r\nbeta\r\n", updated);
    }

    /// <summary>
    /// P-C 行尾保真：空宿主文件（新建零字节）按传入内容原样落盘，不做行尾转换。
    /// </summary>
    [Fact]
    public async Task SaveTemplateContentAsync_EmptyHost_WritesContentAsIs()
    {
        string userLibrary = Path.Combine(_tempRoot, "user-empty");
        TemplatePackageService service = CreateService(userLibrary);
        string packageDir = await CreateUserPackageAsync(userLibrary, "edit-pkg", content: string.Empty);

        TemplatePackageOperationResult result = await service.SaveTemplateContentAsync(
            "edit-pkg", "entity.java.scriban", "x\ny\n", CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Succeeded, result.Status);
        Assert.Equal("x\ny\n", await File.ReadAllTextAsync(Path.Combine(packageDir, "entity.java.scriban")));
    }
}
