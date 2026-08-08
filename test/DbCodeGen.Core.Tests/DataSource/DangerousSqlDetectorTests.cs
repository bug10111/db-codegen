using DbCodeGen.Core.DataSource;

namespace DbCodeGen.Core.Tests.DataSource;

/// <summary>
/// 危险语句检测器的单元测试，覆盖去注释、去字符串字面量、首关键字大小写不敏感与顶层 WHERE 判定。
/// </summary>
public sealed class DangerousSqlDetectorTests
{
    /// <summary>
    /// DROP 语句无论大小写均应判定为 Drop。
    /// </summary>
    [Theory]
    [InlineData("DROP TABLE orders")]
    [InlineData("drop table orders")]
    [InlineData("  DROP   TABLE   orders  ")]
    public void Detect_DropKeywords_ReturnsDrop(string sql)
    {
        Assert.Equal(DangerousSqlKind.Drop, DangerousSqlDetector.Detect(sql));
    }

    /// <summary>
    /// TRUNCATE 语句应判定为 Truncate。
    /// </summary>
    [Fact]
    public void Detect_TruncateStatement_ReturnsTruncate()
    {
        Assert.Equal(DangerousSqlKind.Truncate, DangerousSqlDetector.Detect("TRUNCATE TABLE orders"));
    }

    /// <summary>
    /// 无 WHERE 的 DELETE 语句应判定为 DeleteWithoutWhere。
    /// </summary>
    [Theory]
    [InlineData("DELETE FROM orders")]
    [InlineData("delete from orders")]
    [InlineData("DELETE   FROM   orders")]
    public void Detect_DeleteWithoutWhere_ReturnsDeleteWithoutWhere(string sql)
    {
        Assert.Equal(DangerousSqlKind.DeleteWithoutWhere, DangerousSqlDetector.Detect(sql));
    }

    /// <summary>
    /// 含顶层 WHERE 的 DELETE 语句视为已限定范围，不应触发确认。
    /// </summary>
    [Fact]
    public void Detect_DeleteWithWhere_ReturnsNone()
    {
        Assert.Equal(DangerousSqlKind.None, DangerousSqlDetector.Detect("DELETE FROM orders WHERE id = 1"));
    }

    /// <summary>
    /// WHERE 出现在子查询中时仍为顶层限定，不应触发确认。
    /// </summary>
    [Fact]
    public void Detect_DeleteWithWhereInSubquery_ReturnsNone()
    {
        const string sql = "DELETE FROM orders WHERE id IN (SELECT order_id FROM items WHERE status = 1)";
        Assert.Equal(DangerousSqlKind.None, DangerousSqlDetector.Detect(sql));
    }

    /// <summary>
    /// 无 WHERE 的 UPDATE 语句应判定为 UpdateWithoutWhere。
    /// </summary>
    [Theory]
    [InlineData("UPDATE orders SET status = 1")]
    [InlineData("update orders set status = 1")]
    public void Detect_UpdateWithoutWhere_ReturnsUpdateWithoutWhere(string sql)
    {
        Assert.Equal(DangerousSqlKind.UpdateWithoutWhere, DangerousSqlDetector.Detect(sql));
    }

    /// <summary>
    /// 含顶层 WHERE 的 UPDATE 语句视为已限定范围，不应触发确认。
    /// </summary>
    [Fact]
    public void Detect_UpdateWithWhere_ReturnsNone()
    {
        Assert.Equal(DangerousSqlKind.None, DangerousSqlDetector.Detect("UPDATE orders SET status = 1 WHERE id = 1"));
    }

    /// <summary>
    /// 块注释包裹危险关键字时，剥离注释后仍应正确判定语句首关键字。
    /// </summary>
    [Fact]
    public void Detect_BlockCommentPrefix_IsStrippedAndKeywordDetected()
    {
        const string sql = "/* 清空历史数据 */ DROP TABLE orders";
        Assert.Equal(DangerousSqlKind.Drop, DangerousSqlDetector.Detect(sql));
    }

    /// <summary>
    /// 行注释包裹危险关键字时，剥离注释后仍应正确判定语句首关键字。
    /// </summary>
    [Fact]
    public void Detect_LineCommentPrefix_IsStrippedAndKeywordDetected()
    {
        const string sql = "-- 清理测试数据\nDELETE FROM orders";
        Assert.Equal(DangerousSqlKind.DeleteWithoutWhere, DangerousSqlDetector.Detect(sql));
    }

    /// <summary>
    /// MySQL 方言井号注释包裹危险关键字时，剥离注释后仍应正确判定语句首关键字。
    /// </summary>
    [Fact]
    public void Detect_HashCommentPrefix_IsStrippedAndKeywordDetected()
    {
        const string sql = "# 清理测试数据\nUPDATE orders SET status = 0";
        Assert.Equal(DangerousSqlKind.UpdateWithoutWhere, DangerousSqlDetector.Detect(sql));
    }

    /// <summary>
    /// WHERE 关键字出现在行注释中时不应参与判定，语句应视为无 WHERE 的 DELETE。
    /// </summary>
    [Fact]
    public void Detect_WhereInsideLineComment_IsStrippedAndDeleteFlagged()
    {
        const string sql = "DELETE FROM orders -- WHERE id = 1";
        Assert.Equal(DangerousSqlKind.DeleteWithoutWhere, DangerousSqlDetector.Detect(sql));
    }

    /// <summary>
    /// WHERE 关键字出现在字符串字面量中时不应参与判定，语句应视为无 WHERE 的 DELETE。
    /// </summary>
    [Fact]
    public void Detect_WhereInsideStringLiteral_IsStrippedAndDeleteFlagged()
    {
        const string sql = "DELETE FROM orders WHERE name = 'where'";
        Assert.Equal(DangerousSqlKind.None, DangerousSqlDetector.Detect(sql));
    }

    /// <summary>
    /// 反引号标识符内的关键字不应参与 WHERE 判定，无 WHERE 子句的 UPDATE 应触发确认。
    /// </summary>
    [Fact]
    public void Detect_WhereInsideBacktickIdentifier_IsStrippedAndUpdateFlagged()
    {
        const string sql = "UPDATE orders SET `where` = 1";
        Assert.Equal(DangerousSqlKind.UpdateWithoutWhere, DangerousSqlDetector.Detect(sql));
    }

    /// <summary>
    /// where_column 等以 where 开头的标识符不应被误判为 WHERE 子句。
    /// </summary>
    [Fact]
    public void Detect_WhereAsIdentifierPrefix_DoesNotCountAsWhereClause()
    {
        const string sql = "UPDATE orders SET where_column = 1 WHERE id = 5";
        Assert.Equal(DangerousSqlKind.None, DangerousSqlDetector.Detect(sql));
    }

    /// <summary>
    /// 非危险语句如 SELECT、INSERT、CREATE 不应触发确认。
    /// </summary>
    [Theory]
    [InlineData("SELECT * FROM orders")]
    [InlineData("INSERT INTO orders VALUES (1, 2)")]
    [InlineData("CREATE TABLE t (id INT)")]
    [InlineData("SHOW TABLES")]
    public void Detect_SafeStatements_ReturnNone(string sql)
    {
        Assert.Equal(DangerousSqlKind.None, DangerousSqlDetector.Detect(sql));
    }

    /// <summary>
    /// 空串与纯空白语句应返回 None。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void Detect_BlankText_ReturnsNone(string sql)
    {
        Assert.Equal(DangerousSqlKind.None, DangerousSqlDetector.Detect(sql));
    }

    /// <summary>
    /// IsDangerous 应与 Detect 结果一致，危险语句返回 true。
    /// </summary>
    [Fact]
    public void IsDangerous_DangerousStatement_ReturnsTrue()
    {
        Assert.True(DangerousSqlDetector.IsDangerous("DROP TABLE orders"));
        Assert.False(DangerousSqlDetector.IsDangerous("SELECT * FROM orders"));
    }

    /// <summary>
    /// 风险描述应按类型返回可读提示，安全类型返回空串。
    /// </summary>
    [Fact]
    public void GetRiskDescription_ReturnsReadableTextPerKind()
    {
        Assert.Contains("DROP", DangerousSqlDetector.GetRiskDescription(DangerousSqlKind.Drop));
        Assert.Contains("TRUNCATE", DangerousSqlDetector.GetRiskDescription(DangerousSqlKind.Truncate));
        Assert.Contains("DELETE", DangerousSqlDetector.GetRiskDescription(DangerousSqlKind.DeleteWithoutWhere));
        Assert.Contains("UPDATE", DangerousSqlDetector.GetRiskDescription(DangerousSqlKind.UpdateWithoutWhere));
        Assert.Equal(string.Empty, DangerousSqlDetector.GetRiskDescription(DangerousSqlKind.None));
    }
}
