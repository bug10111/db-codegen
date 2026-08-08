using System.Text.Json.Serialization;

namespace DbCodeGen.Core.Model;

/// <summary>
/// 数据库类型枚举，标识受支持的关系型数据库驱动。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataSourceType
{
    /// <summary>
    /// MySQL 数据库，使用 MySqlConnector 驱动连接。
    /// </summary>
    MySql,

    /// <summary>
    /// PostgreSQL 数据库，使用 Npgsql 驱动连接。
    /// </summary>
    PostgreSql
}
