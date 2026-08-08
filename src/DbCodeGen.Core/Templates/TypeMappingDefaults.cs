using DbCodeGen.Core.Model;

namespace DbCodeGen.Core.Templates;

/// <summary>
/// 内置默认类型映射集，覆盖 MySQL 与 PostgreSQL 常见数据库类型到 Java 类型（含导包），
/// 随应用发布，首次启动或旧配置升级时写入全局配置，供用户经"类型映射"窗口增删改。
/// </summary>
public static class TypeMappingDefaults
{
    /// <summary>
    /// 构建内置默认映射条目集合，含数据库原始类型、目标类型与需要的导包。
    /// </summary>
    /// <returns>内置默认映射条目列表。</returns>
    public static IReadOnlyList<TypeMappingEntry> BuildDefault()
    {
        return new List<TypeMappingEntry>
        {
            // 整数类：MySQL 与 PostgreSQL 通用
            new() { DbType = "bigint", TargetType = "Long", Remark = "大整数" },
            new() { DbType = "int", TargetType = "Integer", Remark = "整数" },
            new() { DbType = "mediumint", TargetType = "Integer", Remark = "中整数" },
            new() { DbType = "smallint", TargetType = "Integer", Remark = "小整数" },
            new() { DbType = "tinyint", TargetType = "Integer", Remark = "微型整数" },
            new() { DbType = "serial", TargetType = "Long", Remark = "PG 自增序列" },
            new() { DbType = "bigserial", TargetType = "Long", Remark = "PG 大自增序列" },

            // 布尔与位
            new() { DbType = "bit", TargetType = "Boolean", Remark = "位" },
            new() { DbType = "boolean", TargetType = "Boolean", Remark = "布尔" },
            new() { DbType = "bool", TargetType = "Boolean", Remark = "布尔" },

            // 浮点与定点
            new() { DbType = "float", TargetType = "Float", Remark = "单精度浮点" },
            new() { DbType = "double", TargetType = "Double", Remark = "双精度浮点" },
            new() { DbType = "real", TargetType = "Double", Remark = "PG 双精度浮点" },
            new() { DbType = "decimal", TargetType = "BigDecimal", Import = "java.math.BigDecimal", Remark = "定点小数" },
            new() { DbType = "numeric", TargetType = "BigDecimal", Import = "java.math.BigDecimal", Remark = "定点小数" },
            new() { DbType = "money", TargetType = "BigDecimal", Import = "java.math.BigDecimal", Remark = "货币" },

            // 字符与文本
            new() { DbType = "varchar", TargetType = "String", Remark = "变长字符串" },
            new() { DbType = "char", TargetType = "String", Remark = "定长字符串" },
            new() { DbType = "text", TargetType = "String", Remark = "长文本" },
            new() { DbType = "tinytext", TargetType = "String", Remark = "微型文本" },
            new() { DbType = "mediumtext", TargetType = "String", Remark = "中文本" },
            new() { DbType = "longtext", TargetType = "String", Remark = "长文本" },
            new() { DbType = "character varying", TargetType = "String", Remark = "PG 变长字符串" },
            new() { DbType = "character", TargetType = "String", Remark = "PG 定长字符串" },
            new() { DbType = "clob", TargetType = "String", Remark = "字符大对象" },

            // 二进制
            new() { DbType = "blob", TargetType = "byte[]", Remark = "二进制大对象" },
            new() { DbType = "bytea", TargetType = "byte[]", Remark = "PG 字节数组" },

            // 日期时间
            new() { DbType = "date", TargetType = "LocalDate", Import = "java.time.LocalDate", Remark = "日期" },
            new() { DbType = "datetime", TargetType = "LocalDateTime", Import = "java.time.LocalDateTime", Remark = "日期时间" },
            new() { DbType = "timestamp", TargetType = "LocalDateTime", Import = "java.time.LocalDateTime", Remark = "时间戳" },
            new() { DbType = "timestamptz", TargetType = "OffsetDateTime", Import = "java.time.OffsetDateTime", Remark = "PG 带时区时间戳" },
            new() { DbType = "timestamp with time zone", TargetType = "OffsetDateTime", Import = "java.time.OffsetDateTime", Remark = "PG 带时区时间戳" },
            new() { DbType = "time", TargetType = "LocalTime", Import = "java.time.LocalTime", Remark = "时间" },
            new() { DbType = "time with time zone", TargetType = "OffsetTime", Import = "java.time.OffsetTime", Remark = "PG 带时区时间" },
            new() { DbType = "year", TargetType = "Integer", Remark = "年份" },

            // 结构化与特殊类型
            new() { DbType = "json", TargetType = "String", Remark = "JSON" },
            new() { DbType = "jsonb", TargetType = "String", Remark = "PG JSON 二进制" },
            new() { DbType = "uuid", TargetType = "UUID", Import = "java.util.UUID", Remark = "通用唯一标识" },
            new() { DbType = "inet", TargetType = "String", Remark = "IP 地址" },
            new() { DbType = "cidr", TargetType = "String", Remark = "IP 网段" },
            new() { DbType = "macaddr", TargetType = "String", Remark = "MAC 地址" },
            new() { DbType = "xml", TargetType = "String", Remark = "XML" },
            new() { DbType = "enum", TargetType = "String", Remark = "枚举" },
            new() { DbType = "interval", TargetType = "String", Remark = "PG 时间间隔" }
        };
    }
}
