using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using DbCodeGen.Core.BackupRestore;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Security;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbCodeGen.Core.Tests.BackupRestore;

/// <summary>
/// BackupRestoreService 备份/恢复服务单元测试，覆盖用户包打包、配置脱敏、备份文件校验
/// （版本/格式/目录穿越/zip bomb 上限）、用户包冲突确认与配置还原等验收要点。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BackupRestoreServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly CredentialProtector _protector = new();
    private readonly List<IDisposable> _disposables = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 测试用服务环境：配置服务、模板包服务、备份/恢复服务及独立目录根。
    /// </summary>
    private sealed class Environment
    {
        /// <summary>
        /// 配置服务实例。
        /// </summary>
        public ConfigService ConfigService { get; init; } = null!;

        /// <summary>
        /// 模板包服务实例。
        /// </summary>
        public TemplatePackageService TemplateService { get; init; } = null!;

        /// <summary>
        /// 备份/恢复服务实例。
        /// </summary>
        public BackupRestoreService BackupService { get; init; } = null!;

        /// <summary>
        /// 用户模板库目录。
        /// </summary>
        public string UserLibrary { get; init; } = string.Empty;

        /// <summary>
        /// 内置包根目录。
        /// </summary>
        public string BuiltinRoot { get; init; } = string.Empty;

        /// <summary>
        /// 默认模板库目录。
        /// </summary>
        public string DefaultTemplateDirectory { get; init; } = string.Empty;

        /// <summary>
        /// 恢复临时目录根。
        /// </summary>
        public string RestoreTempRoot { get; init; } = string.Empty;
    }

    /// <summary>
    /// 为每个测试实例创建独立临时目录，避免用例间备份文件与模板库互相污染。
    /// </summary>
    public BackupRestoreServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// 释放登记的全部服务并递归删除临时目录。
    /// </summary>
    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }

        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// 创建独立测试环境：全新配置、内置包根、用户库与默认模板库均指向临时目录。
    /// </summary>
    /// <returns>测试环境。</returns>
    private Environment CreateEnvironment()
    {
        string configPath = Path.Combine(_tempRoot, $"config-{Guid.NewGuid():N}", "config.json");
        ConfigService configService = new(_protector, NullLogger<ConfigService>.Instance, configPath);
        _disposables.Add(configService);

        string builtinRoot = Path.Combine(_tempRoot, $"builtin-{Guid.NewGuid():N}");
        string userLibrary = Path.Combine(_tempRoot, $"user-{Guid.NewGuid():N}");
        Directory.CreateDirectory(builtinRoot);
        Directory.CreateDirectory(userLibrary);

        AppConfig appConfig = configService.Load();
        appConfig.TemplateSearchDirectories.Clear();
        appConfig.TemplateSearchDirectories.Add(userLibrary);
        configService.Save();

        TemplatePackageService templateService = new(
            configService,
            NullLogger<TemplatePackageService>.Instance,
            builtinRoot,
            Path.Combine(_tempRoot, $"imports-{Guid.NewGuid():N}"));
        _disposables.Add(templateService);

        string defaultTemplateDirectory = Path.Combine(_tempRoot, $"default-templates-{Guid.NewGuid():N}");
        string restoreTempRoot = Path.Combine(_tempRoot, $"restores-{Guid.NewGuid():N}");
        BackupRestoreService backupService = new(
            configService,
            templateService,
            NullLogger<BackupRestoreService>.Instance,
            restoreTempRoot,
            defaultTemplateDirectory);
        _disposables.Add(backupService);

        return new Environment
        {
            ConfigService = configService,
            TemplateService = templateService,
            BackupService = backupService,
            UserLibrary = userLibrary,
            BuiltinRoot = builtinRoot,
            DefaultTemplateDirectory = defaultTemplateDirectory,
            RestoreTempRoot = restoreTempRoot
        };
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
    /// 手工构造 .dbcg 备份文件：写入 manifest.json 与 templates/&lt;相对路径&gt; 条目，用于负向用例构造。
    /// </summary>
    /// <param name="zipPath">目标备份文件路径。</param>
    /// <param name="manifest">备份清单。</param>
    /// <param name="packageFiles">模板包条目相对路径到内容的映射。</param>
    private static async Task WriteDbcgAsync(string zipPath, BackupManifest manifest, IReadOnlyDictionary<string, string> packageFiles)
    {
        string? directory = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using FileStream zipStream = new(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using ZipArchive archive = new(zipStream, ZipArchiveMode.Create);

        ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json");
        await using (Stream manifestTarget = manifestEntry.Open())
        {
            byte[] manifestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, JsonOptions));
            await manifestTarget.WriteAsync(manifestBytes);
        }

        foreach (KeyValuePair<string, string> pair in packageFiles)
        {
            ZipArchiveEntry entry = archive.CreateEntry($"templates/{pair.Key}");
            await using Stream entryStream = entry.Open();
            byte[] content = Encoding.UTF8.GetBytes(pair.Value);
            await entryStream.WriteAsync(content);
        }
    }

    /// <summary>
    /// 读取 zip 内 manifest.json 条目文本。
    /// </summary>
    /// <param name="archive">打开的 zip 归档。</param>
    /// <returns>manifest.json 文本内容。</returns>
    private static async Task<string> ReadManifestTextAsync(ZipArchive archive)
    {
        ZipArchiveEntry entry = archive.GetEntry("manifest.json")!;
        await using Stream stream = entry.Open();
        using StreamReader reader = new(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// 备份只打包用户包为 templates/&lt;包名&gt;/… 结构，内置包不进入备份文件。
    /// </summary>
    [Fact]
    public async Task CreateBackupAsync_UserPackagesOnly_WritesDbcgWithNoBuiltinPackages()
    {
        Environment env = CreateEnvironment();
        await CreatePackageAsync(env.BuiltinRoot, "builtin-pkg");
        await CreatePackageAsync(env.UserLibrary, "user-pkg");
        string targetZip = Path.Combine(_tempRoot, "backup.dbcg");

        BackupResult result = await env.BackupService.CreateBackupAsync(targetZip, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(targetZip), result.BackupFilePath);
        Assert.Equal(1, result.UserPackageCount);
        Assert.Equal(new[] { "user-pkg" }, result.PackageNames);
        Assert.True(File.Exists(targetZip));

        using FileStream zipStream = new(targetZip, FileMode.Open, FileAccess.Read);
        using ZipArchive archive = new(zipStream, ZipArchiveMode.Read);
        string[] names = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("manifest.json", names);
        Assert.Contains("templates/user-pkg/template.json", names);
        Assert.Contains("templates/user-pkg/entity.java.scriban", names);
        Assert.DoesNotContain(names, name => name.StartsWith("templates/builtin-pkg/", StringComparison.Ordinal));
    }

    /// <summary>
    /// 备份目标目录不存在时应自动创建。
    /// </summary>
    [Fact]
    public async Task CreateBackupAsync_CreatesTargetDirectory()
    {
        Environment env = CreateEnvironment();
        await CreatePackageAsync(env.UserLibrary, "pkg");
        string nestedPath = Path.Combine(_tempRoot, "nested", "sub", "backup.dbcg");

        BackupResult result = await env.BackupService.CreateBackupAsync(nestedPath, CancellationToken.None);

        Assert.True(File.Exists(nestedPath));
        Assert.Equal(Path.GetFullPath(nestedPath), result.BackupFilePath);
    }

    /// <summary>
    /// 备份文件的配置快照应剔除密码与 apiKey 密文：文件文本与 manifest 均不含明文或密文字段。
    /// </summary>
    [Fact]
    public async Task CreateBackupAsync_ConfigSnapshotSanitizesSecrets()
    {
        Environment env = CreateEnvironment();
        await CreatePackageAsync(env.UserLibrary, "user-pkg");

        const string plainPassword = "p@ssw0rd";
        const string plainApiKey = "sk-secret-key";
        AppConfig config = env.ConfigService.Load();
        config.Llm.ApiKeyEncrypted = _protector.Encrypt(plainApiKey);
        config.DataSources.Add(new DataSourceConfig
        {
            Name = "prod",
            Type = DataSourceType.MySql,
            Host = "db.example.com",
            Port = 3306,
            Database = "shop",
            UserId = "root",
            PasswordCipher = _protector.Encrypt(plainPassword),
            CreatedAt = new DateTime(2026, 1, 1, 8, 0, 0),
            UpdatedAt = new DateTime(2026, 1, 1, 8, 0, 0)
        });
        env.ConfigService.Save();

        string targetZip = Path.Combine(_tempRoot, "secrets.dbcg");
        await env.BackupService.CreateBackupAsync(targetZip, CancellationToken.None);

        // 备份文件整体字节与 manifest 文本均不得出现明文或密文
        byte[] fileBytes = await File.ReadAllBytesAsync(targetZip);
        string fileText = Encoding.UTF8.GetString(fileBytes);
        Assert.DoesNotContain(plainPassword, fileText);
        Assert.DoesNotContain(plainApiKey, fileText);

        using FileStream zipStream = new(targetZip, FileMode.Open, FileAccess.Read);
        using ZipArchive archive = new(zipStream, ZipArchiveMode.Read);
        string manifestJson = await ReadManifestTextAsync(archive);
        Assert.DoesNotContain(plainPassword, manifestJson);
        Assert.DoesNotContain(plainApiKey, manifestJson);
        Assert.DoesNotContain("passwordCipher", manifestJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKeyEncrypted", manifestJson, StringComparison.OrdinalIgnoreCase);

        BackupManifest manifest = JsonSerializer.Deserialize<BackupManifest>(manifestJson, JsonOptions)!;
        Assert.Single(manifest.PackageNames);
        Assert.Equal("user-pkg", manifest.PackageNames[0]);
        BackupManifestConfig.DataSourceSnapshot dataSource = Assert.Single(manifest.Config.DataSources);
        Assert.Equal("prod", dataSource.Name);
        Assert.Equal(DataSourceType.MySql, dataSource.Type);
        Assert.True(dataSource.PasswordConfigured);
        Assert.True(manifest.Config.LlmApiKeyConfigured);
    }

    /// <summary>
    /// 合法备份应完整还原：用户包落到默认模板库、非密配置字段还原、密码与 apiKey 密文清空，
    /// 并返回需重输密码的数据源名与需重配 LLM 标记。
    /// </summary>
    [Fact]
    public async Task RestoreBackupAsync_ValidBackup_RestoresPackagesAndConfig()
    {
        Environment source = CreateEnvironment();
        await CreatePackageAsync(source.UserLibrary, "migrate-pkg", marker: "new content");
        const string plainPassword = "s3cret";
        AppConfig sourceConfig = source.ConfigService.Load();
        sourceConfig.WorkspaceRoot = @"C:\workspace\old";
        sourceConfig.LastRelativeOutputRoot = "src/main";
        sourceConfig.Llm.BaseUrl = "https://custom.example.com/v1";
        sourceConfig.Llm.Model = "qwen-max";
        sourceConfig.Llm.ApiKeyEncrypted = _protector.Encrypt("sk-abc");
        sourceConfig.DataSources.Add(new DataSourceConfig
        {
            Name = "prod",
            Type = DataSourceType.PostgreSql,
            Host = "db.example.com",
            Port = 5432,
            Database = "shop",
            UserId = "app",
            PasswordCipher = _protector.Encrypt(plainPassword),
            CreatedAt = new DateTime(2026, 2, 1, 8, 0, 0),
            UpdatedAt = new DateTime(2026, 2, 1, 8, 0, 0)
        });
        sourceConfig.TypeMappings.Clear();
        sourceConfig.TypeMappings.Add(new TypeMappingEntry { DbType = "jsonb", TargetType = "String", Remark = "自定义映射" });
        source.ConfigService.Save();

        string backupZip = Path.Combine(_tempRoot, "migrate.dbcg");
        await source.BackupService.CreateBackupAsync(backupZip, CancellationToken.None);

        Environment target = CreateEnvironment();
        RestoreResult result = await target.BackupService.RestoreBackupAsync(backupZip, overwriteUserPackages: false, CancellationToken.None);

        Assert.False(result.NeedsConfirmation);
        Assert.Equal(new[] { "migrate-pkg" }, result.RestoredPackageNames);
        Assert.Equal(new[] { "prod" }, result.PasswordRequiredDataSources);
        Assert.True(result.LlmNeedsReconfigure);

        // 用户包已还原到默认模板库目录，内容与源一致
        Assert.True(File.Exists(Path.Combine(target.DefaultTemplateDirectory, "migrate-pkg", "template.json")));
        Assert.True(File.Exists(Path.Combine(target.DefaultTemplateDirectory, "migrate-pkg", "entity.java.scriban")));
        string restoredContent = await File.ReadAllTextAsync(Path.Combine(target.DefaultTemplateDirectory, "migrate-pkg", "entity.java.scriban"));
        Assert.Equal("new content", restoredContent);

        // 非密配置字段已还原，默认模板库目录已并入模板搜索目录
        AppConfig restoredConfig = target.ConfigService.Current;
        Assert.Equal(@"C:\workspace\old", restoredConfig.WorkspaceRoot);
        Assert.Equal("src/main", restoredConfig.LastRelativeOutputRoot);
        Assert.Equal("https://custom.example.com/v1", restoredConfig.Llm.BaseUrl);
        Assert.Equal("qwen-max", restoredConfig.Llm.Model);
        Assert.Contains(source.UserLibrary, restoredConfig.TemplateSearchDirectories);
        Assert.Contains(target.DefaultTemplateDirectory, restoredConfig.TemplateSearchDirectories);

        // 数据源非密字段已还原，密码密文已清空
        DataSourceConfig restoredDataSource = Assert.Single(restoredConfig.DataSources);
        Assert.Equal("prod", restoredDataSource.Name);
        Assert.Equal(DataSourceType.PostgreSql, restoredDataSource.Type);
        Assert.Equal("db.example.com", restoredDataSource.Host);
        Assert.Equal(5432, restoredDataSource.Port);
        Assert.Equal("shop", restoredDataSource.Database);
        Assert.Equal("app", restoredDataSource.UserId);
        Assert.Equal(string.Empty, restoredDataSource.PasswordCipher);

        // 类型映射表已随备份还原，用户自定义条目完整保留
        TypeMappingEntry restoredMapping = Assert.Single(restoredConfig.TypeMappings);
        Assert.Equal("jsonb", restoredMapping.DbType);
        Assert.Equal("String", restoredMapping.TargetType);
        Assert.Equal("自定义映射", restoredMapping.Remark);

        // 落盘的配置文件文本不得出现任何明文密码或 apiKey
        string configText = await File.ReadAllTextAsync(target.ConfigService.ConfigFilePath);
        Assert.DoesNotContain(plainPassword, configText);
        Assert.DoesNotContain("sk-abc", configText);
    }

    /// <summary>
    /// 同名用户包冲突且未允许覆盖时应返回需确认结果，且不执行任何写盘。
    /// </summary>
    [Fact]
    public async Task RestoreBackupAsync_UserPackageConflict_ReturnsNeedsConfirmationWithoutWrites()
    {
        Environment source = CreateEnvironment();
        await CreatePackageAsync(source.UserLibrary, "conflict-pkg", marker: "new content");
        AppConfig sourceConfig = source.ConfigService.Load();
        sourceConfig.WorkspaceRoot = @"C:\workspace\backup";
        source.ConfigService.Save();
        string backupZip = Path.Combine(_tempRoot, "conflict.dbcg");
        await source.BackupService.CreateBackupAsync(backupZip, CancellationToken.None);

        Environment target = CreateEnvironment();
        await CreatePackageAsync(target.DefaultTemplateDirectory, "conflict-pkg", marker: "old content");

        RestoreResult result = await target.BackupService.RestoreBackupAsync(backupZip, overwriteUserPackages: false, CancellationToken.None);

        Assert.True(result.NeedsConfirmation);
        Assert.Equal(new[] { "conflict-pkg" }, result.ConflictingPackageNames);
        Assert.Empty(result.RestoredPackageNames);
        Assert.Empty(result.PasswordRequiredDataSources);

        // 未确认覆盖前不执行任何写盘：目标包内容不变，配置未还原
        string content = await File.ReadAllTextAsync(Path.Combine(target.DefaultTemplateDirectory, "conflict-pkg", "entity.java.scriban"));
        Assert.Equal("old content", content);
        Assert.NotEqual(@"C:\workspace\backup", target.ConfigService.Current.WorkspaceRoot);
    }

    /// <summary>
    /// 同名用户包冲突且允许覆盖时应成功替换旧包内容。
    /// </summary>
    [Fact]
    public async Task RestoreBackupAsync_UserPackageConflict_OverwriteSucceeds()
    {
        Environment source = CreateEnvironment();
        await CreatePackageAsync(source.UserLibrary, "conflict-pkg", marker: "new content");
        string backupZip = Path.Combine(_tempRoot, "overwrite.dbcg");
        await source.BackupService.CreateBackupAsync(backupZip, CancellationToken.None);

        Environment target = CreateEnvironment();
        await CreatePackageAsync(target.DefaultTemplateDirectory, "conflict-pkg", marker: "old content");

        RestoreResult result = await target.BackupService.RestoreBackupAsync(backupZip, overwriteUserPackages: true, CancellationToken.None);

        Assert.False(result.NeedsConfirmation);
        Assert.Equal(new[] { "conflict-pkg" }, result.RestoredPackageNames);
        string content = await File.ReadAllTextAsync(Path.Combine(target.DefaultTemplateDirectory, "conflict-pkg", "entity.java.scriban"));
        Assert.Equal("new content", content);
    }

    /// <summary>
    /// 恢复完成后临时目录应被清理，不留残留子目录。
    /// </summary>
    [Fact]
    public async Task RestoreBackupAsync_CleansUpTempDirectory()
    {
        Environment source = CreateEnvironment();
        await CreatePackageAsync(source.UserLibrary, "clean-pkg");
        string backupZip = Path.Combine(_tempRoot, "clean.dbcg");
        await source.BackupService.CreateBackupAsync(backupZip, CancellationToken.None);

        Environment target = CreateEnvironment();
        await target.BackupService.RestoreBackupAsync(backupZip, overwriteUserPackages: false, CancellationToken.None);

        bool hasLeftover = Directory.Exists(target.RestoreTempRoot) && Directory.EnumerateDirectories(target.RestoreTempRoot).Any();
        Assert.False(hasLeftover, "恢复临时目录存在残留子目录。");
    }

    /// <summary>
    /// 不支持的备份文件版本应被拒绝并抛出结构化异常。
    /// </summary>
    [Fact]
    public async Task RestoreBackupAsync_UnsupportedVersion_Throws()
    {
        string backupZip = Path.Combine(_tempRoot, "version.dbcg");
        var manifest = new BackupManifest { Version = 99, CreatedAt = DateTime.Now, AppVersion = "test" };
        await WriteDbcgAsync(backupZip, manifest, new Dictionary<string, string>());

        Environment target = CreateEnvironment();
        BackupValidationException exception = await Assert.ThrowsAsync<BackupValidationException>(
            () => target.BackupService.RestoreBackupAsync(backupZip, overwriteUserPackages: false, CancellationToken.None));

        Assert.Contains("版本", exception.Message);
    }

    /// <summary>
    /// 缺少 manifest.json 的备份文件应被拒绝。
    /// </summary>
    [Fact]
    public async Task RestoreBackupAsync_MissingManifest_Throws()
    {
        string backupZip = Path.Combine(_tempRoot, "no-manifest.dbcg");
        string? directory = Path.GetDirectoryName(backupZip);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using (FileStream zipStream = new(backupZip, FileMode.Create))
        using (ZipArchive archive = new(zipStream, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("templates/foo/template.json");
            await using Stream entryStream = entry.Open();
            await entryStream.WriteAsync("{}"u8.ToArray());
        }

        Environment target = CreateEnvironment();
        BackupValidationException exception = await Assert.ThrowsAsync<BackupValidationException>(
            () => target.BackupService.RestoreBackupAsync(backupZip, overwriteUserPackages: false, CancellationToken.None));

        Assert.Contains("manifest.json", exception.Message);
    }

    /// <summary>
    /// 含目录穿越条目（zip slip）的备份文件应被拒绝。
    /// </summary>
    [Fact]
    public async Task RestoreBackupAsync_SlipEntry_Throws()
    {
        string backupZip = Path.Combine(_tempRoot, "slip.dbcg");
        string? directory = Path.GetDirectoryName(backupZip);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using (FileStream zipStream = new(backupZip, FileMode.Create))
        using (ZipArchive archive = new(zipStream, ZipArchiveMode.Create))
        {
            ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json");
            await using (Stream manifestTarget = manifestEntry.Open())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new BackupManifest { Version = 1 }, JsonOptions));
                await manifestTarget.WriteAsync(bytes);
            }

            archive.CreateEntry("../evil.txt");
        }

        Environment target = CreateEnvironment();
        BackupValidationException exception = await Assert.ThrowsAsync<BackupValidationException>(
            () => target.BackupService.RestoreBackupAsync(backupZip, overwriteUserPackages: false, CancellationToken.None));

        Assert.Contains("非预期顶层条目", exception.Message);
    }

    /// <summary>
    /// 备份文件含清单未声明的孤儿包目录时应被拒绝。
    /// </summary>
    [Fact]
    public async Task RestoreBackupAsync_OrphanPackageDirectory_Throws()
    {
        string backupZip = Path.Combine(_tempRoot, "orphan.dbcg");
        var manifest = new BackupManifest
        {
            Version = 1,
            CreatedAt = DateTime.Now,
            AppVersion = "test",
            PackageNames = new List<string> { "declared" }
        };
        await WriteDbcgAsync(backupZip, manifest, new Dictionary<string, string>
        {
            ["declared/template.json"] = "{}",
            ["orphan/template.json"] = "{}"
        });

        Environment target = CreateEnvironment();
        BackupValidationException exception = await Assert.ThrowsAsync<BackupValidationException>(
            () => target.BackupService.RestoreBackupAsync(backupZip, overwriteUserPackages: false, CancellationToken.None));

        Assert.Contains("未在清单中声明", exception.Message);
    }

    /// <summary>
    /// 备份清单声明的包目录内 template.json 包名与目录不一致时应被拒绝，保持"包名=目录名"不变量。
    /// </summary>
    [Fact]
    public async Task RestoreBackupAsync_PackageNameMismatch_Throws()
    {
        string backupZip = Path.Combine(_tempRoot, "mismatch.dbcg");
        var manifest = new BackupManifest
        {
            Version = 1,
            CreatedAt = DateTime.Now,
            AppVersion = "test",
            PackageNames = new List<string> { "declared" }
        };
        await WriteDbcgAsync(backupZip, manifest, new Dictionary<string, string>
        {
            ["declared/template.json"] = """{"name":"other","engine":"scriban","files":[{"template":"a.txt","output":"out/{{table.className}}.java"}]}""",
            ["declared/a.txt"] = "x"
        });

        Environment target = CreateEnvironment();
        BackupValidationException exception = await Assert.ThrowsAsync<BackupValidationException>(
            () => target.BackupService.RestoreBackupAsync(backupZip, overwriteUserPackages: false, CancellationToken.None));

        Assert.Contains("包名与目录不一致", exception.Message);
    }

    /// <summary>
    /// 备份条目数超过上限应被拒绝（防 zip bomb）。
    /// </summary>
    [Fact]
    public async Task RestoreBackupAsync_EntryCountOverLimit_Throws()
    {
        string backupZip = Path.Combine(_tempRoot, "too-many.dbcg");
        string? directory = Path.GetDirectoryName(backupZip);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using (FileStream zipStream = new(backupZip, FileMode.Create))
        using (ZipArchive archive = new(zipStream, ZipArchiveMode.Create))
        {
            ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json");
            await using (Stream manifestTarget = manifestEntry.Open())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new BackupManifest { Version = 1 }, JsonOptions));
                await manifestTarget.WriteAsync(bytes);
            }

            for (int i = 0; i < TemplatePackageService.MaxZipEntries + 1; i++)
            {
                ZipArchiveEntry entry = archive.CreateEntry($"templates/pkg/f{i}.txt");
                await using Stream entryStream = entry.Open();
                await entryStream.WriteAsync("x"u8.ToArray());
            }
        }

        Environment target = CreateEnvironment();
        BackupValidationException exception = await Assert.ThrowsAsync<BackupValidationException>(
            () => target.BackupService.RestoreBackupAsync(backupZip, overwriteUserPackages: false, CancellationToken.None));

        Assert.Contains("条目数超过上限", exception.Message);
    }

    /// <summary>
    /// 备份单条解压大小超过上限应被拒绝（防 zip bomb）。
    /// </summary>
    [Fact]
    public async Task RestoreBackupAsync_SingleEntryOverLimit_Throws()
    {
        string backupZip = Path.Combine(_tempRoot, "big-entry.dbcg");
        string? directory = Path.GetDirectoryName(backupZip);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        byte[] bigBytes = new byte[TemplatePackageService.MaxSingleEntryBytes + 1];
        using (FileStream zipStream = new(backupZip, FileMode.Create))
        using (ZipArchive archive = new(zipStream, ZipArchiveMode.Create))
        {
            ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json");
            await using (Stream manifestTarget = manifestEntry.Open())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new BackupManifest { Version = 1 }, JsonOptions));
                await manifestTarget.WriteAsync(bytes);
            }

            ZipArchiveEntry entry = archive.CreateEntry("templates/pkg/big.bin");
            await using Stream entryStream = entry.Open();
            await entryStream.WriteAsync(bigBytes);
        }

        Environment target = CreateEnvironment();
        BackupValidationException exception = await Assert.ThrowsAsync<BackupValidationException>(
            () => target.BackupService.RestoreBackupAsync(backupZip, overwriteUserPackages: false, CancellationToken.None));

        Assert.Contains("单条解压大小超过上限", exception.Message);
    }
}
