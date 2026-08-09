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
        Assert.Equal(300, config.Llm.TimeoutSeconds);
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
    /// 旧配置迁移后应立即原子落盘：仅 Load 不调用 Save，磁盘文件也应已升级为 Version 3，
    /// 保证磁盘配置始终是生效配置，避免"打开即关不落盘"残留旧版本。
    /// </summary>
    [Fact]
    public void Load_VersionOne_MigratesAndPersistsImmediately()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """{"version":1,"workspaceRoot":"","lastRelativeOutputRoot":""}""");

        service.Load();

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath));
        Assert.Equal(3, document.RootElement.GetProperty("version").GetInt32());
        Assert.True(document.RootElement.GetProperty("typeMappings").GetArrayLength() > 0);
    }

    /// <summary>
    /// 旧版配置含自定义映射时，迁移应保留用户条目并补齐按库默认条目，不覆盖自定义。
    /// </summary>
    [Fact]
    public void Load_VersionTwo_WithCustomMappings_PreservesAndAddsDefaults()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        // 旧配置含一条自定义映射 varchar→MyString（通用），且无 PG 专属 integer 条目
        File.WriteAllText(configPath, """{"version":2,"typeMappings":[{"dbType":"varchar","targetType":"MyString","import":null,"remark":null}]}""");

        AppConfig config = service.Load();

        Assert.Equal(3, config.Version);
        Assert.Contains(config.TypeMappings, entry => entry.DbType == "varchar" && entry.TargetType == "MyString");
        Assert.Contains(config.TypeMappings, entry => entry.DbType == "integer" && entry.DatabaseType == DataSourceType.PostgreSql);
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
        config.Llm.TimeoutSeconds = 240;
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
        Assert.Equal(240, loaded.Llm.TimeoutSeconds);
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
    /// 保存后使用同一路径新建服务重新加载，按包记忆的模板勾选态应完整还原（多包、多文件、勾选真假混合）。
    /// </summary>
    [Fact]
    public void Save_ThenNewServiceLoads_RoundTripsTemplateFileStates()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);
        AppConfig config = service.Load();

        config.TemplateFileStates["user-pkg"] = new List<TemplateFileState>
        {
            new() { TemplatePath = "entity.java.scriban", Enabled = true },
            new() { TemplatePath = "mapper.xml.scriban", Enabled = false }
        };
        config.TemplateFileStates["builtin-pkg"] = new List<TemplateFileState>
        {
            new() { TemplatePath = "main.tpl", Enabled = true }
        };
        service.Save();

        ConfigService reloaded = CreateService(configPath);
        AppConfig loaded = reloaded.Load();

        Assert.Equal(2, loaded.TemplateFileStates.Count);
        Assert.True(loaded.TemplateFileStates.TryGetValue("user-pkg", out List<TemplateFileState>? userStates));
        Assert.Equal(2, userStates!.Count);
        Assert.Contains(userStates, state => state.TemplatePath == "entity.java.scriban" && state.Enabled);
        Assert.Contains(userStates, state => state.TemplatePath == "mapper.xml.scriban" && !state.Enabled);
        Assert.True(loaded.TemplateFileStates.TryGetValue("builtin-pkg", out List<TemplateFileState>? builtinStates));
        TemplateFileState mainState = Assert.Single(builtinStates!);
        Assert.Equal("main.tpl", mainState.TemplatePath);
        Assert.True(mainState.Enabled);
    }

    /// <summary>
    /// 不含模板勾选态字段的旧配置文件反序列化后，TemplateFileStates 应兜底为非 null 的空字典，下游读取不抛空引用。
    /// </summary>
    [Fact]
    public void Load_JsonWithoutTemplateFileStates_NormalizesToEmptyDictionary()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """{"version":3,"workspaceRoot":"","lastRelativeOutputRoot":""}""");

        AppConfig config = service.Load();

        Assert.NotNull(config.TemplateFileStates);
        Assert.Empty(config.TemplateFileStates);
    }

    /// <summary>
    /// 保存后使用同一路径新建服务重新加载，包顺序与包内文件顺序记忆应完整还原，
    /// 落盘字段为 camelCase 的 templatePackageOrder/templateFileOrder，验证读写幂等。
    /// </summary>
    [Fact]
    public void Save_ThenNewServiceLoads_RoundTripsTemplateOrderFields()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);
        AppConfig config = service.Load();

        config.TemplatePackageOrder.Add("java-mybatis");
        config.TemplatePackageOrder.Add("my-user-pkg");
        config.TemplateFileOrder["user-pkg"] = new List<string> { "entity/Entity.java", "mapper/Mapper.xml" };
        config.TemplateFileOrder["builtin-pkg"] = new List<string> { "main.tpl" };
        service.Save();

        string fileText = File.ReadAllText(configPath, Encoding.UTF8);
        Assert.Contains("templatePackageOrder", fileText);
        Assert.Contains("templateFileOrder", fileText);

        ConfigService reloaded = CreateService(configPath);
        AppConfig loaded = reloaded.Load();

        Assert.Equal(2, loaded.TemplatePackageOrder.Count);
        Assert.Equal("java-mybatis", loaded.TemplatePackageOrder[0]);
        Assert.Equal("my-user-pkg", loaded.TemplatePackageOrder[1]);
        Assert.Equal(2, loaded.TemplateFileOrder.Count);
        Assert.True(loaded.TemplateFileOrder.TryGetValue("user-pkg", out List<string>? userFiles));
        Assert.Equal(2, userFiles!.Count);
        Assert.Equal("entity/Entity.java", userFiles[0]);
        Assert.Equal("mapper/Mapper.xml", userFiles[1]);
        Assert.True(loaded.TemplateFileOrder.TryGetValue("builtin-pkg", out List<string>? builtinFiles));
        Assert.Equal("main.tpl", Assert.Single(builtinFiles!));
    }

    /// <summary>
    /// 不含排序记忆字段的旧配置文件反序列化后，TemplatePackageOrder 与 TemplateFileOrder 应兜底为非 null 的空集合，
    /// 下游按记忆重排时读到空集合即视为默认排序，不抛空引用。
    /// </summary>
    [Fact]
    public void Load_JsonWithoutTemplateOrderFields_NormalizesToEmptyCollections()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """{"version":3,"workspaceRoot":"","lastRelativeOutputRoot":""}""");

        AppConfig config = service.Load();

        Assert.NotNull(config.TemplatePackageOrder);
        Assert.Empty(config.TemplatePackageOrder);
        Assert.NotNull(config.TemplateFileOrder);
        Assert.Empty(config.TemplateFileOrder);
    }

    /// <summary>
    /// 手工编辑配置使排序记忆字段含 null 值、空白包名、空键、空值清单与清单内空白项时，
    /// 加载后应剔除非法项，仅保留合法排序记忆，防止下游按记忆重排读到空串或空引用。
    /// </summary>
    [Fact]
    public void Load_JsonWithNullAndBlankTemplateOrderEntries_Normalizes()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """{"version":3,"templatePackageOrder":["java-mybatis","",null,"  ","my-user-pkg"],"templateFileOrder":{"user-pkg":["entity/Entity.java","",null,"mapper/Mapper.xml"],"":["x.tpl"],"  ":["y.tpl"],"null-list":null}}""");

        AppConfig config = service.Load();

        Assert.Equal(2, config.TemplatePackageOrder.Count);
        Assert.Equal("java-mybatis", config.TemplatePackageOrder[0]);
        Assert.Equal("my-user-pkg", config.TemplatePackageOrder[1]);
        Assert.Single(config.TemplateFileOrder);
        Assert.True(config.TemplateFileOrder.TryGetValue("user-pkg", out List<string>? userFiles));
        Assert.Equal(2, userFiles!.Count);
        Assert.Equal("entity/Entity.java", userFiles[0]);
        Assert.Equal("mapper/Mapper.xml", userFiles[1]);
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

    /// <summary>
    /// 首次启动默认配置中 AI 参考文件限制应为契约默认值：数量 20、单文件 1MB、总大小 10MB。
    /// </summary>
    [Fact]
    public void Load_NoFile_AiReferenceFileLimitsHasContractDefaults()
    {
        ConfigService service = CreateServiceInNewDirectory(out _);

        AppConfig config = service.Load();

        Assert.NotNull(config.AiReferenceFileLimits);
        Assert.Equal(20, config.AiReferenceFileLimits.MaxFileCount);
        Assert.Equal(1 * 1024 * 1024, config.AiReferenceFileLimits.MaxSingleFileBytes);
        Assert.Equal(10 * 1024 * 1024, config.AiReferenceFileLimits.MaxTotalBytes);
    }

    /// <summary>
    /// 修改 AI 参考文件限制后保存再新实例加载，三个字段应完整还原，落盘字段为 camelCase 的 aiReferenceFileLimits，验证读写幂等。
    /// </summary>
    [Fact]
    public void Save_ThenNewServiceLoads_RoundTripsAiReferenceFileLimits()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);
        AppConfig config = service.Load();

        config.AiReferenceFileLimits.MaxFileCount = 5;
        config.AiReferenceFileLimits.MaxSingleFileBytes = 2 * 1024 * 1024;
        config.AiReferenceFileLimits.MaxTotalBytes = 8 * 1024 * 1024;
        service.Save();

        string fileText = File.ReadAllText(configPath, Encoding.UTF8);
        Assert.Contains("aiReferenceFileLimits", fileText);

        ConfigService reloaded = CreateService(configPath);
        AppConfig loaded = reloaded.Load();

        Assert.Equal(5, loaded.AiReferenceFileLimits.MaxFileCount);
        Assert.Equal(2 * 1024 * 1024, loaded.AiReferenceFileLimits.MaxSingleFileBytes);
        Assert.Equal(8 * 1024 * 1024, loaded.AiReferenceFileLimits.MaxTotalBytes);
    }

    /// <summary>
    /// 配置文件显式写入 null 的 aiReferenceFileLimits 字段时，加载后应兜底为默认实例，下游读取不抛空引用。
    /// </summary>
    [Fact]
    public void Load_JsonWithNullAiReferenceFileLimits_NormalizesToDefaults()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """{"version":3,"workspaceRoot":"","lastRelativeOutputRoot":"","aiReferenceFileLimits":null}""");

        AppConfig config = service.Load();

        Assert.NotNull(config.AiReferenceFileLimits);
        Assert.Equal(20, config.AiReferenceFileLimits.MaxFileCount);
        Assert.Equal(1 * 1024 * 1024, config.AiReferenceFileLimits.MaxSingleFileBytes);
        Assert.Equal(10 * 1024 * 1024, config.AiReferenceFileLimits.MaxTotalBytes);
    }

    /// <summary>
    /// 手工编辑配置使任一限制字段非正数时，加载后应恢复对应默认常量，防止非法上限生效。
    /// </summary>
    [Fact]
    public void Load_AiReferenceFileLimitsNonPositive_NormalizesToDefaults()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """{"version":3,"aiReferenceFileLimits":{"maxFileCount":0,"maxSingleFileBytes":-1,"maxTotalBytes":-5}}""");

        AppConfig config = service.Load();

        Assert.Equal(20, config.AiReferenceFileLimits.MaxFileCount);
        Assert.Equal(1 * 1024 * 1024, config.AiReferenceFileLimits.MaxSingleFileBytes);
        Assert.Equal(10 * 1024 * 1024, config.AiReferenceFileLimits.MaxTotalBytes);
    }

    /// <summary>
    /// 手工编辑使单文件上限大于总大小上限时，加载后应收敛为总大小上限，防止上传校验死锁。
    /// </summary>
    [Fact]
    public void Load_AiReferenceFileLimitsSingleFileExceedsTotal_ConvergesToTotal()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """{"version":3,"aiReferenceFileLimits":{"maxFileCount":5,"maxSingleFileBytes":8,"maxTotalBytes":4}}""");

        AppConfig config = service.Load();

        Assert.Equal(5, config.AiReferenceFileLimits.MaxFileCount);
        Assert.Equal(4, config.AiReferenceFileLimits.MaxSingleFileBytes);
        Assert.Equal(4, config.AiReferenceFileLimits.MaxTotalBytes);
    }

    /// <summary>
    /// 手工编辑配置使 LLM 请求超时非正数时，加载后应恢复默认 300，防止非法超时值使请求瞬间超时或无限挂起。
    /// </summary>
    [Fact]
    public void Load_LlmTimeoutSecondsNonPositive_NormalizesToDefault()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """{"version":3,"llm":{"baseUrl":"https://example.com/v1","timeoutSeconds":0}}""");

        AppConfig config = service.Load();

        Assert.Equal(300, config.Llm.TimeoutSeconds);
    }

    /// <summary>
    /// 配置文件缺省 LLM 请求超时字段时，加载后应使用属性初始化器默认 300，旧配置自然回退无需迁移。
    /// </summary>
    [Fact]
    public void Load_LlmWithoutTimeoutSeconds_DefaultsTo300()
    {
        ConfigService service = CreateServiceInNewDirectory(out string configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """{"version":3,"llm":{"baseUrl":"https://example.com/v1"}}""");

        AppConfig config = service.Load();

        Assert.Equal(300, config.Llm.TimeoutSeconds);
    }
}
