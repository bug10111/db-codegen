using DbCodeGen.Core.Model;

namespace DbCodeGen.Core.Templates;

/// <summary>
/// 内置默认类型映射集，按数据库类型分桶（通用 / MySQL / PostgreSQL），覆盖常见数据库类型到 Java 类型（含导包）。
/// 随应用发布，首次启动或旧配置升级时写入全局配置，供用户经"类型映射"窗口增删改。
/// </summary>
public static class TypeMappingDefaults
{
    /// <summary>
    /// 构建内置默认映射条目集合，含适用数据库类型、数据库原始类型、目标类型与需要的导包。
    /// 通用条目对所有数据库生效，数据库专属条目仅对对应数据库生效。
    /// </summary>
    /// <returns>内置默认映射条目列表。</returns>
    public static IReadOnlyList<TypeMappingEntry> BuildDefault()
    {
        return new List<TypeMappingEntry>
        {
            // ===== 通用：MySQL 与 PostgreSQL 同类型名且映射一致 =====
            new() { DbType = "bigint", TargetType = "Long", Remark = "大整数" },
            new() { DbType = "smallint", TargetType = "Integer", Remark = "小整数" },
            new() { DbType = "boolean", TargetType = "Boolean", Remark = "布尔" },
            new() { DbType = "bool", TargetType = "Boolean", Remark = "布尔" },
            new() { DbType = "bit", TargetType = "Boolean", Remark = "位" },
            new() { DbType = "decimal", TargetType = "BigDecimal", Import = "java.math.BigDecimal", Remark = "定点小数" },
            new() { DbType = "numeric", TargetType = "BigDecimal", Import = "java.math.BigDecimal", Remark = "定点小数" },
            new() { DbType = "money", TargetType = "BigDecimal", Import = "java.math.BigDecimal", Remark = "货币" },
            new() { DbType = "text", TargetType = "String", Remark = "长文本" },
            new() { DbType = "date", TargetType = "Date", Import = "java.util.Date", Remark = "日期" },
            new() { DbType = "json", TargetType = "String", Remark = "JSON" },
            new() { DbType = "xml", TargetType = "String", Remark = "XML" },
            new() { DbType = "enum", TargetType = "String", Remark = "枚举" },

            // ===== MySQL 专属类型 =====
            new() { DbType = "int", TargetType = "Integer", DatabaseType = DataSourceType.MySql, Remark = "整数" },
            new() { DbType = "mediumint", TargetType = "Integer", DatabaseType = DataSourceType.MySql, Remark = "中整数" },
            new() { DbType = "tinyint", TargetType = "Integer", DatabaseType = DataSourceType.MySql, Remark = "微型整数" },
            new() { DbType = "float", TargetType = "Float", DatabaseType = DataSourceType.MySql, Remark = "单精度浮点" },
            new() { DbType = "double", TargetType = "Double", DatabaseType = DataSourceType.MySql, Remark = "双精度浮点" },
            new() { DbType = "varchar", TargetType = "String", DatabaseType = DataSourceType.MySql, Remark = "变长字符串" },
            new() { DbType = "char", TargetType = "String", DatabaseType = DataSourceType.MySql, Remark = "定长字符串" },
            new() { DbType = "tinytext", TargetType = "String", DatabaseType = DataSourceType.MySql, Remark = "微型文本" },
            new() { DbType = "mediumtext", TargetType = "String", DatabaseType = DataSourceType.MySql, Remark = "中文本" },
            new() { DbType = "longtext", TargetType = "String", DatabaseType = DataSourceType.MySql, Remark = "长文本" },
            new() { DbType = "datetime", TargetType = "Date", Import = "java.util.Date", DatabaseType = DataSourceType.MySql, Remark = "日期时间" },
            new() { DbType = "timestamp", TargetType = "Date", Import = "java.util.Date", DatabaseType = DataSourceType.MySql, Remark = "时间戳" },
            new() { DbType = "time", TargetType = "Date", Import = "java.util.Date", DatabaseType = DataSourceType.MySql, Remark = "时间" },
            new() { DbType = "year", TargetType = "Integer", DatabaseType = DataSourceType.MySql, Remark = "年份" },
            new() { DbType = "blob", TargetType = "byte[]", DatabaseType = DataSourceType.MySql, Remark = "二进制大对象" },
            new() { DbType = "clob", TargetType = "String", DatabaseType = DataSourceType.MySql, Remark = "字符大对象" },
            new() { DbType = "set", TargetType = "String", DatabaseType = DataSourceType.MySql, Remark = "集合" },

            // ===== PostgreSQL 专属类型 =====
            new() { DbType = "integer", TargetType = "Integer", DatabaseType = DataSourceType.PostgreSql, Remark = "整数" },
            new() { DbType = "real", TargetType = "Double", DatabaseType = DataSourceType.PostgreSql, Remark = "单精度浮点" },
            new() { DbType = "double precision", TargetType = "Double", DatabaseType = DataSourceType.PostgreSql, Remark = "双精度浮点" },
            new() { DbType = "character varying", TargetType = "String", DatabaseType = DataSourceType.PostgreSql, Remark = "变长字符串" },
            new() { DbType = "character", TargetType = "String", DatabaseType = DataSourceType.PostgreSql, Remark = "定长字符串" },
            new() { DbType = "serial", TargetType = "Long", DatabaseType = DataSourceType.PostgreSql, Remark = "自增序列" },
            new() { DbType = "bigserial", TargetType = "Long", DatabaseType = DataSourceType.PostgreSql, Remark = "大自增序列" },
            new() { DbType = "timestamp without time zone", TargetType = "Date", Import = "java.util.Date", DatabaseType = DataSourceType.PostgreSql, Remark = "时间戳" },
            new() { DbType = "timestamp with time zone", TargetType = "Date", Import = "java.util.Date", DatabaseType = DataSourceType.PostgreSql, Remark = "带时区时间戳" },
            new() { DbType = "timestamptz", TargetType = "Date", Import = "java.util.Date", DatabaseType = DataSourceType.PostgreSql, Remark = "带时区时间戳别名" },
            new() { DbType = "time without time zone", TargetType = "Date", Import = "java.util.Date", DatabaseType = DataSourceType.PostgreSql, Remark = "时间" },
            new() { DbType = "time with time zone", TargetType = "Date", Import = "java.util.Date", DatabaseType = DataSourceType.PostgreSql, Remark = "带时区时间" },
            new() { DbType = "bytea", TargetType = "byte[]", DatabaseType = DataSourceType.PostgreSql, Remark = "字节数组" },
            new() { DbType = "jsonb", TargetType = "String", DatabaseType = DataSourceType.PostgreSql, Remark = "JSON 二进制" },
            new() { DbType = "uuid", TargetType = "UUID", Import = "java.util.UUID", DatabaseType = DataSourceType.PostgreSql, Remark = "通用唯一标识" },
            new() { DbType = "inet", TargetType = "String", DatabaseType = DataSourceType.PostgreSql, Remark = "IP 地址" },
            new() { DbType = "cidr", TargetType = "String", DatabaseType = DataSourceType.PostgreSql, Remark = "IP 网段" },
            new() { DbType = "macaddr", TargetType = "String", DatabaseType = DataSourceType.PostgreSql, Remark = "MAC 地址" },
            new() { DbType = "interval", TargetType = "String", DatabaseType = DataSourceType.PostgreSql, Remark = "时间间隔" }
        };
    }
}
