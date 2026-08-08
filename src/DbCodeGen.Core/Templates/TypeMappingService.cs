using DbCodeGen.Core.Config;
using DbCodeGen.Core.Model;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.Core.Templates;

/// <summary>
/// 全局类型映射解析服务实现：按"全局映射表 > 包 typeMap > 兜底"三级解析链解析数据库原始类型，
/// 并支持生成前未映射类型预检。全局映射表实时读取配置服务当前快照，用户修改后立即生效无需重启。
/// </summary>
public sealed class TypeMappingService : ITypeMappingService
{
    private readonly IConfigService _configService;
    private readonly ILogger<TypeMappingService> _logger;

    /// <summary>
    /// 使用配置服务与日志器构造类型映射解析服务。
    /// </summary>
    /// <param name="configService">配置服务，提供全局映射表与包无关的默认解析数据。</param>
    /// <param name="logger">解析服务日志器。</param>
    /// <exception cref="ArgumentNullException">configService 或 logger 为 null 时抛出。</exception>
    public TypeMappingService(IConfigService configService, ILogger<TypeMappingService> logger)
    {
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(logger);
        _configService = configService;
        _logger = logger;
    }

    /// <inheritdoc />
    public TypeMappingResult Resolve(string? rawDbType, IReadOnlyDictionary<string, string>? packageTypeMap, string fallback = "String")
    {
        if (string.IsNullOrWhiteSpace(rawDbType))
        {
            return new TypeMappingResult(false, fallback, null);
        }

        string normalized = TypeMapper.Normalize(rawDbType);

        // 全局映射表优先：用户配置的映射对全部模板包生效
        foreach (TypeMappingEntry entry in _configService.Current.TypeMappings)
        {
            // 跳过空条目与缺键缺值的非法条目，避免配置异常时解析崩溃
            if (entry is null
                || string.IsNullOrWhiteSpace(entry.DbType)
                || string.IsNullOrWhiteSpace(entry.TargetType)
                || TypeMapper.Normalize(entry.DbType) != normalized)
            {
                continue;
            }

            return new TypeMappingResult(
                true,
                entry.TargetType.Trim(),
                string.IsNullOrWhiteSpace(entry.Import) ? null : entry.Import.Trim());
        }

        // 模板包 typeMap 兜底：兼容旧包，包内映射优先级低于全局表
        if (packageTypeMap is not null)
        {
            foreach (KeyValuePair<string, string> pair in packageTypeMap)
            {
                // 跳过空键空值条目，避免畸形包 manifest 导致解析崩溃
                if (pair.Key is null
                    || string.IsNullOrWhiteSpace(pair.Value)
                    || TypeMapper.Normalize(pair.Key) != normalized)
                {
                    continue;
                }

                return new TypeMappingResult(true, pair.Value.Trim(), null);
            }
        }

        // 最终兜底返回默认类型，并标记未命中供预检收集
        return new TypeMappingResult(false, fallback, null);
    }

    /// <inheritdoc />
    public IReadOnlyList<UnmappedTypeInfo> FindUnmappedTypes(IReadOnlyList<TableInfo> tables, IReadOnlyDictionary<string, string>? packageTypeMap)
    {
        ArgumentNullException.ThrowIfNull(tables);

        // 按规范化类型键归并出现次数与首次出现位置，同类型只保留一条
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var firstSeen = new Dictionary<string, UnmappedTypeInfo>(StringComparer.Ordinal);

        foreach (TableInfo table in tables)
        {
            foreach (ColumnInfo column in table.Columns)
            {
                if (string.IsNullOrWhiteSpace(column.RawDbType))
                {
                    continue;
                }

                TypeMappingResult resolved = Resolve(column.RawDbType, packageTypeMap);
                if (resolved.Found)
                {
                    continue;
                }

                string key = TypeMapper.Normalize(column.RawDbType);
                occurrences[key] = occurrences.GetValueOrDefault(key) + 1;
                if (!firstSeen.ContainsKey(key))
                {
                    firstSeen[key] = new UnmappedTypeInfo
                    {
                        DbType = column.RawDbType,
                        TableName = table.RawName,
                        ColumnName = column.RawName
                    };
                }
            }
        }

        List<UnmappedTypeInfo> result = new(firstSeen.Count);
        foreach (KeyValuePair<string, UnmappedTypeInfo> pair in firstSeen)
        {
            pair.Value.Occurrences = occurrences[pair.Key];
            result.Add(pair.Value);
        }

        // 存在未映射类型时记录调试日志，供生成问题排查，日志不含表内敏感数据
        if (result.Count > 0)
        {
            _logger.LogDebug("生成预检发现 {Count} 个未映射数据库类型。", result.Count);
        }

        // 按类型名稳定排序，保证弹窗展示顺序确定
        return result.OrderBy(item => item.DbType, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
