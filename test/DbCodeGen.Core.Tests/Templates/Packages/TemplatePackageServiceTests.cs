using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text.Json;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Security;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbCodeGen.Core.Tests.Templates.Packages;

/// <summary>
/// TemplatePackageService 模板包管理服务单元测试，覆盖列表排序、zip/文件夹导入、复制、导出、删除、
/// 内置包只读边界、zip 防穿越与解压上限、临时目录清理等验收要点。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TemplatePackageServiceTests : IDisposable
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
    public TemplatePackageServiceTests()
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
    /// 在指定目录下创建含一个模板文件的合法测试包。
    /// </summary>
    /// <param name="parentDir">包父目录。</param>
    /// <param name="packageName">包名。</param>
    /// <param name="marker">写入模板文件的内容标记，用于验证内容是否被覆盖。</param>
    /// <returns>包目录绝对路径。</returns>
    private static async Task<string> CreatePackageAsync(string parentDir, string packageName, string marker = "public class {{table.className}} { }")
    {
        string packageDir = Path.Combine(parentDir, packageName);
        Directory.CreateDirectory(packageDir);

        var manifest = new TemplateManifest
        {
            Name = packageName,
            Description = "测试包",
            Engine = "scriban",
            BasePackage = "com.example",
            TypeMap = new Dictionary<string, string> { ["bigint"] = "Long" },
            Files = new List<TemplateFileEntry>
            {
                new()
                {
                    Template = "entity.java.scriban",
                    Output = "{{package.dir}}/entity/{{table.className}}.java",
                    Enabled = true
                }
            }
        };

        await File.WriteAllTextAsync(
            Path.Combine(packageDir, TemplatePackageLoader.ManifestFileName),
            JsonSerializer.Serialize(manifest, JsonOptions));
        await File.WriteAllTextAsync(Path.Combine(packageDir, "entity.java.scriban"), marker);
        return packageDir;
    }

    /// <summary>
    /// 将目录内容打包为 zip 文件。
    /// </summary>
    /// <param name="sourceDir">源目录。</param>
    /// <param name="zipPath">目标 zip 路径。</param>
    /// <returns>zip 绝对路径。</returns>
    private static async Task<string> CreateZipAsync(string sourceDir, string zipPath)
    {
        string? directory = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using FileStream zipStream = new(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using ZipArchive archive = new(zipStream, ZipArchiveMode.Create);
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
            ZipArchiveEntry entry = archive.CreateEntry(relative);
            await using Stream target = entry.Open();
            await using FileStream source = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            await source.CopyToAsync(target);
        }

        return zipPath;
    }

    /// <summary>
    /// 创建模板包服务实例：配置用户模板库指向 userLibraryRoot，内置包根指向 builtinRoot。
    /// </summary>
    /// <param name="builtinRoot">内置包根目录。</param>
    /// <param name="userLibraryRoot">用户模板库目录。</param>
    /// <param name="configService">输出的配置服务实例。</param>
    /// <param name="importTempRoot">输出的导入临时目录根。</param>
    /// <returns>模板包服务实例。</returns>
    private TemplatePackageService CreateService(string builtinRoot, string userLibraryRoot, out ConfigService configService, out string importTempRoot)
    {
        string configPath = Path.Combine(_tempRoot, $"config-{Guid.NewGuid():N}", "config.json");
        ConfigService config = new(_protector, NullLogger<ConfigService>.Instance, configPath);
        _configServices.Add(config);
        AppConfig appConfig = config.Load();
        appConfig.TemplateSearchDirectories.Clear();
        appConfig.TemplateSearchDirectories.Add(userLibraryRoot);
        config.Save();

        Directory.CreateDirectory(userLibraryRoot);
        importTempRoot = Path.Combine(_tempRoot, $"imports-{Guid.NewGuid():N}");
        TemplatePackageService service = new(config, NullLogger<TemplatePackageService>.Instance, builtinRoot, importTempRoot);
        _services.Add(service);
        configService = config;
        return service;
    }

    /// <summary>
    /// 列表应内置包优先、组内包名升序排序。
    /// </summary>
    [Fact]
    public async Task ListPackagesAsync_BuiltinFirstThenNameSorted()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        await CreatePackageAsync(builtinRoot, "zebra");
        await CreatePackageAsync(builtinRoot, "alpha");
        await CreatePackageAsync(userLibrary, "mid");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        IReadOnlyList<TemplatePackageInfo> packages = await service.ListPackagesAsync(CancellationToken.None);

        string[] names = packages.Select(package => package.Name).ToArray();
        Assert.Equal(new[] { "alpha", "zebra", "mid" }, names);
        Assert.True(packages[0].IsBuiltin);
        Assert.True(packages[1].IsBuiltin);
        Assert.False(packages[2].IsBuiltin);
    }

    /// <summary>
    /// 列表遇到损坏包应跳过该包，不中断整体列表。
    /// </summary>
    [Fact]
    public async Task ListPackagesAsync_CorruptPackage_IsSkipped()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        await CreatePackageAsync(builtinRoot, "good");
        Directory.CreateDirectory(Path.Combine(builtinRoot, "bad"));
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        IReadOnlyList<TemplatePackageInfo> packages = await service.ListPackagesAsync(CancellationToken.None);

        TemplatePackageInfo package = Assert.Single(packages);
        Assert.Equal("good", package.Name);
    }

    /// <summary>
    /// 合法 zip 导入应成功安装到用户模板库，返回 Succeeded 与新包信息。
    /// </summary>
    [Fact]
    public async Task ImportFromZipAsync_ValidZip_InstallsToUserLibrary()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        string sourceDir = await CreatePackageAsync(Path.Combine(_tempRoot, "src"), "imported-pkg");
        string zipPath = await CreateZipAsync(sourceDir, Path.Combine(_tempRoot, "import.zip"));
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult result = await service.ImportFromZipAsync(zipPath, overwrite: false, CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Succeeded, result.Status);
        Assert.NotNull(result.Package);
        Assert.Equal("imported-pkg", result.Package.Name);
        Assert.False(result.Package.IsBuiltin);
        Assert.True(File.Exists(Path.Combine(userLibrary, "imported-pkg", "template.json")));
        Assert.True(File.Exists(Path.Combine(userLibrary, "imported-pkg", "entity.java.scriban")));
    }

    /// <summary>
    /// 导入后临时目录应被清理，不留残留子目录。
    /// </summary>
    [Fact]
    public async Task ImportFromZipAsync_CleansUpTempDirectory()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        string sourceDir = await CreatePackageAsync(Path.Combine(_tempRoot, "src"), "temp-pkg");
        string zipPath = await CreateZipAsync(sourceDir, Path.Combine(_tempRoot, "temp.zip"));
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out string importTempRoot);

        await service.ImportFromZipAsync(zipPath, overwrite: false, CancellationToken.None);

        // 临时目录根不存在或为空，均视为已清理完成
        bool hasLeftover = Directory.Exists(importTempRoot) && Directory.EnumerateDirectories(importTempRoot).Any();
        Assert.False(hasLeftover, "导入临时目录存在残留子目录。");
    }

    /// <summary>
    /// zip 条目含 .. 段应被拒绝（zip slip），返回 Invalid。
    /// </summary>
    [Fact]
    public async Task ImportFromZipAsync_SlipEntry_ReturnsInvalid()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        string zipPath = Path.Combine(_tempRoot, "slip.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        using (FileStream zipStream = new(zipPath, FileMode.Create))
        using (ZipArchive archive = new(zipStream, ZipArchiveMode.Create))
        {
            ZipArchiveEntry manifestEntry = archive.CreateEntry("template.json");
            await using (Stream target = manifestEntry.Open())
            {
                await using StreamWriter writer = new(target);
                await writer.WriteAsync("""{"name":"slip","engine":"scriban","files":[{"template":"t.java.scriban","output":"out/{{table.className}}.java"}]}""");
            }

            archive.CreateEntry("../evil.txt");
        }

        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult result = await service.ImportFromZipAsync(zipPath, overwrite: false, CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Invalid, result.Status);
        Assert.Contains("..", result.Message);
    }

    /// <summary>
    /// zip 缺少 template.json 应返回 Invalid。
    /// </summary>
    [Fact]
    public async Task ImportFromZipAsync_MissingManifest_ReturnsInvalid()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        string zipPath = Path.Combine(_tempRoot, "no-manifest.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        using (FileStream zipStream = new(zipPath, FileMode.Create))
        using (ZipArchive archive = new(zipStream, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("foo.txt");
            await using Stream target = entry.Open();
            await target.WriteAsync("content"u8.ToArray());
        }

        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult result = await service.ImportFromZipAsync(zipPath, overwrite: false, CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Invalid, result.Status);
    }

    /// <summary>
    /// 导入包名与内置包同名应只读拒绝，overwrite=true 亦不生效。
    /// </summary>
    [Fact]
    public async Task ImportFromZipAsync_BuiltinConflict_RejectedEvenWithOverwrite()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        await CreatePackageAsync(builtinRoot, "java-mybatis-plus");
        string sourceDir = await CreatePackageAsync(Path.Combine(_tempRoot, "src"), "java-mybatis-plus");
        string zipPath = await CreateZipAsync(sourceDir, Path.Combine(_tempRoot, "builtin-conflict.zip"));
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult first = await service.ImportFromZipAsync(zipPath, overwrite: false, CancellationToken.None);
        TemplatePackageOperationResult second = await service.ImportFromZipAsync(zipPath, overwrite: true, CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.BuiltinConflict, first.Status);
        Assert.Equal(TemplatePackageOperationStatus.BuiltinConflict, second.Status);
        Assert.Empty(Directory.EnumerateDirectories(userLibrary));
    }

    /// <summary>
    /// 导入包名与用户包同名：未覆盖时返回 NameConflict，覆盖时成功替换旧包内容。
    /// </summary>
    [Fact]
    public async Task ImportFromZipAsync_UserConflict_NeedsOverwriteThenSucceeds()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        await CreatePackageAsync(userLibrary, "same-pkg", marker: "old content");
        string sourceDir = await CreatePackageAsync(Path.Combine(_tempRoot, "src"), "same-pkg", marker: "new content");
        string zipPath = await CreateZipAsync(sourceDir, Path.Combine(_tempRoot, "user-conflict.zip"));
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult conflict = await service.ImportFromZipAsync(zipPath, overwrite: false, CancellationToken.None);
        Assert.Equal(TemplatePackageOperationStatus.NameConflict, conflict.Status);

        TemplatePackageOperationResult overwritten = await service.ImportFromZipAsync(zipPath, overwrite: true, CancellationToken.None);
        Assert.Equal(TemplatePackageOperationStatus.Succeeded, overwritten.Status);
        string content = await File.ReadAllTextAsync(Path.Combine(userLibrary, "same-pkg", "entity.java.scriban"));
        Assert.Equal("new content", content);
    }

    /// <summary>
    /// zip 条目数超过上限应返回 Invalid（防 zip bomb）。
    /// </summary>
    [Fact]
    public async Task ImportFromZipAsync_EntryCountOverLimit_ReturnsInvalid()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        string zipPath = Path.Combine(_tempRoot, "too-many.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        using (FileStream zipStream = new(zipPath, FileMode.Create))
        using (ZipArchive archive = new(zipStream, ZipArchiveMode.Create))
        {
            for (int i = 0; i < TemplatePackageService.MaxZipEntries + 1; i++)
            {
                ZipArchiveEntry entry = archive.CreateEntry($"f{i}.txt");
                await using Stream target = entry.Open();
                await target.WriteAsync("x"u8.ToArray());
            }
        }

        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult result = await service.ImportFromZipAsync(zipPath, overwrite: false, CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Invalid, result.Status);
        Assert.Contains("条目数超过上限", result.Message);
    }

    /// <summary>
    /// zip 单条解压大小超过上限应返回 Invalid（防 zip bomb）。
    /// </summary>
    [Fact]
    public async Task ImportFromZipAsync_SingleEntryOverLimit_ReturnsInvalid()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        string zipPath = Path.Combine(_tempRoot, "big-entry.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        byte[] bigBytes = new byte[TemplatePackageService.MaxSingleEntryBytes + 1];
        using (FileStream zipStream = new(zipPath, FileMode.Create))
        using (ZipArchive archive = new(zipStream, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("big.bin");
            await using Stream target = entry.Open();
            await target.WriteAsync(bigBytes);
        }

        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult result = await service.ImportFromZipAsync(zipPath, overwrite: false, CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Invalid, result.Status);
        Assert.Contains("单条解压大小超过上限", result.Message);
    }

    /// <summary>
    /// zip 解压总大小超过上限应返回 Invalid（防 zip bomb）。
    /// </summary>
    [Fact]
    public async Task ImportFromZipAsync_TotalSizeOverLimit_ReturnsInvalid()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        string zipPath = Path.Combine(_tempRoot, "big-total.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        byte[] chunk = new byte[TemplatePackageService.MaxTotalBytes / 6 + 1];
        using (FileStream zipStream = new(zipPath, FileMode.Create))
        using (ZipArchive archive = new(zipStream, ZipArchiveMode.Create))
        {
            for (int i = 0; i < 6; i++)
            {
                ZipArchiveEntry entry = archive.CreateEntry($"chunk{i}.bin");
                await using Stream target = entry.Open();
                await target.WriteAsync(chunk);
            }
        }

        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult result = await service.ImportFromZipAsync(zipPath, overwrite: false, CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Invalid, result.Status);
        Assert.Contains("总大小超过上限", result.Message);
    }

    /// <summary>
    /// 合法文件夹导入应成功安装到用户模板库。
    /// </summary>
    [Fact]
    public async Task ImportFromFolderAsync_ValidFolder_InstallsToUserLibrary()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        string sourceDir = await CreatePackageAsync(Path.Combine(_tempRoot, "src"), "folder-pkg");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult result = await service.ImportFromFolderAsync(sourceDir, overwrite: false, CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Succeeded, result.Status);
        Assert.Equal("folder-pkg", result.Package!.Name);
        Assert.True(File.Exists(Path.Combine(userLibrary, "folder-pkg", "template.json")));
    }

    /// <summary>
    /// 导入的文件夹已在用户模板库时，overwrite=true 不应删除源目录，直接返回成功。
    /// </summary>
    [Fact]
    public async Task ImportFromFolderAsync_WhenFolderAlreadyInstalled_ReturnsSuccessWithoutCorruption()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        string installedDir = await CreatePackageAsync(userLibrary, "in-place");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult result = await service.ImportFromFolderAsync(installedDir, overwrite: true, CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Succeeded, result.Status);
        Assert.True(File.Exists(Path.Combine(installedDir, "template.json")));
    }

    /// <summary>
    /// 内置包复制到用户库应成功且转可读写，新包名被写入清单并重新校验。
    /// </summary>
    [Fact]
    public async Task CopyPackageAsync_BuiltinToUser_ReturnsEditableCopy()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        await CreatePackageAsync(builtinRoot, "builtin-pkg");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult result = await service.CopyPackageAsync("builtin-pkg", "my-copy", overwrite: false, CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Succeeded, result.Status);
        TemplatePackageInfo copied = result.Package!;
        Assert.Equal("my-copy", copied.Name);
        Assert.False(copied.IsBuiltin);
        string copiedManifest = await File.ReadAllTextAsync(Path.Combine(userLibrary, "my-copy", "template.json"));
        Assert.Contains("\"name\"", copiedManifest);
        Assert.Contains("my-copy", copiedManifest);
        Assert.True(File.Exists(Path.Combine(userLibrary, "my-copy", "entity.java.scriban")));
        Assert.False(Directory.Exists(Path.Combine(userLibrary, "builtin-pkg")));
    }

    /// <summary>
    /// 复制新包名与内置包同名应只读拒绝，overwrite=true 亦不生效。
    /// </summary>
    [Fact]
    public async Task CopyPackageAsync_NewNameMatchesBuiltin_RejectedEvenWithOverwrite()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        await CreatePackageAsync(builtinRoot, "builtin-pkg");
        await CreatePackageAsync(builtinRoot, "java-mybatis-plus");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult result = await service.CopyPackageAsync("builtin-pkg", "java-mybatis-plus", overwrite: true, CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.BuiltinConflict, result.Status);
    }

    /// <summary>
    /// 复制新包名非法应返回 Invalid。
    /// </summary>
    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a b")]
    public async Task CopyPackageAsync_InvalidNewName_ReturnsInvalid(string newName)
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        await CreatePackageAsync(builtinRoot, "builtin-pkg");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult result = await service.CopyPackageAsync("builtin-pkg", newName, overwrite: false, CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Invalid, result.Status);
    }

    /// <summary>
    /// 复制新包名与用户包同名：未覆盖时返回 NameConflict，覆盖时成功。
    /// </summary>
    [Fact]
    public async Task CopyPackageAsync_UserConflict_NeedsOverwriteThenSucceeds()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        await CreatePackageAsync(builtinRoot, "builtin-pkg");
        await CreatePackageAsync(userLibrary, "existing");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult conflict = await service.CopyPackageAsync("builtin-pkg", "existing", overwrite: false, CancellationToken.None);
        Assert.Equal(TemplatePackageOperationStatus.NameConflict, conflict.Status);

        TemplatePackageOperationResult overwritten = await service.CopyPackageAsync("builtin-pkg", "existing", overwrite: true, CancellationToken.None);
        Assert.Equal(TemplatePackageOperationStatus.Succeeded, overwritten.Status);
        Assert.Equal("existing", overwritten.Package!.Name);
    }

    /// <summary>
    /// 用户包复制为自身同名：overwrite=true 也不应删除源包目录，直接返回成功。
    /// </summary>
    [Fact]
    public async Task CopyPackageAsync_UserSameName_DoesNotDeleteSource()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        await CreatePackageAsync(userLibrary, "same-name");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult result = await service.CopyPackageAsync("same-name", "same-name", overwrite: true, CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Succeeded, result.Status);
        Assert.True(File.Exists(Path.Combine(userLibrary, "same-name", "template.json")));
        Assert.True(File.Exists(Path.Combine(userLibrary, "same-name", "entity.java.scriban")));
    }

    /// <summary>
    /// 导入失败（zip slip 被拒绝）后临时目录也应被 finally 清理，不留残留。
    /// </summary>
    [Fact]
    public async Task ImportFromZipAsync_FailurePath_CleansUpTempDirectory()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        string zipPath = Path.Combine(_tempRoot, "slip2.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        using (FileStream zipStream = new(zipPath, FileMode.Create))
        using (ZipArchive archive = new(zipStream, ZipArchiveMode.Create))
        {
            ZipArchiveEntry manifestEntry = archive.CreateEntry("template.json");
            await using (Stream target = manifestEntry.Open())
            {
                await using StreamWriter writer = new(target);
                await writer.WriteAsync("""{"name":"slip","engine":"scriban","files":[{"template":"t.java.scriban","output":"out/{{table.className}}.java"}]}""");
            }

            archive.CreateEntry("../evil.txt");
        }

        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out string importTempRoot);

        TemplatePackageOperationResult result = await service.ImportFromZipAsync(zipPath, overwrite: false, CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Invalid, result.Status);
        bool hasLeftover = Directory.Exists(importTempRoot) && Directory.EnumerateDirectories(importTempRoot).Any();
        Assert.False(hasLeftover, "导入失败后临时目录存在残留子目录。");
    }

    /// <summary>
    /// 导出 zip 应包含 template.json 与包内全部模板文件。
    /// </summary>
    [Fact]
    public async Task ExportToZipAsync_ExportsManifestAndFiles()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        await CreatePackageAsync(builtinRoot, "builtin-pkg");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);
        string targetZip = Path.Combine(_tempRoot, "exported.zip");

        string result = await service.ExportToZipAsync("builtin-pkg", targetZip, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(targetZip), result);
        Assert.True(File.Exists(targetZip));
        using FileStream zipStream = new(targetZip, FileMode.Open, FileAccess.Read);
        using ZipArchive archive = new(zipStream, ZipArchiveMode.Read);
        string[] names = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("template.json", names);
        Assert.Contains("entity.java.scriban", names);
    }

    /// <summary>
    /// 导出的 zip 应能被本服务重新导入到用户模板库。
    /// </summary>
    [Fact]
    public async Task ExportToZipAsync_ExportedZipCanBeImported()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        await CreatePackageAsync(userLibrary, "roundtrip");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);
        string targetZip = Path.Combine(_tempRoot, "roundtrip.zip");
        await service.ExportToZipAsync("roundtrip", targetZip, CancellationToken.None);

        // 删除原用户包后再导入导出的 zip，验证可重新安装
        await service.DeletePackageAsync("roundtrip", CancellationToken.None);
        TemplatePackageOperationResult imported = await service.ImportFromZipAsync(targetZip, overwrite: false, CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Succeeded, imported.Status);
        Assert.Equal("roundtrip", imported.Package!.Name);
        Assert.True(File.Exists(Path.Combine(userLibrary, "roundtrip", "template.json")));
    }

    /// <summary>
    /// 删除用户包应物理删除包目录。
    /// </summary>
    [Fact]
    public async Task DeletePackageAsync_UserPackage_DeletesDirectory()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        await CreatePackageAsync(userLibrary, "todelete");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        await service.DeletePackageAsync("todelete", CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(userLibrary, "todelete")));
    }

    /// <summary>
    /// 删除内置包应抛只读异常。
    /// </summary>
    [Fact]
    public async Task DeletePackageAsync_BuiltinPackage_Throws()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        await CreatePackageAsync(builtinRoot, "builtin-pkg");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageException exception = await Assert.ThrowsAsync<TemplatePackageException>(
            () => service.DeletePackageAsync("builtin-pkg", CancellationToken.None));

        Assert.Contains("只读", exception.Message);
        Assert.True(Directory.Exists(Path.Combine(builtinRoot, "builtin-pkg")));
    }

    /// <summary>
    /// 删除不存在的包应抛异常。
    /// </summary>
    [Fact]
    public async Task DeletePackageAsync_NotFound_Throws()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        await Assert.ThrowsAsync<TemplatePackageException>(
            () => service.DeletePackageAsync("nope", CancellationToken.None));
    }

    /// <summary>
    /// 单包加载内置包应标记只读，加载用户包应可读写。
    /// </summary>
    [Fact]
    public async Task LoadPackageAsync_BuiltinAndUser_RespectsReadonlyFlag()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        await CreatePackageAsync(builtinRoot, "builtin-pkg");
        await CreatePackageAsync(userLibrary, "user-pkg");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageInfo builtin = await service.LoadPackageAsync("builtin-pkg", CancellationToken.None);
        TemplatePackageInfo user = await service.LoadPackageAsync("user-pkg", CancellationToken.None);

        Assert.True(builtin.IsBuiltin);
        Assert.False(user.IsBuiltin);
    }

    /// <summary>
    /// 单包加载不存在的包应抛异常。
    /// </summary>
    [Fact]
    public async Task LoadPackageAsync_NotFound_Throws()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        await Assert.ThrowsAsync<TemplatePackageException>(
            () => service.LoadPackageAsync("missing", CancellationToken.None));
    }

    /// <summary>
    /// 新建包合法输入应创建包目录、清单与首模板空文件，并可由服务重新加载。
    /// </summary>
    [Fact]
    public async Task CreatePackageAsync_ValidInput_CreatesLoadablePackage()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult result = await service.CreatePackageAsync(
            "new-pkg", "新包说明", "entity/main.tpl", "out/{{table.className}}.java", CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Succeeded, result.Status);
        Assert.NotNull(result.Package);
        Assert.Equal("new-pkg", result.Package.Name);
        Assert.False(result.Package.IsBuiltin);
        TemplateFileInfo file = Assert.Single(result.Package.Files);
        Assert.Equal("entity/main.tpl", file.RelativeTemplatePath);
        Assert.True(File.Exists(Path.Combine(userLibrary, "new-pkg", TemplatePackageLoader.ManifestFileName)));
        Assert.True(File.Exists(Path.Combine(userLibrary, "new-pkg", "entity", "main.tpl")));

        TemplatePackageInfo reloaded = await service.LoadPackageAsync("new-pkg", CancellationToken.None);
        Assert.Equal("scriban", reloaded.Engine);
        Assert.Equal("新包说明", reloaded.Description);
    }

    /// <summary>
    /// 新建包名与内置包同名应只读拒绝，且不在用户库创建任何内容。
    /// </summary>
    [Fact]
    public async Task CreatePackageAsync_BuiltinName_Rejected()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        await CreatePackageAsync(builtinRoot, "java-mybatis-plus");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult result = await service.CreatePackageAsync(
            "java-mybatis-plus", "说明", "main.tpl", "main.tpl", CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.BuiltinConflict, result.Status);
        Assert.Empty(Directory.EnumerateDirectories(userLibrary));
    }

    /// <summary>
    /// 新建包名与用户包同名应返回 NameConflict，新建不走覆盖，用户库内容保持不变。
    /// </summary>
    [Fact]
    public async Task CreatePackageAsync_UserConflict_ReturnsNameConflict()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        await CreatePackageAsync(userLibrary, "same-pkg");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult result = await service.CreatePackageAsync(
            "same-pkg", "说明", "main.tpl", "main.tpl", CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.NameConflict, result.Status);
        Assert.True(File.Exists(Path.Combine(userLibrary, "same-pkg", "entity.java.scriban")));
    }

    /// <summary>
    /// 新建包名非法应返回 Invalid。
    /// </summary>
    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("")]
    public async Task CreatePackageAsync_InvalidName_ReturnsInvalid(string packageName)
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult result = await service.CreatePackageAsync(
            packageName, "说明", "main.tpl", "main.tpl", CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Invalid, result.Status);
    }

    /// <summary>
    /// 新建包首模板路径含目录穿越或绝对路径应返回 Invalid，且不创建包目录。
    /// </summary>
    [Theory]
    [InlineData("../evil.tpl")]
    [InlineData("a/../../evil.tpl")]
    [InlineData("C:\\windows\\main.tpl")]
    [InlineData("\\server\\share\\main.tpl")]
    public async Task CreatePackageAsync_PathTraversal_ReturnsInvalid(string templatePath)
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult result = await service.CreatePackageAsync(
            "safe-pkg", "说明", templatePath, "main.tpl", CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Invalid, result.Status);
        Assert.False(Directory.Exists(Path.Combine(userLibrary, "safe-pkg")));
    }

    /// <summary>
    /// 新建包首模板路径含 Windows 非法文件名字符应返回 Invalid，且不创建包目录、不落盘脏数据。
    /// </summary>
    [Theory]
    [InlineData("main|bad.tpl")]
    [InlineData("main*bad.tpl")]
    [InlineData("main?bad.tpl")]
    [InlineData("bad\"name.tpl")]
    public async Task CreatePackageAsync_InvalidFileNameChars_ReturnsInvalid(string templatePath)
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult result = await service.CreatePackageAsync(
            "safe-pkg", "说明", templatePath, "main.tpl", CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Invalid, result.Status);
        Assert.False(Directory.Exists(Path.Combine(userLibrary, "safe-pkg")));
    }

    /// <summary>
    /// 新增文件路径含 Windows 非法文件名字符应返回 Invalid，且不创建任何文件。
    /// </summary>
    [Fact]
    public async Task AddTemplateFileAsync_InvalidFileNameChars_ReturnsInvalid()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);
        await service.CreatePackageAsync("pkg", "", "main.tpl", "main.tpl", CancellationToken.None);

        TemplatePackageOperationResult result = await service.AddTemplateFileAsync(
            "pkg", "group/pojo|bad.tpl", "out.tpl", CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Invalid, result.Status);
        Assert.False(File.Exists(Path.Combine(userLibrary, "pkg", "pojo|bad.tpl")));
    }

    /// <summary>
    /// 新增文件（含分组目录）应创建文件并同步追加 manifest 条目，重新加载后清单与磁盘一致。
    /// </summary>
    [Fact]
    public async Task AddTemplateFileAsync_WithGroupDirectory_SyncsManifestAndCreatesFile()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);
        await service.CreatePackageAsync("multi", "", "main.tpl", "main.tpl", CancellationToken.None);

        TemplatePackageOperationResult added = await service.AddTemplateFileAsync(
            "multi", "entity/pojo.tpl", "entity/{{table.className}}.java", CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Succeeded, added.Status);
        Assert.Equal(2, added.Package!.Files.Count);
        Assert.Contains(added.Package.Files, file => file.RelativeTemplatePath == "entity/pojo.tpl");
        Assert.True(File.Exists(Path.Combine(userLibrary, "multi", "entity", "pojo.tpl")));

        TemplatePackageInfo reloaded = await service.LoadPackageAsync("multi", CancellationToken.None);
        Assert.Equal(2, reloaded.Files.Count);
        Assert.Contains(reloaded.Files, file => file.RelativeTemplatePath == "entity/pojo.tpl");
    }

    /// <summary>
    /// 新增文件目标已存在应返回失败，且 manifest 不追加重复条目。
    /// </summary>
    [Fact]
    public async Task AddTemplateFileAsync_ExistingFile_ReturnsFailure()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);
        await service.CreatePackageAsync("ex", "", "main.tpl", "main.tpl", CancellationToken.None);

        TemplatePackageOperationResult result = await service.AddTemplateFileAsync(
            "ex", "main.tpl", "main.tpl", CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Failed, result.Status);
        TemplatePackageInfo reloaded = await service.LoadPackageAsync("ex", CancellationToken.None);
        Assert.Single(reloaded.Files);
    }

    /// <summary>
    /// 新增文件路径含目录穿越应返回 Invalid，且不创建任何文件。
    /// </summary>
    [Fact]
    public async Task AddTemplateFileAsync_PathTraversal_ReturnsInvalid()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);
        await service.CreatePackageAsync("pkg", "", "main.tpl", "main.tpl", CancellationToken.None);

        TemplatePackageOperationResult result = await service.AddTemplateFileAsync(
            "pkg", "../evil.tpl", "out.tpl", CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Invalid, result.Status);
        Assert.False(File.Exists(Path.Combine(userLibrary, "pkg", "evil.tpl")));
    }

    /// <summary>
    /// 删除文件应删除磁盘文件并同步移除 manifest 条目，重新加载后清单与磁盘一致。
    /// </summary>
    [Fact]
    public async Task DeleteTemplateFileAsync_RemovesFileAndManifestEntry()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);
        await service.CreatePackageAsync("del", "", "main.tpl", "main.tpl", CancellationToken.None);
        await service.AddTemplateFileAsync("del", "second.tpl", "second.tpl", CancellationToken.None);

        TemplatePackageOperationResult result = await service.DeleteTemplateFileAsync("del", "main.tpl", CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Succeeded, result.Status);
        TemplateFileInfo remaining = Assert.Single(result.Package!.Files);
        Assert.Equal("second.tpl", remaining.RelativeTemplatePath);
        Assert.False(File.Exists(Path.Combine(userLibrary, "del", "main.tpl")));
        Assert.True(File.Exists(Path.Combine(userLibrary, "del", "second.tpl")));

        TemplatePackageInfo reloaded = await service.LoadPackageAsync("del", CancellationToken.None);
        Assert.Single(reloaded.Files);
        Assert.Equal("second.tpl", reloaded.Files[0].RelativeTemplatePath);
    }

    /// <summary>
    /// 删除包内最后一个文件应返回失败，文件与清单保持不变，防清单 files 为空校验失败。
    /// </summary>
    [Fact]
    public async Task DeleteTemplateFileAsync_LastFile_Rejected()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);
        await service.CreatePackageAsync("single", "", "main.tpl", "main.tpl", CancellationToken.None);

        TemplatePackageOperationResult result = await service.DeleteTemplateFileAsync("single", "main.tpl", CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Failed, result.Status);
        Assert.True(File.Exists(Path.Combine(userLibrary, "single", "main.tpl")));
    }

    /// <summary>
    /// 删除不存在的文件应返回失败。
    /// </summary>
    [Fact]
    public async Task DeleteTemplateFileAsync_NotExist_ReturnsFailure()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);
        await service.CreatePackageAsync("pkg", "", "main.tpl", "main.tpl", CancellationToken.None);

        TemplatePackageOperationResult result = await service.DeleteTemplateFileAsync("pkg", "missing.tpl", CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.Failed, result.Status);
    }

    /// <summary>
    /// 内置包新增文件应只读拒绝。
    /// </summary>
    [Fact]
    public async Task AddTemplateFileAsync_Builtin_Rejected()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        await CreatePackageAsync(builtinRoot, "builtin-pkg");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult result = await service.AddTemplateFileAsync(
            "builtin-pkg", "new.tpl", "new.tpl", CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.BuiltinConflict, result.Status);
        Assert.False(File.Exists(Path.Combine(builtinRoot, "builtin-pkg", "new.tpl")));
    }

    /// <summary>
    /// 内置包删除文件应只读拒绝。
    /// </summary>
    [Fact]
    public async Task DeleteTemplateFileAsync_Builtin_Rejected()
    {
        string builtinRoot = Path.Combine(_tempRoot, "builtin");
        string userLibrary = Path.Combine(_tempRoot, "user");
        await CreatePackageAsync(builtinRoot, "builtin-pkg");
        TemplatePackageService service = CreateService(builtinRoot, userLibrary, out _, out _);

        TemplatePackageOperationResult result = await service.DeleteTemplateFileAsync(
            "builtin-pkg", "entity.java.scriban", CancellationToken.None);

        Assert.Equal(TemplatePackageOperationStatus.BuiltinConflict, result.Status);
        Assert.True(File.Exists(Path.Combine(builtinRoot, "builtin-pkg", "entity.java.scriban")));
    }
}
