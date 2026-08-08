using System.Text;
using System.Text.Json;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;

namespace DbCodeGen.Core.Tests.Templates;

/// <summary>
/// TemplateFileWriter 读写单元测试，覆盖防目录穿越、内置包只读拒绝、Content 缓存失效与文件已更新事件。
/// </summary>
public sealed class TemplateFileWriterTests : IDisposable
{
    private readonly List<string> _tempRoots = new();
    private readonly TemplateFileWriter _writer = new();

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
    /// 读取应返回模板文本，并去除 UTF-8 BOM。
    /// </summary>
    [Fact]
    public async Task ReadAsync_ReturnsContentWithoutBom()
    {
        (TemplatePackageInfo package, _) = await CreateTempPackageAsync();
        string targetPath = Path.Combine(package.RootPath, "entity.java.scriban");
        await File.WriteAllTextAsync(targetPath, "class Demo {}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        string content = await _writer.ReadAsync(package, "entity.java.scriban", CancellationToken.None);

        Assert.Equal("class Demo {}", content);
    }

    /// <summary>
    /// 相对路径含 .. 段应被拒绝（目录穿越）。
    /// </summary>
    [Fact]
    public async Task ReadAsync_TraversalPath_Throws()
    {
        (TemplatePackageInfo package, _) = await CreateTempPackageAsync();

        await Assert.ThrowsAsync<TemplatePackageException>(
            () => _writer.ReadAsync(package, "../secret.txt", CancellationToken.None));
    }

    /// <summary>
    /// 绝对路径应被拒绝（目录穿越）。
    /// </summary>
    [Fact]
    public async Task ReadAsync_AbsolutePath_Throws()
    {
        (TemplatePackageInfo package, _) = await CreateTempPackageAsync();

        await Assert.ThrowsAsync<TemplatePackageException>(
            () => _writer.ReadAsync(package, @"C:\secret.txt", CancellationToken.None));
    }

    /// <summary>
    /// 写盘到用户包应成功，内容以 UTF-8 无 BOM 落盘并可读回。
    /// </summary>
    [Fact]
    public async Task WriteAsync_UserPackage_WritesContentWithoutBom()
    {
        (TemplatePackageInfo package, _) = await CreateTempPackageAsync();

        TemplateSaveResult result = await _writer.WriteAsync(package, "entity.java.scriban", "class Updated {}", CancellationToken.None);

        Assert.True(result.IsSuccess);
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(package.RootPath, "entity.java.scriban"));
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Equal("class Updated {}", await File.ReadAllTextAsync(Path.Combine(package.RootPath, "entity.java.scriban")));
    }

    /// <summary>
    /// 写盘到内置包应被只读拒绝，且不写任何文件。
    /// </summary>
    [Fact]
    public async Task WriteAsync_BuiltinPackage_RejectedReadOnly()
    {
        (TemplatePackageInfo package, _) = await CreateTempPackageAsync(isBuiltin: true);

        TemplateSaveResult result = await _writer.WriteAsync(package, "entity.java.scriban", "hack", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsReadOnlyBuiltin);
        Assert.Equal("class {{table.className}} {}", await File.ReadAllTextAsync(Path.Combine(package.RootPath, "entity.java.scriban")));
    }

    /// <summary>
    /// 相对路径越界写盘应被拒绝，返回路径穿越结果且不写文件。
    /// </summary>
    [Fact]
    public async Task WriteAsync_TraversalPath_Rejected()
    {
        (TemplatePackageInfo package, _) = await CreateTempPackageAsync();

        TemplateSaveResult result = await _writer.WriteAsync(package, "../evil.txt", "hack", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsPathTraversal);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(package.RootPath)!, "evil.txt")));
    }

    /// <summary>
    /// 写盘到子目录应自动创建父目录。
    /// </summary>
    [Fact]
    public async Task WriteAsync_SubDirectory_CreatesParentDirectory()
    {
        (TemplatePackageInfo package, _) = await CreateTempPackageAsync();

        TemplateSaveResult result = await _writer.WriteAsync(package, "sub/deep/entity.java.scriban", "nested", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(File.Exists(Path.Combine(package.RootPath, "sub", "deep", "entity.java.scriban")));
    }

    /// <summary>
    /// 写盘成功应失效对应模板文件的 Content 缓存。
    /// </summary>
    [Fact]
    public async Task WriteAsync_InvalidatesContentCache()
    {
        (TemplatePackageInfo package, _) = await CreateTempPackageAsync();
        TemplateFileInfo file = package.Files.Single(item => item.RelativeTemplatePath == "entity.java.scriban");
        file.Content = "旧内容";

        await _writer.WriteAsync(package, "entity.java.scriban", "新内容", CancellationToken.None);

        Assert.Null(file.Content);
    }

    /// <summary>
    /// 写盘成功应派发"文件已更新"事件，参数携带包名与规范化相对路径。
    /// </summary>
    [Fact]
    public async Task WriteAsync_FiresFileUpdatedEvent()
    {
        (TemplatePackageInfo package, _) = await CreateTempPackageAsync();
        TemplateFileWriter.TemplateFileChangedEventArgs? received = null;
        _writer.FileUpdated += (_, args) => received = args;

        await _writer.WriteAsync(package, "entity.java.scriban", "新内容", CancellationToken.None);

        Assert.NotNull(received);
        Assert.Equal("user-pkg", received.PackageName);
        Assert.Equal("entity.java.scriban", received.RelativePath);
    }

    /// <summary>
    /// 未配置 manifest 登记的相对路径写盘不应派发事件参数错误，Content 无对应缓存时静默跳过失效。
    /// </summary>
    [Fact]
    public async Task WriteAsync_UnregisteredFile_WritesAndFires()
    {
        (TemplatePackageInfo package, _) = await CreateTempPackageAsync();
        TemplateFileWriter.TemplateFileChangedEventArgs? received = null;
        _writer.FileUpdated += (_, args) => received = args;

        TemplateSaveResult result = await _writer.WriteAsync(package, "extra/file.txt", "text", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(received);
        Assert.Equal("extra/file.txt", received.RelativePath);
    }

    /// <summary>
    /// 在临时目录下创建含一个模板文件的合法测试包并加载。
    /// </summary>
    /// <param name="isBuiltin">是否标记为内置包。</param>
    /// <returns>加载后的模板包信息。</returns>
    private async Task<(TemplatePackageInfo Package, string TempRoot)> CreateTempPackageAsync(bool isBuiltin = false)
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
            Files = new List<TemplateFileEntry>
            {
                new() { Template = "entity.java.scriban", Output = "out/{{table.className}}.java", Enabled = true }
            }
        };

        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        await File.WriteAllTextAsync(Path.Combine(packageDir, TemplatePackageLoader.ManifestFileName), JsonSerializer.Serialize(manifest, jsonOptions));
        await File.WriteAllTextAsync(Path.Combine(packageDir, "entity.java.scriban"), "class {{table.className}} {}");

        TemplatePackageInfo package = await TemplatePackageLoader.LoadFromDirectoryAsync(packageDir, isBuiltin, CancellationToken.None);
        return (package, tempRoot);
    }
}
