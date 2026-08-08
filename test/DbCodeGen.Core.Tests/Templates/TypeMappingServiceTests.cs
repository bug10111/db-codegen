using DbCodeGen.Core.Config;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbCodeGen.Core.Tests.Templates;

/// <summary>
/// 全局类型映射解析服务单元测试，覆盖"全局表>包typeMap>兜底"解析链、去修饰匹配、
/// 多词类型保留与生成前未映射类型预检。
/// </summary>
public sealed class TypeMappingServiceTests
{
    /// <summary>
    /// 全局映射表优先于包 typeMap：同名键两边都有时返回全局表的类型与导包。
    /// </summary>
    [Fact]
    public void Resolve_GlobalMapping_BeatsPackageTypeMap()
    {
        var config = new StubConfigService
        {
            Current = new AppConfig
            {
                TypeMappings = new List<TypeMappingEntry>
                {
                    new() { DbType = "bigint", TargetType = "BigInteger", Import = "java.math.BigInteger" }
                }
            }
        };
        TypeMappingService service = CreateService(config);
        var packageTypeMap = new Dictionary<string, string> { ["bigint"] = "Long" };

        TypeMappingResult result = service.Resolve("bigint", packageTypeMap);

        Assert.True(result.Found);
        Assert.Equal("BigInteger", result.TypeName);
        Assert.Equal("java.math.BigInteger", result.Import);
    }

    /// <summary>
    /// 全局表未命中时应回落到包 typeMap，返回包内类型且无导包。
    /// </summary>
    [Fact]
    public void Resolve_GlobalMiss_FallsBackToPackageTypeMap()
    {
        var service = CreateService(new StubConfigService());
        var packageTypeMap = new Dictionary<string, string> { ["bigint"] = "java.lang.Long" };

        TypeMappingResult result = service.Resolve("bigint", packageTypeMap);

        Assert.True(result.Found);
        Assert.Equal("java.lang.Long", result.TypeName);
        Assert.Null(result.Import);
    }

    /// <summary>
    /// 全局与包均未命中时返回兜底类型并标记未命中。
    /// </summary>
    [Fact]
    public void Resolve_AllMiss_ReturnsFallbackAndNotFound()
    {
        var service = CreateService(new StubConfigService());

        TypeMappingResult result = service.Resolve("jsonb", null);

        Assert.False(result.Found);
        Assert.Equal("String", result.TypeName);
        Assert.Null(result.Import);
    }

    /// <summary>
    /// 匹配应大小写不敏感并去除长度括号后缀，varchar(255) 命中 varchar 条目。
    /// </summary>
    [Theory]
    [InlineData("varchar", "String")]
    [InlineData("VARCHAR", "String")]
    [InlineData("varchar(255)", "String")]
    [InlineData("numeric(10,2)", "BigDecimal")]
    public void Resolve_NormalizesCaseAndParens(string rawDbType, string expected)
    {
        var config = new StubConfigService
        {
            Current = new AppConfig
            {
                TypeMappings = new List<TypeMappingEntry>
                {
                    new() { DbType = "varchar", TargetType = "String" },
                    new() { DbType = "numeric", TargetType = "BigDecimal", Import = "java.math.BigDecimal" }
                }
            }
        };
        TypeMappingService service = CreateService(config);

        TypeMappingResult result = service.Resolve(rawDbType, null);

        Assert.True(result.Found);
        Assert.Equal(expected, result.TypeName);
    }

    /// <summary>
    /// 多词类型（PostgreSQL timestamp with time zone）应整体匹配，不被空格拆分。
    /// </summary>
    [Fact]
    public void Resolve_MultiWordType_MatchesWhole()
    {
        var config = new StubConfigService
        {
            Current = new AppConfig
            {
                TypeMappings = new List<TypeMappingEntry>
                {
                    new() { DbType = "timestamp with time zone", TargetType = "OffsetDateTime", Import = "java.time.OffsetDateTime" },
                    new() { DbType = "timestamp", TargetType = "LocalDateTime", Import = "java.time.LocalDateTime" }
                }
            }
        };
        TypeMappingService service = CreateService(config);

        TypeMappingResult tz = service.Resolve("timestamp with time zone", null);
        TypeMappingResult plain = service.Resolve("timestamp", null);

        Assert.Equal("OffsetDateTime", tz.TypeName);
        Assert.Equal("LocalDateTime", plain.TypeName);
    }

    /// <summary>
    /// 未映射类型预检应归并同类型跨表跨列出现次数，记录首次出现位置，全部命中时为空。
    /// </summary>
    [Fact]
    public void FindUnmappedTypes_MergesOccurrences_AndSkipsMapped()
    {
        var config = new StubConfigService
        {
            Current = new AppConfig
            {
                TypeMappings = new List<TypeMappingEntry> { new() { DbType = "bigint", TargetType = "Long" } }
            }
        };
        TypeMappingService service = CreateService(config);

        var table = new TableInfo { RawName = "sys_user" };
        table.SetColumns(new[]
        {
            new ColumnInfo { RawName = "id", RawDbType = "bigint" },
            new ColumnInfo { RawName = "meta", RawDbType = "jsonb" },
            new ColumnInfo { RawName = "tags", RawDbType = "text[]" }
        });
        var other = new TableInfo { RawName = "sys_role" };
        other.SetColumns(new[]
        {
            new ColumnInfo { RawName = "extra", RawDbType = "jsonb" }
        });

        IReadOnlyList<UnmappedTypeInfo> unmapped = service.FindUnmappedTypes(new[] { table, other }, null);

        Assert.Equal(2, unmapped.Count);
        UnmappedTypeInfo jsonb = unmapped.Single(item => item.DbType == "jsonb");
        Assert.Equal("sys_user", jsonb.TableName);
        Assert.Equal("meta", jsonb.ColumnName);
        Assert.Equal(2, jsonb.Occurrences);
        UnmappedTypeInfo array = unmapped.Single(item => item.DbType == "text[]");
        Assert.Equal("sys_user", array.TableName);
        Assert.Equal("tags", array.ColumnName);
        Assert.Equal(1, array.Occurrences);
    }

    /// <summary>
    /// 全部列均已映射时未映射预检返回空列表。
    /// </summary>
    [Fact]
    public void FindUnmappedTypes_AllMapped_ReturnsEmpty()
    {
        var config = new StubConfigService
        {
            Current = new AppConfig
            {
                TypeMappings = new List<TypeMappingEntry>
                {
                    new() { DbType = "bigint", TargetType = "Long" },
                    new() { DbType = "varchar", TargetType = "String" }
                }
            }
        };
        TypeMappingService service = CreateService(config);

        var table = new TableInfo { RawName = "sys_user" };
        table.SetColumns(new[]
        {
            new ColumnInfo { RawName = "id", RawDbType = "bigint" },
            new ColumnInfo { RawName = "name", RawDbType = "varchar" }
        });

        IReadOnlyList<UnmappedTypeInfo> unmapped = service.FindUnmappedTypes(new[] { table }, null);

        Assert.Empty(unmapped);
    }

    /// <summary>
    /// 配置服务测试替身，仅承载内存配置快照供解析服务读取。
    /// </summary>
    private sealed class StubConfigService : IConfigService
    {
        /// <summary>
        /// 内存配置快照，默认空配置。
        /// </summary>
        public AppConfig Current { get; set; } = new();

        /// <inheritdoc />
        public string ConfigFilePath => "stub";

        /// <inheritdoc />
        public AppConfig Load() => Current;

        /// <inheritdoc />
        public void Save()
        {
        }

        /// <inheritdoc />
        public GenerationDefaults GetGenerationDefaults() => new(string.Empty, string.Empty);

        /// <inheritdoc />
        public string? GetLlmApiKey() => null;
    }

    /// <summary>
    /// 构造指向指定配置替身的类型映射服务。
    /// </summary>
    /// <param name="config">配置替身。</param>
    /// <returns>类型映射服务实例。</returns>
    private static TypeMappingService CreateService(StubConfigService config)
    {
        return new TypeMappingService(config, NullLogger<TypeMappingService>.Instance);
    }
}
