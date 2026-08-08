using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbCodeGen.Core.Tests.Config;

/// <summary>
/// ConfigService 配置持久化服务的单元测试，覆盖首启默认、读写幂等、密文落盘、损坏重建、原子写与并发锁。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConfigServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly CredentialProtector _protector = new();
    private readonly List<ConfigService> _services = new();

    /// <summary>
    /// 为每个测试实例创建独立临时目录，避免用例间配置文件互相污染。
    /// </summary>
    public ConfigServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// 在独立临时目录下创建配置服务，并登记到清理列表。
    /// </summary>
    /// <param name="configPath">生成的配置文件绝对路径。</param>
    /// <returns>指向该配置文件的配置服务实例。</returns>
    private ConfigService CreateServiceInNewDirectory(out string configPath)
    {
        string directory = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        configPath = Path.Combine(directory, "config.json");
        return CreateService(configPath);
    }

    /// <summary>
    /// 在指定配置文件路径上创建配置服务，并登记到清理列表。
    /// </summary>
    /// <param name="configPath">配置文件绝对路径。</param>
    /// <returns>指向该配置文件的配置服务实例。</returns>
    private ConfigService CreateService(string configPath)
    {
        ConfigService service = new(_protector, NullLogger<ConfigService>.Instance, configPath);
        _services.Add(service);
        return service;
    }

    /// <summary>
    /// 释放所有登记的服务并递归删除临时目录。
    /// </summary>
    public void Dispose()
    {
        foreach (ConfigService service in _services)
        {
            service.Dispose();
        }

        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// 首次启动无配置文件时，Load 应生成默认配置并原子落盘，返回的默认值符合契约。
    /// </summary>
    [Fact]
    public void Load_NoFile_CreatesDefaultConfigFileAndReturnsDefaults()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);

        AppConfig config = service.Load();

        Assert.True(File.Exists(configPath));
        Assert.Equal(3, config.Version);
        Assert.Equal(string.Empty, config.WorkspaceRoot);
        Assert.Equal(string.Empty, config.LastRelativeOutputRoot);
        Assert.Equal("https://dashscope.aliyuncs.com/compatible-mode/v1", config.Llm.BaseUrl);
        Assert.Equal("qwen-plus", config.Llm.Model);
        Assert.Equal(string.Empty, config.Llm.ApiKeyEncrypted);
        string expectedTemplates = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DbCodeGen", "Templates");
        Assert.Contains(expectedTemplates, config.TemplateSearchDirectories);
        Assert.Empty(config.DataSources);
        Assert.NotEmpty(config.TypeMappings);
        Assert.Contains(config.TypeMappings, entry => entry.DbType == "bigint" && entry.TargetType == "Long");
    }

    /// <summary>
    /// 旧版配置（Version 2 及以下）加载时应升级到 Version 3，并按数据库类型分桶重灌内置默认映射。
    /// </summary>
    [Fact]
    public void Load_VersionOneWithoutTypeMappings_MigratesToDefaults()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """{"version":1,"workspaceRoot":"","lastRelativeOutputRoot":""}""");

        AppConfig config = service.Load();

        Assert.Equal(3, config.Version);
        Assert.NotEmpty(config.TypeMappings);
        Assert.Contains(config.TypeMappings, entry => entry.DbType == "bigint" && entry.TargetType == "Long" && entry.DatabaseType is null);
        Assert.Contains(config.TypeMappings, entry => entry.DbType == "integer" && entry.TargetType == "Integer" && entry.DatabaseType == DataSourceType.PostgreSql);
    }

    /// <summary>
    /// 已升级到 Version 3 后用户清空映射表，再次加载不应被回填默认映射。
    /// </summary>
    [Fact]
    public void Load_VersionThreeWithClearedMappings_DoesNotReseed()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """{"version":3,"typeMappings":[]}""");

        AppConfig config = service.Load();

        Assert.Equal(3, config.Version);
        Assert.Empty(config.TypeMappings);
    }

    /// <summary>
    /// 保存后使用同一路径新建服务重新加载，各字段值应完整还原，验证配置读写幂等。
    /// </summary>
    [Fact]
    public void Save_ThenNewServiceLoads_RoundTripsAllValues()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);
        AppConfig config = service.Load();

        config.WorkspaceRoot = @"C:\workspace\app";
        config.LastRelativeOutputRoot = "src/main/java";
        config.Llm.BaseUrl = "https://custom.example.com/v1";
        config.Llm.Model = "qwen-max";
        config.TemplateSearchDirectories.Clear();
        config.TemplateSearchDirectories.Add(@"C:\templates\custom");
        config.TypeMappings.Clear();
        config.TypeMappings.Add(new TypeMappingEntry { DbType = "jsonb", TargetType = "String", DatabaseType = DataSourceType.PostgreSql });
        config.TypeMappings.Add(new TypeMappingEntry { DbType = "varchar", TargetType = "String" });
        config.DataSources.Add(new DataSourceConfig
        {
            Name = "dev",
            Type = DataSourceType.MySql,
            Host = "localhost",
            Port = 3306,
            Database = "shop",
            UserId = "root",
            PasswordCipher = _protector.Encrypt("p@ss"),
            CreatedAt = new DateTime(2026, 1, 1, 8, 0, 0),
            UpdatedAt = new DateTime(2026, 1, 1, 8, 0, 0)
        });
        service.Save();

        ConfigService reloaded = CreateService(configPath);
        AppConfig loaded = reloaded.Load();

        Assert.Equal(@"C:\workspace\app", loaded.WorkspaceRoot);
        Assert.Equal("src/main/java", loaded.LastRelativeOutputRoot);
        Assert.Equal("https://custom.example.com/v1", loaded.Llm.BaseUrl);
        Assert.Equal("qwen-max", loaded.Llm.Model);
        Assert.Single(loaded.TemplateSearchDirectories);
        Assert.Equal(@"C:\templates\custom", loaded.TemplateSearchDirectories[0]);
        DataSourceConfig dataSource = Assert.Single(loaded.DataSources);
        Assert.Equal("dev", dataSource.Name);
        Assert.Equal(DataSourceType.MySql, dataSource.Type);
        Assert.Equal("localhost", dataSource.Host);
        Assert.Equal(3306, dataSource.Port);
        Assert.Equal("shop", dataSource.Database);
        Assert.Equal("root", dataSource.UserId);
        Assert.Equal("p@ss", _protector.Decrypt(dataSource.PasswordCipher));
        Assert.Equal(new DateTime(2026, 1, 1, 8, 0, 0), dataSource.CreatedAt);
        Assert.Equal(new DateTime(2026, 1, 1, 8, 0, 0), dataSource.UpdatedAt);

        // 类型映射含数据库作用域字段应完整还原：PG 专属条目与通用条目均保留 DatabaseType 值
        Assert.Equal(2, loaded.TypeMappings.Count);
        TypeMappingEntry pgEntry = loaded.TypeMappings.Single(entry => entry.DbType == "jsonb");
        Assert.Equal(DataSourceType.PostgreSql, pgEntry.DatabaseType);
        TypeMappingEntry genericEntry = loaded.TypeMappings.Single(entry => entry.DbType == "varchar");
        Assert.Null(genericEntry.DatabaseType);
    }

    /// <summary>
    /// apiKey 应密文落盘：文件中不含明文，且 GetLlmApiKey 能解密还原明文。
    /// </summary>
    [Fact]
    public void Save_ApiKeyEncrypted_FileHasNoPlaintextAndGetLlmApiKeyRoundTrips()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);
        AppConfig config = service.Load();

        const string plainApiKey = "sk-secret-key-abc123";
        config.Llm.ApiKeyEncrypted = _protector.Encrypt(plainApiKey);
        service.Save();

        string fileText = File.ReadAllText(configPath, Encoding.UTF8);
        Assert.DoesNotContain(plainApiKey, fileText);
        Assert.Contains("apiKeyEncrypted", fileText);
        Assert.Equal(plainApiKey, service.GetLlmApiKey());
    }

    /// <summary>
    /// 未配置 apiKey 时 GetLlmApiKey 应返回 null，不触发解密。
    /// </summary>
    [Fact]
    public void GetLlmApiKey_NotConfigured_ReturnsNull()
    {
        ConfigService service = CreateServiceInNewDirectory(out _);

        string? apiKey = service.GetLlmApiKey();

        Assert.Null(apiKey);
    }

    /// <summary>
    /// GetGenerationDefaults 应返回当前工作区根与最近相对输出根的快照。
    /// </summary>
    [Fact]
    public void GetGenerationDefaults_ReturnsWorkspaceRootAndLastRelativeOutputRoot()
    {
        ConfigService service = CreateServiceInNewDirectory(out _);
        AppConfig config = service.Load();
        config.WorkspaceRoot = @"C:\gen\root";
        config.LastRelativeOutputRoot = "src/main/resources";
        service.Save();

        GenerationDefaults defaults = service.GetGenerationDefaults();

        Assert.Equal(@"C:\gen\root", defaults.WorkspaceRoot);
        Assert.Equal("src/main/resources", defaults.LastRelativeOutputRoot);
    }

    /// <summary>
    /// 损坏的 JSON 配置文件应被备份为 .bak.时间戳 副本，并按默认配置重建落盘。
    /// </summary>
    [Fact]
    public void Load_CorruptJson_BacksUpFileAndRebuildsDefaults()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);
        const string corruptText = "{ this is not valid json !!!";
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, corruptText);

        AppConfig config = service.Load();

        Assert.Equal(3, config.Version);
        Assert.Equal(string.Empty, config.WorkspaceRoot);
        string configDirectory = Path.GetDirectoryName(configPath)!;
        string[] backups = Directory.GetFiles(configDirectory, "config.json.bak.*");
        Assert.Single(backups);
        Assert.Equal(corruptText, File.ReadAllText(backups[0]));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath));
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    /// <summary>
    /// 空文件应视为损坏，备份后按默认配置重建，应用不崩溃。
    /// </summary>
    [Fact]
    public void Load_EmptyFile_BacksUpAndRebuildsDefaults()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, string.Empty);

        AppConfig config = service.Load();

        Assert.Equal(3, config.Version);
        string configDirectory = Path.GetDirectoryName(configPath)!;
        Assert.NotEmpty(Directory.GetFiles(configDirectory, "config.json.bak.*"));
        Assert.True(File.Exists(configPath));
    }

    /// <summary>
    /// 写盘失败时应抛出 ConfigSaveException 且内存中的原值保持不变，可提示用户重试。
    /// </summary>
    [Fact]
    public void Save_WhenDirectoryBlockedByFile_ThrowsConfigSaveExceptionAndPreservesValue()
    {
        string blockedDirectory = Path.Combine(_tempRoot, "blocked");
        Directory.CreateDirectory(blockedDirectory);
        string blockerFile = Path.Combine(blockedDirectory, "config.json");
        File.WriteAllText(blockerFile, "i am a file, not a directory");
        string impossiblePath = Path.Combine(blockerFile, "config.json");

        ConfigService service = CreateService(impossiblePath);
        AppConfig config = service.Load();

        // 首启落盘失败时 Load 仍应返回内存默认配置，不崩溃
        Assert.Equal(3, config.Version);

        config.WorkspaceRoot = @"C:\workspace\should-keep";
        ConfigSaveException exception = Assert.Throws<ConfigSaveException>(() => service.Save());

        Assert.NotNull(exception.InnerException);
        Assert.Equal(@"C:\workspace\should-keep", service.Current.WorkspaceRoot);
    }

    /// <summary>
    /// 未先调用 Load 直接 Save，应自动初始化默认配置并落盘，不写出空配置。
    /// </summary>
    [Fact]
    public void Save_WithoutPriorLoad_CreatesDefaultConfigFile()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);

        service.Save();

        Assert.True(File.Exists(configPath));
        AppConfig config = service.Load();
        Assert.Equal(3, config.Version);
    }

    /// <summary>
    /// 原子写应不残留临时文件，保存后的文件为完整 JSON。
    /// </summary>
    [Fact]
    public void Save_AtomicWrite_LeavesNoTempFileAndFileIsValidJson()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);
        service.Load();
        service.Save();

        Assert.False(File.Exists(configPath + ".tmp"));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath));
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    /// <summary>
    /// 多次调用 Load 应返回同一内存快照实例，验证 Load 幂等。
    /// </summary>
    [Fact]
    public void Load_Twice_ReturnsSameSnapshot()
    {
        ConfigService service = CreateServiceInNewDirectory(out _);

        AppConfig first = service.Load();
        AppConfig second = service.Load();

        Assert.Same(first, second);
    }

    /// <summary>
    /// 多线程并发调用 Load/Save/GetGenerationDefaults 不应撕裂配置文件，文件始终保持合法 JSON。
    /// </summary>
    [Fact]
    public void ConcurrentLoadAndSave_FileRemainsValidJson()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);
        service.Load();

        var errors = new List<Exception>();
        var threads = new List<Thread>();
        // 并发线程反复执行读改写，验证单例锁下不产生竞态、文件不被撕裂
        for (int i = 0; i < 8; i++)
        {
            Thread thread = new Thread(() =>
            {
                try
                {
                    for (int j = 0; j < 20; j++)
                    {
                        service.Load();
                        service.Save();
                        service.GetGenerationDefaults();
                    }
                }
                catch (Exception exception)
                {
                    // 收集各线程异常，用例末尾统一断言无异常
                    lock (errors)
                    {
                        errors.Add(exception);
                    }
                }
            });
            threads.Add(thread);
        }

        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        Assert.Empty(errors);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath));
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }
}
