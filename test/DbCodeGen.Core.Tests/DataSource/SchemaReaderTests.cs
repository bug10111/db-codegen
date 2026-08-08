using DbCodeGen.Core.DataSource;
using DbCodeGen.Core.Model;
using MySqlConnector;
using Npgsql;

namespace DbCodeGen.Core.Tests.DataSource;

/// <summary>
/// 元数据读取相关单元测试，覆盖表/列命名转换、列分组派生与读取器工厂创建。
/// </summary>
public sealed class SchemaReaderTests
{
    /// <summary>
    /// 原始表名应转换为 PascalCase 类名，下划线分隔与大小写边界均按单词拆分。
    /// </summary>
    [Theory]
    [InlineData("user_info", "UserInfo")]
    [InlineData("order", "Order")]
    [InlineData("t_user", "TUser")]
    [InlineData("order_detail_item", "OrderDetailItem")]
    [InlineData("OrderDetail", "OrderDetail")]
    [InlineData("USER_INFO", "UserInfo")]
    [InlineData("", "")]
    public void ToPascalCase_ConvertsRawNameToClassName(string rawName, string expected)
    {
        string className = TableInfo.ToPascalCase(rawName);

        Assert.Equal(expected, className);
    }

    /// <summary>
    /// 原始表名应转换为 camelCase 变量名，首段保持小写，后续段首字母大写。
    /// </summary>
    [Theory]
    [InlineData("user_info", "userInfo")]
    [InlineData("order", "order")]
    [InlineData("t_user", "tUser")]
    [InlineData("order_detail_item", "orderDetailItem")]
    [InlineData("OrderDetail", "orderDetail")]
    [InlineData("USER_INFO", "userInfo")]
    [InlineData("", "")]
    public void ToCamelCase_ConvertsRawNameToVariableName(string rawName, string expected)
    {
        string variableName = TableInfo.ToCamelCase(rawName);

        Assert.Equal(expected, variableName);
    }

    /// <summary>
    /// 填充列集合后应派生主键/全量/非主键子集，全量列与列集合同源，子集按主键标记拆分。
    /// </summary>
    [Fact]
    public void SetColumns_PartitionsColumnsIntoPrimaryAndOtherGroups()
    {
        var idColumn = new ColumnInfo { RawName = "id", IsPrimaryKey = true, AutoIncrement = true };
        var nameColumn = new ColumnInfo { RawName = "name", IsPrimaryKey = false };
        var table = new TableInfo { RawName = "user" };

        table.SetColumns(new[] { idColumn, nameColumn });

        Assert.Equal(2, table.Columns.Count);
        Assert.Same(table.Columns, table.FullColumn);
        ColumnInfo primaryKey = Assert.Single(table.PrimaryKeys);
        Assert.Equal("id", primaryKey.RawName);
        ColumnInfo otherColumn = Assert.Single(table.OtherColumn);
        Assert.Equal("name", otherColumn.RawName);
    }

    /// <summary>
    /// 未填充列集合时各列分组应保持空集合，保证下游遍历安全。
    /// </summary>
    [Fact]
    public void TableInfo_WithoutColumns_HasEmptyColumnGroups()
    {
        var table = new TableInfo { RawName = "user" };

        Assert.Empty(table.Columns);
        Assert.Empty(table.PrimaryKeys);
        Assert.Empty(table.FullColumn);
        Assert.Empty(table.OtherColumn);
    }

    /// <summary>
    /// 工厂按 MySql 类型应创建 MySql 方言读取器并绑定传入连接。
    /// </summary>
    [Fact]
    public void Create_MySql_ReturnsMySqlSchemaReader()
    {
        var factory = new SchemaReaderFactory();
        using var connection = new MySqlConnection();

        using ISchemaReader reader = factory.Create(DataSourceType.MySql, connection);

        Assert.IsType<MySqlSchemaReader>(reader);
    }

    /// <summary>
    /// 工厂按 PostgreSql 类型应创建 PostgreSql 方言读取器并绑定传入连接。
    /// </summary>
    [Fact]
    public void Create_PostgreSql_ReturnsPostgreSqlSchemaReader()
    {
        var factory = new SchemaReaderFactory();
        using var connection = new NpgsqlConnection();

        using ISchemaReader reader = factory.Create(DataSourceType.PostgreSql, connection);

        Assert.IsType<PostgreSqlSchemaReader>(reader);
    }

    /// <summary>
    /// MySql 类型传入 Npgsql 连接时工厂应抛参数异常，阻止错误驱动组合运行。
    /// </summary>
    [Fact]
    public void Create_MySqlWithNpgsqlConnection_ThrowsArgumentException()
    {
        var factory = new SchemaReaderFactory();
        using var connection = new NpgsqlConnection();

        Assert.Throws<ArgumentException>(() => factory.Create(DataSourceType.MySql, connection));
    }

    /// <summary>
    /// PostgreSql 类型传入 MySql 连接时工厂应抛参数异常，阻止错误驱动组合运行。
    /// </summary>
    [Fact]
    public void Create_PostgreSqlWithMySqlConnection_ThrowsArgumentException()
    {
        var factory = new SchemaReaderFactory();
        using var connection = new MySqlConnection();

        Assert.Throws<ArgumentException>(() => factory.Create(DataSourceType.PostgreSql, connection));
    }

    /// <summary>
    /// 空连接传入工厂应抛参数空异常。
    /// </summary>
    [Fact]
    public void Create_NullConnection_ThrowsArgumentNullException()
    {
        var factory = new SchemaReaderFactory();

        Assert.Throws<ArgumentNullException>(() => factory.Create(DataSourceType.MySql, null!));
    }
}
