using System.Runtime.Versioning;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.DataSource;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbCodeGen.Core.Tests.DataSource;

/// <summary>
/// DataSourceManagementService 连接管理服务单元测试，覆盖新增/更新/删除的持久化与回滚、
/// 唯一名校验（忽略大小写、排除自身）、密码 DPAPI 加密与留空保持原密文、字段校验与名称精确查找。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DataSourceManagementServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly CredentialProtector _protector = new();
    private readonly List<ConfigService> _configServices = new();

    /// <summary>
    /// 为每个测试实例创建独立临时目录，避免配置文件互相污染。
    /// </summary>
    public DataSourceManagementServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// 释放登记的配置服务并递归删除临时目录。
    /// </summary>
    public void Dispose()
    {
        foreach (ConfigService configService in _configServices)
        {
            configService.Dispose();
        }

        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// 新增连接应成功落盘：密码以 DPAPI 密文持久化且可解密还原，时间戳写入，新服务实例可从磁盘读回。
    /// </summary>
    [Fact]
    public void Save_NewConnection_PersistsEncryptedCipherAndRoundTrips()
    {
        DataSourceManagementService service = CreateService(out ConfigService configService);

        DataSourceOperationResult result = service.Save(BuildDraft(name: "dev-mysql", password: "secret-P@ss"));

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Config);
        Assert.Single(service.List());
        DataSourceConfig saved = service.GetByName("dev-mysql")!;
        Assert.NotEqual("secret-P@ss", saved.PasswordCipher);
        Assert.Equal("secret-P@ss", _protector.Decrypt(saved.PasswordCipher));
        Assert.True(saved.CreatedAt > default(DateTime));
        Assert.True(saved.UpdatedAt > default(DateTime));

        // 换新服务实例从同一配置文件读回，验证持久化真实落盘
        DataSourceManagementService reloaded = CreateManagementService(configService.ConfigFilePath);
        DataSourceConfig? persisted = reloaded.GetByName("dev-mysql");
        Assert.NotNull(persisted);
        Assert.Equal("secret-P@ss", _protector.Decrypt(persisted.PasswordCipher));
    }

    /// <summary>
    /// 新增连接与既有连接重名（忽略大小写）应返回 NameConflict 且不落盘。
    /// </summary>
    [Fact]
    public void Save_NewConnection_DuplicateNameIgnoringCase_ReturnsNameConflict()
    {
        DataSourceManagementService service = CreateService(out _);
        Assert.True(service.Save(BuildDraft(name: "dev-mysql")).IsSuccess);

        DataSourceOperationResult result = service.Save(BuildDraft(name: "DEV-MYSQL", port: 3307));

        Assert.Equal(DataSourceOperationStatus.NameConflict, result.Status);
        Assert.Contains("已存在", result.Message);
        Assert.Single(service.List());
    }

    /// <summary>
    /// 编辑重命名到既有连接名应返回 NameConflict；改回唯一名应成功。
    /// </summary>
    [Fact]
    public void Save_EditRenameToExistingName_ReturnsNameConflict()
    {
        DataSourceManagementService service = CreateService(out _);
        Assert.True(service.Save(BuildDraft(name: "dev-mysql")).IsSuccess);
        Assert.True(service.Save(BuildDraft(name: "prod-mysql")).IsSuccess);

        DataSourceOperationResult conflict = service.Save(
            BuildDraft(name: "Prod-Mysql", originalName: "dev-mysql"));

        Assert.Equal(DataSourceOperationStatus.NameConflict, conflict.Status);
        Assert.Equal(2, service.List().Count);
        Assert.Null(service.GetByName("Prod-Mysql"));

        DataSourceOperationResult renamed = service.Save(
            BuildDraft(name: "dev-mysql-2", originalName: "dev-mysql"));
        Assert.True(renamed.IsSuccess);
        Assert.NotNull(service.GetByName("dev-mysql-2"));
        Assert.Null(service.GetByName("dev-mysql"));
    }

    /// <summary>
    /// 编辑连接密码留空应保持原密文且保留创建时间；提供新密码应重新加密覆盖。
    /// </summary>
    [Fact]
    public void Save_EditPasswordSemantics_KeepCipherWhenEmptyAndReencryptWhenProvided()
    {
        DataSourceManagementService service = CreateService(out _);
        Assert.True(service.Save(BuildDraft(name: "dev-mysql", password: "old-pass")).IsSuccess);
        string originalCipher = service.GetByName("dev-mysql")!.PasswordCipher;
        DateTime originalCreatedAt = service.GetByName("dev-mysql")!.CreatedAt;

        // 密码留空：保持原密文，创建时间保留，其余字段更新
        DataSourceOperationResult kept = service.Save(
            BuildDraft(name: "dev-mysql", originalName: "dev-mysql", password: null, host: "10.0.0.8"));
        Assert.True(kept.IsSuccess);
        DataSourceConfig keptConfig = service.GetByName("dev-mysql")!;
        Assert.Equal(originalCipher, keptConfig.PasswordCipher);
        Assert.Equal("old-pass", _protector.Decrypt(keptConfig.PasswordCipher));
        Assert.Equal(originalCreatedAt, keptConfig.CreatedAt);
        Assert.Equal("10.0.0.8", keptConfig.Host);
        Assert.True(keptConfig.UpdatedAt >= originalCreatedAt);

        // 提供新密码：重新加密覆盖
        DataSourceOperationResult reencrypted = service.Save(
            BuildDraft(name: "dev-mysql", originalName: "dev-mysql", password: "new-pass"));
        Assert.True(reencrypted.IsSuccess);
        DataSourceConfig updatedConfig = service.GetByName("dev-mysql")!;
        Assert.NotEqual(originalCipher, updatedConfig.PasswordCipher);
        Assert.Equal("new-pass", _protector.Decrypt(updatedConfig.PasswordCipher));
    }

    /// <summary>
    /// 编辑目标（原名称连接）不存在时应返回 OriginalMissing 且不落盘。
    /// </summary>
    [Fact]
    public void Save_EditMissingOriginal_ReturnsOriginalMissing()
    {
        DataSourceManagementService service = CreateService(out _);

        DataSourceOperationResult result = service.Save(
            BuildDraft(name: "dev-mysql", originalName: "ghost"));

        Assert.Equal(DataSourceOperationStatus.OriginalMissing, result.Status);
        Assert.Empty(service.List());
    }

    /// <summary>
    /// 字段校验失败（名称/主机/库名/用户名为空或端口越界）应返回 Invalid 且不落盘。
    /// </summary>
    [Theory]
    [InlineData("", "127.0.0.1", 3306, "shop", "root")]
    [InlineData("dev", "", 3306, "shop", "root")]
    [InlineData("dev", "127.0.0.1", 3306, "", "root")]
    [InlineData("dev", "127.0.0.1", 3306, "shop", "")]
    [InlineData("dev", "127.0.0.1", 0, "shop", "root")]
    [InlineData("dev", "127.0.0.1", 65536, "shop", "root")]
    public void Save_InvalidFields_ReturnsInvalidWithoutTouchingConfig(
        string name, string host, int port, string database, string userId)
    {
        DataSourceManagementService service = CreateService(out _);

        DataSourceOperationResult result = service.Save(
            BuildDraft(name: name, host: host, port: port, database: database, userId: userId));

        Assert.Equal(DataSourceOperationStatus.Invalid, result.Status);
        Assert.Empty(service.List());
    }

    /// <summary>
    /// 落盘失败时新增连接应回滚内存快照（列表恢复为空），并返回 SaveFailed 携带底层消息。
    /// </summary>
    [Fact]
    public void Save_ConfigSaveFailsOnNew_RollsBackMemory()
    {
        var stubConfig = new StubConfigService();
        DataSourceManagementService service = CreateServiceOnConfig(stubConfig);

        DataSourceOperationResult result = service.Save(BuildDraft(name: "dev-mysql"));

        Assert.Equal(DataSourceOperationStatus.SaveFailed, result.Status);
        Assert.Contains("模拟写盘失败", result.Message);
        Assert.Empty(stubConfig.Current.DataSources);
    }

    /// <summary>
    /// 落盘失败时编辑连接应回滚内存快照（原值字段完整恢复）。
    /// </summary>
    [Fact]
    public void Save_ConfigSaveFailsOnEdit_RestoresOriginalValues()
    {
        var stubConfig = new StubConfigService();
        stubConfig.Current.DataSources.Add(new DataSourceConfig
        {
            Name = "dev-mysql",
            Type = DataSourceType.MySql,
            Host = "127.0.0.1",
            Port = 3306,
            Database = "shop",
            UserId = "root",
            PasswordCipher = "cipher-base64",
            CreatedAt = DateTime.Now.AddDays(-1),
            UpdatedAt = DateTime.Now.AddDays(-1)
        });
        DataSourceManagementService service = CreateServiceOnConfig(stubConfig);

        DataSourceOperationResult result = service.Save(
            BuildDraft(name: "dev-renamed", originalName: "dev-mysql", host: "10.0.0.8"));

        Assert.Equal(DataSourceOperationStatus.SaveFailed, result.Status);
        DataSourceConfig restored = Assert.Single(stubConfig.Current.DataSources);
        Assert.Equal("dev-mysql", restored.Name);
        Assert.Equal("127.0.0.1", restored.Host);
        Assert.Equal("cipher-base64", restored.PasswordCipher);
    }

    /// <summary>
    /// 删除连接应成功落盘并可被新服务实例从磁盘读回（仅剩其余连接）。
    /// </summary>
    [Fact]
    public void Delete_RemovesConnectionAndPersists()
    {
        DataSourceManagementService service = CreateService(out ConfigService configService);
        Assert.True(service.Save(BuildDraft(name: "dev-mysql")).IsSuccess);
        Assert.True(service.Save(BuildDraft(name: "prod-mysql")).IsSuccess);

        DataSourceOperationResult result = service.Delete("dev-mysql");

        Assert.True(result.IsSuccess);
        Assert.Equal("dev-mysql", result.Config!.Name);
        Assert.Null(service.GetByName("dev-mysql"));
        Assert.NotNull(service.GetByName("prod-mysql"));

        DataSourceManagementService reloaded = CreateManagementService(configService.ConfigFilePath);
        Assert.Null(reloaded.GetByName("dev-mysql"));
        Assert.Single(reloaded.List());
    }

    /// <summary>
    /// 删除不存在的连接应返回 NotFound 且不落盘。
    /// </summary>
    [Fact]
    public void Delete_NotFound_ReturnsNotFound()
    {
        DataSourceManagementService service = CreateService(out _);

        DataSourceOperationResult result = service.Delete("ghost");

        Assert.Equal(DataSourceOperationStatus.NotFound, result.Status);
        Assert.Empty(service.List());
    }

    /// <summary>
    /// 删除落盘失败时应回滚内存快照（连接保留在列表中）。
    /// </summary>
    [Fact]
    public void Delete_ConfigSaveFails_RollsBackMemory()
    {
        var stubConfig = new StubConfigService();
        stubConfig.Current.DataSources.Add(new DataSourceConfig { Name = "dev-mysql" });
        DataSourceManagementService service = CreateServiceOnConfig(stubConfig);

        DataSourceOperationResult result = service.Delete("dev-mysql");

        Assert.Equal(DataSourceOperationStatus.SaveFailed, result.Status);
        DataSourceConfig restored = Assert.Single(stubConfig.Current.DataSources);
        Assert.Equal("dev-mysql", restored.Name);
    }

    /// <summary>
    /// GetByName 按区分大小写的精确匹配查找：大小写不同的名称不命中。
    /// </summary>
    [Fact]
    public void GetByName_UsesOrdinalExactMatch()
    {
        DataSourceManagementService service = CreateService(out _);
        Assert.True(service.Save(BuildDraft(name: "dev-mysql")).IsSuccess);

        Assert.NotNull(service.GetByName("dev-mysql"));
        Assert.Null(service.GetByName("DEV-MYSQL"));
        Assert.Null(service.GetByName("dev"));
        Assert.Null(service.GetByName(""));
    }

    /// <summary>
    /// 创建指向独立临时配置文件的连接管理服务与真实配置服务。
    /// </summary>
    /// <param name="configService">输出的真实配置服务实例。</param>
    /// <returns>连接管理服务实例。</returns>
    private DataSourceManagementService CreateService(out ConfigService configService)
    {
        string configPath = Path.Combine(_tempRoot, $"config-{Guid.NewGuid():N}", "config.json");
        configService = new ConfigService(_protector, NullLogger<ConfigService>.Instance, configPath);
        _configServices.Add(configService);
        return new DataSourceManagementService(
            configService, _protector, NullLogger<DataSourceManagementService>.Instance);
    }

    /// <summary>
    /// 创建指向既有配置文件路径的连接管理服务，供持久化读回验证。
    /// </summary>
    /// <param name="configPath">配置文件绝对路径。</param>
    /// <returns>连接管理服务实例。</returns>
    private DataSourceManagementService CreateManagementService(string configPath)
    {
        ConfigService configService = new(_protector, NullLogger<ConfigService>.Instance, configPath);
        _configServices.Add(configService);
        return new DataSourceManagementService(
            configService, _protector, NullLogger<DataSourceManagementService>.Instance);
    }

    /// <summary>
    /// 创建基于测试替身配置服务的连接管理服务，供落盘失败回滚验证。
    /// </summary>
    /// <param name="stubConfig">受控配置服务替身。</param>
    /// <returns>连接管理服务实例。</returns>
    private DataSourceManagementService CreateServiceOnConfig(StubConfigService stubConfig)
    {
        return new DataSourceManagementService(
            stubConfig, _protector, NullLogger<DataSourceManagementService>.Instance);
    }

    /// <summary>
    /// 构造连接保存草稿，缺省字段取合法值，按参数覆盖。
    /// </summary>
    private static DataSourceConnectionDraft BuildDraft(
        string name = "dev-mysql",
        string host = "127.0.0.1",
        int port = 3306,
        string database = "shop",
        string userId = "root",
        string? password = "secret-P@ss",
        string? originalName = null)
    {
        return new DataSourceConnectionDraft(
            name, DataSourceType.MySql, host, port, database, userId, password, originalName);
    }

    /// <summary>
    /// 配置服务测试替身：可注入写盘失败，供验证保存失败回滚行为。
    /// </summary>
    private sealed class StubConfigService : IConfigService
    {
        /// <summary>
        /// 内存配置快照。
        /// </summary>
        public AppConfig Current { get; } = new AppConfig();

        /// <inheritdoc />
        public string ConfigFilePath => Path.Combine(Path.GetTempPath(), "DbCodeGenTests", "stub-config.json");

        /// <inheritdoc />
        public event EventHandler? ConfigChanged
        {
            // 替身不派发配置变化事件，空访问器仅满足接口契约
            add { }
            remove { }
        }

        /// <inheritdoc />
        public AppConfig Load()
        {
            return Current;
        }

        /// <inheritdoc />
        public void Save()
        {
            throw new ConfigSaveException("模拟写盘失败");
        }

        /// <inheritdoc />
        public void ChangeDataDirectory(string dataDirectory)
        {
            throw new NotSupportedException("测试替身不支持切换数据目录。");
        }

        /// <inheritdoc />
        public GenerationDefaults GetGenerationDefaults()
        {
            return new GenerationDefaults(Current.WorkspaceRoot, Current.LastRelativeOutputRoot);
        }

        /// <inheritdoc />
        public string? GetLlmApiKey()
        {
            return null;
        }
    }
}
