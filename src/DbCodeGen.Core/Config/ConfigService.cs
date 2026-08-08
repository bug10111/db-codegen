using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Security;
using DbCodeGen.Core.Templates;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.Core.Config;

/// <summary>
/// 配置持久化服务实现，是 config.json 的唯一读写方，单例注入。
/// 公共门面为同步方法，文件 IO 在内部以异步 API 执行；Load/Save/Current 均在信号量锁内操作保证并发安全。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConfigService : IConfigService, IDisposable
{
    private readonly CredentialProtector _protector;
    private readonly ILogger<ConfigService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AppConfig? _current;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// 使用指定凭据保护器与日志器创建配置服务实例。
    /// </summary>
    /// <param name="credentialProtector">Windows DPAPI 凭据加解密器，用于 apiKey 的密文转换。</param>
    /// <param name="logger">配置服务日志器，日志不得输出明文密钥。</param>
    /// <param name="configFilePath">配置文件绝对路径；为空时默认 %AppData%\DbCodeGen\config.json。</param>
    /// <exception cref="ArgumentNullException">credentialProtector 或 logger 为 null 时抛出。</exception>
    public ConfigService(
        CredentialProtector credentialProtector,
        ILogger<ConfigService> logger,
        string? configFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(credentialProtector);
        ArgumentNullException.ThrowIfNull(logger);
        _protector = credentialProtector;
        _logger = logger;
        ConfigFilePath = configFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DbCodeGen",
            "config.json");
    }

    /// <inheritdoc />
    public string ConfigFilePath { get; }

    /// <inheritdoc />
    public AppConfig Load()
    {
        _gate.Wait();
        try
        {
            EnsureLoadedLocked();
            return _current!;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Save()
    {
        _gate.Wait();
        try
        {
            EnsureLoadedLocked();

            // 同步门面按契约在锁内桥接内部异步文件 IO，原子写失败抛 ConfigSaveException 且内存原值不变
            SaveToDiskAsync(_current!).GetAwaiter().GetResult();
        }
        finally
        {
            _gate.Release();
        }

        // 成功落盘后在锁外触发变化通知，回调内可安全再次读写配置
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public event EventHandler? ConfigChanged;

    /// <inheritdoc />
    public AppConfig Current
    {
        get
        {
            _gate.Wait();
            try
            {
                EnsureLoadedLocked();
                return _current!;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <inheritdoc />
    public GenerationDefaults GetGenerationDefaults()
    {
        _gate.Wait();
        try
        {
            EnsureLoadedLocked();
            return new GenerationDefaults(_current!.WorkspaceRoot, _current.LastRelativeOutputRoot);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public string? GetLlmApiKey()
    {
        _gate.Wait();
        try
        {
            EnsureLoadedLocked();
            string cipher = _current!.Llm?.ApiKeyEncrypted ?? string.Empty;

            // 密文为空表示未配置 apiKey，直接返回 null 不做解密
            if (string.IsNullOrEmpty(cipher))
            {
                return null;
            }

            // 解密在锁内完成，保证与内存快照一致；明文仅本次返回，调用方用后即弃
            return _protector.Decrypt(cipher);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 释放信号量锁等非托管资源。
    /// </summary>
    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <summary>
    /// 在锁内确保配置已加载，未加载时从磁盘读取或生成默认配置。
    /// </summary>
    private void EnsureLoadedLocked()
    {
        // 已加载过则直接返回内存权威快照，保证 Load 幂等
        if (_current is not null)
        {
            return;
        }

        // 同步门面桥接内部异步文件 IO，锁内阻塞等待完成；内部 await 均已 ConfigureAwait(false)，避免 UI 线程死锁
        _current = LoadCoreAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 从磁盘读取配置文件；文件不存在按首次启动生成默认并落盘，读取或解析失败按损坏备份重建。
    /// </summary>
    /// <returns>加载或重建后的配置实例。</returns>
    private async Task<AppConfig> LoadCoreAsync()
    {
        // 文件不存在视为首次启动，生成默认配置并原子落盘
        if (!File.Exists(ConfigFilePath))
        {
            return await CreateDefaultAndPersistAsync().ConfigureAwait(false);
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(ConfigFilePath).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 文件可读性异常视为损坏，备份后重建，应用不崩溃
            _logger.LogWarning(exception, "配置文件读取失败，按损坏处理并备份重建。");
            return await RebuildFromCorruptAsync().ConfigureAwait(false);
        }

        AppConfig? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            // JSON 结构损坏，备份原文件后按默认重建
            _logger.LogWarning(exception, "配置文件 JSON 解析失败，按损坏处理并备份重建。");
            return await RebuildFromCorruptAsync().ConfigureAwait(false);
        }

        if (loaded is null)
        {
            // 文件内容为 null，视为损坏重建
            _logger.LogWarning("配置文件内容为空，按损坏处理并备份重建。");
            return await RebuildFromCorruptAsync().ConfigureAwait(false);
        }

        return Normalize(loaded);
    }

    /// <summary>
    /// 首次启动路径：生成默认配置并原子落盘，落盘失败时仅保留内存默认值，不阻断应用启动。
    /// </summary>
    private async Task<AppConfig> CreateDefaultAndPersistAsync()
    {
        AppConfig defaults = BuildDefaultConfig();
        try
        {
            await SaveToDiskAsync(defaults).ConfigureAwait(false);
        }
        catch (ConfigSaveException exception)
        {
            _logger.LogWarning(exception, "生成默认配置文件落盘失败，将仅保留内存默认配置。");
        }

        return defaults;
    }

    /// <summary>
    /// 损坏恢复路径：先将损坏文件备份为带时间戳的副本，再按默认配置重建并原子落盘。
    /// </summary>
    private async Task<AppConfig> RebuildFromCorruptAsync()
    {
        string backupPath = BackupCorruptFile();
        AppConfig defaults = BuildDefaultConfig();
        try
        {
            await SaveToDiskAsync(defaults).ConfigureAwait(false);
        }
        catch (ConfigSaveException exception)
        {
            _logger.LogWarning(exception, "重建默认配置文件落盘失败，将仅保留内存默认配置。");
        }

        _logger.LogWarning("配置文件已损坏，已按默认配置重建，备份文件：{BackupPath}。", backupPath);
        return defaults;
    }

    /// <summary>
    /// 将当前损坏配置文件复制为带时间戳的备份副本，备份失败不阻断重建流程。
    /// </summary>
    /// <returns>备份文件绝对路径；备份失败返回空串。</returns>
    private string BackupCorruptFile()
    {
        string timestamp = DateTime.Now.ToString("yyyyMMddHHmmssfff");
        string backupPath = $"{ConfigFilePath}.bak.{timestamp}";
        try
        {
            // 原损坏文件复制为带时间戳的备份副本，保留现场供用户排查
            File.Copy(ConfigFilePath, backupPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 备份失败不阻断重建流程，仅记录警告并返回空串表示无备份
            _logger.LogWarning(exception, "备份损坏配置文件失败，原路径：{ConfigFilePath}。", ConfigFilePath);
            return string.Empty;
        }

        return backupPath;
    }

    /// <summary>
    /// 将指定配置序列化后原子写入配置文件：先写同目录临时文件，再覆盖移动到目标位置，避免写盘中断产生半截文件。
    /// </summary>
    /// <param name="config">需要持久化的配置实例。</param>
    /// <exception cref="ConfigSaveException">写盘失败时抛出，内存配置不受影响。</exception>
    private async Task SaveToDiskAsync(AppConfig config)
    {
        try
        {
            string json = JsonSerializer.Serialize(config, JsonOptions);

            // 目标目录不存在时自动创建，保证首次启动即可落盘
            string configDirectory = Path.GetDirectoryName(ConfigFilePath)!;
            Directory.CreateDirectory(configDirectory);

            // 先写同目录临时文件，完成后再覆盖移动，避免写盘中段产生半截配置文件
            string tempPath = ConfigFilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).ConfigureAwait(false);
            File.Move(tempPath, ConfigFilePath, overwrite: true);
        }
        catch (Exception exception)
        {
            // 写盘异常清理残留临时文件后抛结构化异常，内存中的原值保持不动
            TryDeleteTempFile();
            throw new ConfigSaveException("配置保存失败，内存中的原值已保留，可稍后重试。", exception);
        }
    }

    /// <summary>
    /// 清理原子写可能残留的临时文件，清理失败仅记录日志，不影响主流程。
    /// </summary>
    private void TryDeleteTempFile()
    {
        try
        {
            string tempPath = ConfigFilePath + ".tmp";
            // 仅清理已存在的残留临时文件，避免误删正式配置文件
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("清理配置临时文件失败，路径：{TempPath}。", ConfigFilePath + ".tmp");
        }
    }

    /// <summary>
    /// 构造首次启动的默认配置：默认 DashScope 兼容端点、qwen-plus 模型与默认模板搜索目录。
    /// </summary>
    private static AppConfig BuildDefaultConfig()
    {
        string templatesDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DbCodeGen",
            "Templates");

        return new AppConfig
        {
            Version = 3,
            WorkspaceRoot = string.Empty,
            LastRelativeOutputRoot = string.Empty,
            Llm = new LlmConfig
            {
                BaseUrl = LlmConfig.DefaultBaseUrl,
                ApiKeyEncrypted = string.Empty,
                Model = LlmConfig.DefaultModel
            },
            TemplateSearchDirectories = new List<string> { templatesDirectory },
            DataSources = new List<DataSourceConfig>(),
            TypeMappings = TypeMappingDefaults.BuildDefault().ToList()
        };
    }

    /// <summary>
    /// 兜底补全反序列化后可能为 null 的嵌套子模型与集合，并剔除列表中的空元素，防止下游读取空引用。
    /// </summary>
    private static AppConfig Normalize(AppConfig config)
    {
        config.Llm ??= new LlmConfig();
        config.TemplateSearchDirectories ??= new List<string>();
        config.DataSources ??= new List<DataSourceConfig>();
        config.TypeMappings ??= new List<TypeMappingEntry>();
        config.TemplateSearchDirectories.RemoveAll(item => item is null);
        config.DataSources.RemoveAll(item => item is null);
        config.TypeMappings.RemoveAll(item => item is null);

        // 旧版本配置升级：映射模型 v3 引入按数据库类型分桶（通用/MySQL/PostgreSQL），
        // 迁移时用新的按库默认集整体重灌映射表。该迁移为一次性的结构性升级，
        // 覆盖旧的无库作用域映射，用户可在映射窗口继续自定义
        if (config.Version < 3)
        {
            config.TypeMappings = TypeMappingDefaults.BuildDefault().ToList();
            config.Version = 3;
        }

        // 嵌套子模型内为 null 的字符串字段兜底补默认值，防止下游读取空引用
        config.WorkspaceRoot ??= string.Empty;
        config.LastRelativeOutputRoot ??= string.Empty;
        config.Llm.BaseUrl ??= LlmConfig.DefaultBaseUrl;
        config.Llm.ApiKeyEncrypted ??= string.Empty;
        config.Llm.Model ??= LlmConfig.DefaultModel;
        return config;
    }
}
