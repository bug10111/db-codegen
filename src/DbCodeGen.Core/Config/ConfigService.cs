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

        int diskVersion = loaded.Version;
        AppConfig normalized = Normalize(loaded);

        // 结构迁移后立即原子落盘：磁盘配置始终与生效配置一致，旧版本文件被原子替换，
        // 避免"打开即关不落盘"时磁盘残留旧版本、下次再次触发迁移
        if (normalized.Version != diskVersion)
        {
            try
            {
                await SaveToDiskAsync(normalized).ConfigureAwait(false);
            }
            catch (ConfigSaveException exception)
            {
                // 迁移落盘失败仅记警告，本次仍以内存迁移结果生效，不阻断启动
                _logger.LogWarning(exception, "配置结构迁移后落盘失败，本次仅保留内存迁移结果。");
            }
        }

        return normalized;
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
            AiReferenceFileLimits = new AiReferenceFileLimits
            {
                MaxFileCount = AiReferenceFileLimits.DefaultMaxFileCount,
                MaxSingleFileBytes = AiReferenceFileLimits.DefaultMaxSingleFileBytes,
                MaxTotalBytes = AiReferenceFileLimits.DefaultMaxTotalBytes
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
        config.TemplateFileStates ??= new Dictionary<string, List<TemplateFileState>>();

        // AI 参考文件限制子模型为 null 时兜底为默认实例，防止下游读取空引用
        config.AiReferenceFileLimits ??= new AiReferenceFileLimits();

        // 任一限制字段非正数时恢复对应默认常量，防止手工编辑或历史配置产生非法上限
        if (config.AiReferenceFileLimits.MaxFileCount <= 0)
        {
            config.AiReferenceFileLimits.MaxFileCount = AiReferenceFileLimits.DefaultMaxFileCount;
        }
        if (config.AiReferenceFileLimits.MaxSingleFileBytes <= 0)
        {
            config.AiReferenceFileLimits.MaxSingleFileBytes = AiReferenceFileLimits.DefaultMaxSingleFileBytes;
        }
        if (config.AiReferenceFileLimits.MaxTotalBytes <= 0)
        {
            config.AiReferenceFileLimits.MaxTotalBytes = AiReferenceFileLimits.DefaultMaxTotalBytes;
        }

        // 单文件上限大于总大小上限时收敛为总大小，防止上传校验死锁（单文件永远无法通过校验）
        if (config.AiReferenceFileLimits.MaxSingleFileBytes > config.AiReferenceFileLimits.MaxTotalBytes)
        {
            config.AiReferenceFileLimits.MaxSingleFileBytes = config.AiReferenceFileLimits.MaxTotalBytes;
        }

        // 清理按包记忆的勾选态中的空键、空值清单与清单内空元素，防止下游读取空引用
        foreach ((string key, List<TemplateFileState>? states) in config.TemplateFileStates.ToList())
        {
            if (string.IsNullOrWhiteSpace(key) || states is null)
            {
                config.TemplateFileStates.Remove(key);
                continue;
            }

            states.RemoveAll(state => state is null);
        }

        // 旧版本配置升级：映射模型 v3 引入按数据库类型分桶（通用/MySQL/PostgreSQL），
        // 迁移时保留用户已有条目，仅追加按库默认集中尚未覆盖的条目，避免覆盖用户自定义映射
        if (config.Version < 3)
        {
            config.TypeMappings = MergeWithTypeMappingDefaults(config.TypeMappings);
            config.Version = 3;
        }

        // 嵌套子模型内为 null 的字符串字段兜底补默认值，防止下游读取空引用
        config.WorkspaceRoot ??= string.Empty;
        config.LastRelativeOutputRoot ??= string.Empty;
        config.Llm.BaseUrl ??= LlmConfig.DefaultBaseUrl;
        config.Llm.ApiKeyEncrypted ??= string.Empty;
        config.Llm.Model ??= LlmConfig.DefaultModel;

        // 请求超时非正数时恢复默认 300，防止手工编辑或历史配置产生非法超时值导致请求瞬间超时或无限挂起
        if (config.Llm.TimeoutSeconds < 1)
        {
            config.Llm.TimeoutSeconds = LlmConfig.DefaultTimeoutSeconds;
        }

        return config;
    }

    /// <summary>
    /// 将旧版扁平映射与新版按库默认集合并：保留用户已有合法条目（旧条目无库作用域视为通用），
    /// 仅追加默认集中未被现有条目覆盖的条目，按"适用数据库 + 规范化数据库类型"判碰撞。
    /// 迁移不覆盖用户自定义，避免升级丢失自定义映射。
    /// </summary>
    /// <param name="existing">配置中已有的映射条目，可为空。</param>
    /// <returns>合并后的映射条目列表。</returns>
    private static List<TypeMappingEntry> MergeWithTypeMappingDefaults(IEnumerable<TypeMappingEntry>? existing)
    {
        List<TypeMappingEntry> merged = new();
        var covered = new HashSet<string>(StringComparer.Ordinal);

        foreach (TypeMappingEntry entry in existing ?? new List<TypeMappingEntry>())
        {
            // 跳过空条目与缺键缺值的非法条目，保证合并结果干净可用
            if (entry is null || string.IsNullOrWhiteSpace(entry.DbType) || string.IsNullOrWhiteSpace(entry.TargetType))
            {
                continue;
            }

            merged.Add(entry);
            covered.Add(BuildTypeMappingKey(entry));
        }

        foreach (TypeMappingEntry def in TypeMappingDefaults.BuildDefault())
        {
            if (covered.Add(BuildTypeMappingKey(def)))
            {
                merged.Add(def);
            }
        }

        return merged;
    }

    /// <summary>
    /// 构建映射条目碰撞键：适用数据库类型 + 规范化数据库类型，用于合并去重。
    /// </summary>
    /// <param name="entry">映射条目。</param>
    /// <returns>碰撞键文本。</returns>
    private static string BuildTypeMappingKey(TypeMappingEntry entry)
    {
        return $"{entry.DatabaseType}|{TypeMapper.Normalize(entry.DbType)}";
    }
}
