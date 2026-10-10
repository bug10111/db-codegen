using System.Runtime.Versioning;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Security;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.Core.DataSource;

/// <summary>
/// 数据源连接管理服务实现，单例注入。承载连接配置的字段校验、唯一名校验、密码 DPAPI 加密、
/// 新增/更新/删除与经配置服务持久化；落盘失败时回滚内存快照，保证内存与磁盘始终一致。
/// 该实现由桌面端数据源管理窗口与 MCP 数据源管理工具共用，是连接增删改的单一权威通道。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DataSourceManagementService : IDataSourceManagementService
{
    private readonly IConfigService _configService;
    private readonly CredentialProtector _protector;
    private readonly ILogger<DataSourceManagementService> _logger;

    /// <summary>
    /// 使用配置服务、凭据保护器与日志器创建连接管理服务实例。
    /// </summary>
    /// <param name="configService">配置持久化服务，连接列表读写的唯一通道。</param>
    /// <param name="credentialProtector">Windows DPAPI 凭据保护器，用于密码加密落盘。</param>
    /// <param name="logger">连接管理服务日志器，日志不得输出明文密码。</param>
    /// <exception cref="ArgumentNullException">任一依赖参数为 null 时抛出。</exception>
    public DataSourceManagementService(
        IConfigService configService,
        CredentialProtector credentialProtector,
        ILogger<DataSourceManagementService> logger)
    {
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(credentialProtector);
        ArgumentNullException.ThrowIfNull(logger);
        _configService = configService;
        _protector = credentialProtector;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<DataSourceConfig> List()
    {
        // 过滤反序列化兜底残留的空元素，保证调用方拿到干净列表；首次访问触发配置加载
        return _configService.Current.DataSources
            .Where(config => config is not null)
            .ToList();
    }

    /// <inheritdoc />
    public DataSourceConfig? GetByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // 与桌面端既有语义一致：名称定位按区分大小写的精确匹配
        return List().FirstOrDefault(config =>
            string.Equals(config.Name, name.Trim(), StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public DataSourceOperationResult Save(DataSourceConnectionDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        // 字段校验为服务端单一权威规则，桌面端表单与 MCP 入参共用，任一不合法直接拒绝且不触碰配置
        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            return DataSourceOperationResult.Failed(DataSourceOperationStatus.Invalid, "连接名称不能为空。");
        }

        if (string.IsNullOrWhiteSpace(draft.Host))
        {
            return DataSourceOperationResult.Failed(DataSourceOperationStatus.Invalid, "主机地址不能为空。");
        }

        if (string.IsNullOrWhiteSpace(draft.Database))
        {
            return DataSourceOperationResult.Failed(DataSourceOperationStatus.Invalid, "数据库名不能为空。");
        }

        if (string.IsNullOrWhiteSpace(draft.UserId))
        {
            return DataSourceOperationResult.Failed(DataSourceOperationStatus.Invalid, "用户名不能为空。");
        }

        // 端口必须为 1-65535 之间的整数，非法不进入持久化
        if (draft.Port is < 1 or > 65535)
        {
            return DataSourceOperationResult.Failed(DataSourceOperationStatus.Invalid, "端口必须为 1-65535 之间的整数。");
        }

        string trimmedName = draft.Name.Trim();
        string? originalName = string.IsNullOrWhiteSpace(draft.OriginalName) ? null : draft.OriginalName;
        AppConfig config = _configService.Current;

        // 编辑场景先按原名称精确匹配定位既有连接，目标缺失直接返回不落盘
        int editingIndex = -1;
        DataSourceConfig? original = null;
        if (originalName is not null)
        {
            editingIndex = config.DataSources.FindIndex(existing =>
                string.Equals(existing.Name, originalName, StringComparison.Ordinal));
            if (editingIndex < 0)
            {
                return DataSourceOperationResult.Failed(
                    DataSourceOperationStatus.OriginalMissing, $"原连接“{originalName}”已不存在。");
            }

            original = CloneDataSourceConfig(config.DataSources[editingIndex]);
        }

        // 唯一名校验：与列表中除自身（按原名称定位）以外的连接忽略大小写比较，避免下拉引用歧义
        bool nameExists = config.DataSources.Any(existing =>
            !string.Equals(existing.Name, originalName, StringComparison.Ordinal) &&
            string.Equals(existing.Name, trimmedName, StringComparison.OrdinalIgnoreCase));
        if (nameExists)
        {
            return DataSourceOperationResult.Failed(
                DataSourceOperationStatus.NameConflict, $"连接名称“{trimmedName}”已存在，请更换名称。");
        }

        DateTime now = DateTime.Now;
        DataSourceConfig target;
        if (originalName is null)
        {
            // 新增连接：构造新配置追加到列表，创建与更新时间统一取当前时间
            target = new DataSourceConfig
            {
                Name = trimmedName,
                Type = draft.Type,
                Host = draft.Host.Trim(),
                Port = draft.Port,
                Database = draft.Database.Trim(),
                UserId = draft.UserId.Trim(),
                CreatedAt = now,
                UpdatedAt = now
            };
            config.DataSources.Add(target);
        }
        else
        {
            // 编辑连接：覆写可编辑字段并保留创建时间，目标对象仍在原列表位置
            target = config.DataSources[editingIndex];
            target.Name = trimmedName;
            target.Type = draft.Type;
            target.Host = draft.Host.Trim();
            target.Port = draft.Port;
            target.Database = draft.Database.Trim();
            target.UserId = draft.UserId.Trim();
            target.UpdatedAt = now;
        }

        // 密码输入了新值才重新加密覆盖，留空保持原密文；明文仅此瞬态，加密后立即脱离调用链
        if (!string.IsNullOrWhiteSpace(draft.PlainPassword))
        {
            target.PasswordCipher = _protector.Encrypt(draft.PlainPassword);
        }

        try
        {
            _configService.Save();
        }
        catch (ConfigSaveException exception)
        {
            // 写盘失败回滚内存快照：新增移除、编辑恢复原值，保证界面与内存、磁盘三方一致
            if (originalName is null)
            {
                config.DataSources.Remove(target);
            }
            else
            {
                config.DataSources[editingIndex] = original!;
            }

            _logger.LogError(exception, "保存数据源连接失败，连接名称：{ConnectionName}。", trimmedName);
            return DataSourceOperationResult.Failed(DataSourceOperationStatus.SaveFailed, exception.Message);
        }

        _logger.LogInformation(
            "已保存数据源连接：{ConnectionName}（{Operation}）。", trimmedName, originalName is null ? "新增" : "更新");
        return DataSourceOperationResult.Successful(target);
    }

    /// <inheritdoc />
    public DataSourceOperationResult Delete(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return DataSourceOperationResult.Failed(DataSourceOperationStatus.NotFound, "连接名称不能为空。");
        }

        AppConfig config = _configService.Current;

        // 名称定位与桌面端既有语义一致：区分大小写精确匹配
        int removedIndex = config.DataSources.FindIndex(existing =>
            string.Equals(existing.Name, name, StringComparison.Ordinal));
        if (removedIndex < 0)
        {
            return DataSourceOperationResult.Failed(
                DataSourceOperationStatus.NotFound, $"连接“{name}”不存在。");
        }

        DataSourceConfig removed = config.DataSources[removedIndex];
        config.DataSources.RemoveAt(removedIndex);
        try
        {
            _configService.Save();
        }
        catch (ConfigSaveException exception)
        {
            // 写盘失败回滚删除，保证内存与磁盘一致
            config.DataSources.Insert(removedIndex, removed);
            _logger.LogError(exception, "删除数据源连接失败，连接名称：{ConnectionName}。", name);
            return DataSourceOperationResult.Failed(DataSourceOperationStatus.SaveFailed, exception.Message);
        }

        _logger.LogInformation("已删除数据源连接：{ConnectionName}。", name);
        return DataSourceOperationResult.Successful(removed);
    }

    /// <summary>
    /// 复制数据源配置为等值新实例，用于保存失败时回滚被编辑的原值。
    /// </summary>
    /// <param name="source">需要复制的数据源连接配置。</param>
    /// <returns>等值的新配置实例。</returns>
    private static DataSourceConfig CloneDataSourceConfig(DataSourceConfig source)
    {
        return new DataSourceConfig
        {
            Name = source.Name,
            Type = source.Type,
            Host = source.Host,
            Port = source.Port,
            Database = source.Database,
            UserId = source.UserId,
            PasswordCipher = source.PasswordCipher,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt
        };
    }
}
