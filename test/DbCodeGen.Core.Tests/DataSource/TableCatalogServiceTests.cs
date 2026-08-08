using System.Data.Common;
using DbCodeGen.Core.DataSource;
using DbCodeGen.Core.Model;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;

namespace DbCodeGen.Core.Tests.DataSource;

/// <summary>
/// TableCatalogService 编排服务的单元测试，覆盖表清单读取、表详情按（连接名, 表名）缓存与 ClearCache 失效。
/// 数据源连接与方言读取器均以伪实现替换，不发起真实数据库连接。
/// </summary>
public sealed class TableCatalogServiceTests
{
    /// <summary>
    /// 读取表清单应返回伪读取器的表集合，且首屏表项不携带列元数据。
    /// </summary>
    [Fact]
    public async Task GetTablesAsync_ReturnsReaderTableListWithoutColumns()
    {
        var table = new TableInfo { RawName = "user", SchemaName = "shop", Comment = "用户表" };
        var reader = new FakeSchemaReader(new[] { table });
        TableCatalogService service = CreateService(reader);

        IReadOnlyList<TableInfo> tables = await service.GetTablesAsync(CreateConfig("dev"), CancellationToken.None);

        TableInfo first = Assert.Single(tables);
        Assert.Equal("user", first.RawName);
        Assert.Equal("用户表", first.Comment);
        Assert.Empty(first.Columns);
    }

    /// <summary>
    /// 表详情首次读取应走查询，再次读取同一（连接名, 表名）应命中缓存不重复查询。
    /// </summary>
    [Fact]
    public async Task GetTableDetailAsync_SameKey_SecondCallHitsCache()
    {
        var reader = new FakeSchemaReader(Array.Empty<TableInfo>());
        TableCatalogService service = CreateService(reader);
        DataSourceConfig config = CreateConfig("dev");

        TableInfo first = await service.GetTableDetailAsync(config, "user", CancellationToken.None);
        TableInfo second = await service.GetTableDetailAsync(config, "user", CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, reader.GetTableCallCount);
    }

    /// <summary>
    /// 不同连接名读取同一表名应视为不同缓存键，分别触发查询。
    /// </summary>
    [Fact]
    public async Task GetTableDetailAsync_DifferentConnectionName_QueriesSeparately()
    {
        var reader = new FakeSchemaReader(Array.Empty<TableInfo>());
        TableCatalogService service = CreateService(reader);

        await service.GetTableDetailAsync(CreateConfig("dev"), "user", CancellationToken.None);
        await service.GetTableDetailAsync(CreateConfig("prod"), "user", CancellationToken.None);

        Assert.Equal(2, reader.GetTableCallCount);
    }

    /// <summary>
    /// 同一连接名读取不同表名应视为不同缓存键，分别触发查询。
    /// </summary>
    [Fact]
    public async Task GetTableDetailAsync_DifferentTableName_QueriesSeparately()
    {
        var reader = new FakeSchemaReader(Array.Empty<TableInfo>());
        TableCatalogService service = CreateService(reader);

        await service.GetTableDetailAsync(CreateConfig("dev"), "user", CancellationToken.None);
        await service.GetTableDetailAsync(CreateConfig("dev"), "order", CancellationToken.None);

        Assert.Equal(2, reader.GetTableCallCount);
    }

    /// <summary>
    /// 清空缓存后再次读取同一键应重新触发查询，供刷新表与换连接失效陈旧元数据。
    /// </summary>
    [Fact]
    public async Task GetTableDetailAsync_ClearCache_QueriesAgain()
    {
        var reader = new FakeSchemaReader(Array.Empty<TableInfo>());
        TableCatalogService service = CreateService(reader);
        DataSourceConfig config = CreateConfig("dev");

        await service.GetTableDetailAsync(config, "user", CancellationToken.None);
        service.ClearCache();
        await service.GetTableDetailAsync(config, "user", CancellationToken.None);

        Assert.Equal(2, reader.GetTableCallCount);
    }

    /// <summary>
    /// 表名为空或空白时应抛参数异常，不进入查询流程。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task GetTableDetailAsync_BlankTableName_ThrowsArgumentException(string tableName)
    {
        var reader = new FakeSchemaReader(Array.Empty<TableInfo>());
        TableCatalogService service = CreateService(reader);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetTableDetailAsync(CreateConfig("dev"), tableName, CancellationToken.None));
    }

    /// <summary>
    /// 构造测试用数据源配置。
    /// </summary>
    /// <param name="name">连接名。</param>
    /// <returns>数据源配置。</returns>
    private static DataSourceConfig CreateConfig(string name)
    {
        return new DataSourceConfig
        {
            Name = name,
            Type = DataSourceType.MySql,
            Host = "127.0.0.1",
            Port = 3306,
            Database = "shop",
            UserId = "root",
            PasswordCipher = string.Empty
        };
    }

    /// <summary>
    /// 以伪数据源服务与伪读取器构造编排服务。
    /// </summary>
    /// <param name="reader">共享的伪读取器实例，用于断言查询次数。</param>
    /// <returns>编排服务实例。</returns>
    private static TableCatalogService CreateService(FakeSchemaReader reader)
    {
        return new TableCatalogService(
            new FakeDataSourceService(),
            new FakeSchemaReaderFactory(reader),
            NullLogger<TableCatalogService>.Instance);
    }

    /// <summary>
    /// 伪数据源服务，打开连接返回未打开的空 MySql 连接，不发起真实网络请求。
    /// </summary>
    private sealed class FakeDataSourceService : IDataSourceService
    {
        /// <inheritdoc />
        public string BuildConnectionString(DataSourceConfig config) => string.Empty;

        /// <inheritdoc />
        public Task<TestConnectionResult> TestConnectionAsync(TestConnectionInput input, CancellationToken ct)
        {
            return Task.FromResult(new TestConnectionResult { IsSuccess = true, Message = "ok" });
        }

        /// <inheritdoc />
        public Task<DbConnection> OpenConnectionAsync(DataSourceConfig config, CancellationToken ct)
        {
            return Task.FromResult<DbConnection>(new MySqlConnection());
        }
    }

    /// <summary>
    /// 伪读取器工厂，始终返回注入的伪读取器实例，便于断言查询次数。
    /// </summary>
    private sealed class FakeSchemaReaderFactory : ISchemaReaderFactory
    {
        private readonly ISchemaReader _reader;

        /// <summary>
        /// 以固定读取器构造工厂。
        /// </summary>
        /// <param name="reader">伪读取器实例。</param>
        public FakeSchemaReaderFactory(ISchemaReader reader)
        {
            _reader = reader;
        }

        /// <inheritdoc />
        public ISchemaReader Create(DataSourceType type, DbConnection connection) => _reader;
    }

    /// <summary>
    /// 伪方言读取器，返回预置表清单与按需构造的表详情，并记录详情查询次数。
    /// </summary>
    private sealed class FakeSchemaReader : ISchemaReader
    {
        private readonly IReadOnlyList<TableInfo> _tables;

        /// <summary>
        /// 以预置表清单构造伪读取器。
        /// </summary>
        /// <param name="tables">表清单。</param>
        public FakeSchemaReader(IReadOnlyList<TableInfo> tables)
        {
            _tables = tables;
        }

        /// <summary>
        /// 表详情查询累计次数，用于断言缓存行为。
        /// </summary>
        public int GetTableCallCount { get; private set; }

        /// <inheritdoc />
        public Task<IReadOnlyList<TableInfo>> GetTablesAsync(CancellationToken ct) => Task.FromResult(_tables);

        /// <inheritdoc />
        public Task<TableInfo> GetTableAsync(string tableName, CancellationToken ct)
        {
            GetTableCallCount++;
            return Task.FromResult(new TableInfo { RawName = tableName });
        }

        /// <inheritdoc />
        public void Dispose()
        {
        }
    }
}
