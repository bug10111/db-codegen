using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Versioning;
using DbCodeGen.Core.DataSource;
using DbCodeGen.Core.Model;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Npgsql;

namespace DbCodeGen.Core.Tests.DataSource;

/// <summary>
/// SqlExecutor SQL 执行服务的单元测试，用内存假连接与假读取器覆盖查询/执行/截断/异常与脱敏分支。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SqlExecutorTests
{
    /// <summary>
    /// 构造执行服务实例，注入空日志器与指定的假连接服务。
    /// </summary>
    /// <param name="dataSourceService">假连接服务。</param>
    /// <returns>执行服务实例。</returns>
    private static SqlExecutor CreateExecutor(FakeDataSourceService dataSourceService)
    {
        return new SqlExecutor(dataSourceService, NullLogger<SqlExecutor>.Instance);
    }

    /// <summary>
    /// 构造数据源配置，密码密文为空，由假连接服务自行忽略。
    /// </summary>
    /// <returns>数据源配置。</returns>
    private static DataSourceConfig CreateConfig()
    {
        return new DataSourceConfig
        {
            Name = "dev",
            Type = DataSourceType.MySql,
            Host = "127.0.0.1",
            Port = 3306,
            Database = "shop",
            UserId = "root"
        };
    }

    /// <summary>
    /// 构造两行两列的查询结果读取器，覆盖查询分支的列与行读取。
    /// </summary>
    /// <returns>假读取器实例。</returns>
    private static FakeDbDataReader CreateQueryReader()
    {
        return new FakeDbDataReader(
            columnNames: new[] { "id", "name" },
            columnTypes: new[] { typeof(int), typeof(string) },
            rows: new object?[][]
            {
                new object?[] { 1, "alice" },
                new object?[] { 2, "bob" }
            });
    }

    /// <summary>
    /// 构造指定字段数与行数的读取器，用于截断与空结果测试。
    /// </summary>
    /// <param name="rowCount">结果行数。</param>
    /// <returns>假读取器实例。</returns>
    private static FakeDbDataReader CreateSingleColumnReader(int rowCount)
    {
        List<object?[]> rows = new(rowCount);
        for (int index = 0; index < rowCount; index++)
        {
            rows.Add(new object?[] { index });
        }

        return new FakeDbDataReader(
            columnNames: new[] { "value" },
            columnTypes: new[] { typeof(int) },
            rows: rows);
    }

    /// <summary>
    /// 通过内部构造函数创建 MySqlException 实例，用于覆盖驱动异常分支。
    /// </summary>
    /// <param name="message">异常消息。</param>
    /// <returns>MySqlException 实例，错误号按内部构造默认值为 0。</returns>
    private static MySqlException CreateMySqlException(string message)
    {
        return (MySqlException)Activator.CreateInstance(
            typeof(MySqlException),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: new object[] { message },
            culture: null)!;
    }

    /// <summary>
    /// config 为 null 时应抛出参数空异常。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NullConfig_ThrowsArgumentNullException()
    {
        FakeDataSourceService service = new(new FakeDbConnection(() => throw new NotSupportedException()));
        SqlExecutor executor = CreateExecutor(service);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => executor.ExecuteAsync(null!, "SELECT 1", null, CancellationToken.None));
    }

    /// <summary>
    /// SQL 为空或纯空白时应抛出参数异常，不发起执行。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_BlankSql_ThrowsArgumentException(string sql)
    {
        FakeDataSourceService service = new(new FakeDbConnection(() => throw new NotSupportedException()));
        SqlExecutor executor = CreateExecutor(service);

        await Assert.ThrowsAsync<ArgumentException>(
            () => executor.ExecuteAsync(CreateConfig(), sql, null, CancellationToken.None));
    }

    /// <summary>
    /// 查询分支应读取列定义与结果行，成功且不含影响行数。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_QueryBranch_ReadsColumnsAndRows()
    {
        FakeDbCommand command = new(CreateQueryReader());
        FakeDataSourceService service = new(new FakeDbConnection(() => command));
        SqlExecutor executor = CreateExecutor(service);

        SqlExecutionResult result = await executor.ExecuteAsync(
            CreateConfig(), "SELECT id, name FROM users", null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Columns.Count);
        Assert.Equal("id", result.Columns[0].Name);
        Assert.Equal("Int32", result.Columns[0].DisplayType);
        Assert.Equal("name", result.Columns[1].Name);
        Assert.Equal("String", result.Columns[1].DisplayType);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(1, result.Rows[0][0]);
        Assert.Equal("alice", result.Rows[0][1]);
        Assert.Equal(2, result.Rows[1][0]);
        Assert.Equal("bob", result.Rows[1][1]);
        Assert.False(result.Truncated);
        Assert.Null(result.AffectedRows);
        Assert.True(result.Duration > TimeSpan.Zero);
        Assert.Equal(1, service.OpenCallCount);
    }

    /// <summary>
    /// 结果行数超过 MaxResultRows 时应截断并置 Truncated，仅保留上限行数。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_QueryBranch_TruncatesAtMaxResultRows()
    {
        FakeDbCommand command = new(CreateSingleColumnReader(rowCount: 1200));
        FakeDataSourceService service = new(new FakeDbConnection(() => command));
        SqlExecutor executor = CreateExecutor(service);
        SqlExecutionOptions options = new() { MaxResultRows = 1000 };

        SqlExecutionResult result = await executor.ExecuteAsync(
            CreateConfig(), "SELECT value FROM big_table", options, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1000, result.Rows.Count);
        Assert.True(result.Truncated);
    }

    /// <summary>
    /// MaxResultRows 非法为零时应钳制为至少读取一行，防止无限截断。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_QueryBranch_MaxResultRowsZero_ClampedToOneRow()
    {
        FakeDbCommand command = new(CreateSingleColumnReader(rowCount: 2));
        FakeDataSourceService service = new(new FakeDbConnection(() => command));
        SqlExecutor executor = CreateExecutor(service);
        SqlExecutionOptions options = new() { MaxResultRows = 0 };

        SqlExecutionResult result = await executor.ExecuteAsync(
            CreateConfig(), "SELECT value FROM t", options, CancellationToken.None);

        Assert.Single(result.Rows);
        Assert.True(result.Truncated);
    }

    /// <summary>
    /// 执行分支应在关闭 reader 后再读取 RecordsAffected，成功返回影响行数。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ExecutionBranch_ReadsRecordsAffectedAfterClose()
    {
        FakeDbDataReader reader = new(
            columnNames: Array.Empty<string>(),
            columnTypes: Array.Empty<Type>(),
            rows: Array.Empty<object?[]>(),
            recordsAffected: 5);
        FakeDbCommand command = new(reader);
        FakeDataSourceService service = new(new FakeDbConnection(() => command));
        SqlExecutor executor = CreateExecutor(service);

        SqlExecutionResult result = await executor.ExecuteAsync(
            CreateConfig(), "UPDATE users SET status = 1", null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(5, result.AffectedRows);
        Assert.Empty(result.Columns);
        Assert.Empty(result.Rows);
        Assert.True(reader.WasClosedBeforeRecordsAffectedRead);
    }

    /// <summary>
    /// DDL 语句返回 -1 影响行数时，结果应原样承载，供调用方按执行成功展示。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ExecutionBranch_DdlNegativeAffectedRows_IsPreserved()
    {
        FakeDbDataReader reader = new(
            columnNames: Array.Empty<string>(),
            columnTypes: Array.Empty<Type>(),
            rows: Array.Empty<object?[]>(),
            recordsAffected: -1);
        FakeDbCommand command = new(reader);
        FakeDataSourceService service = new(new FakeDbConnection(() => command));
        SqlExecutor executor = CreateExecutor(service);

        SqlExecutionResult result = await executor.ExecuteAsync(
            CreateConfig(), "CREATE TABLE t (id INT)", null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(-1, result.AffectedRows);
    }

    /// <summary>
    /// 未传执行参数时应采用默认超时 30 秒，并写入命令。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_DefaultOptions_UsesDefaultTimeout()
    {
        FakeDbCommand command = new(CreateQueryReader());
        FakeDataSourceService service = new(new FakeDbConnection(() => command));
        SqlExecutor executor = CreateExecutor(service);

        await executor.ExecuteAsync(CreateConfig(), "SELECT 1", null, CancellationToken.None);

        Assert.Equal(30, command.TimeoutUsed);
    }

    /// <summary>
    /// 显式传入执行参数时超时应写入命令，且查询分支不读取影响行数。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CustomOptions_UsesConfiguredTimeout()
    {
        FakeDbCommand command = new(CreateQueryReader());
        FakeDataSourceService service = new(new FakeDbConnection(() => command));
        SqlExecutor executor = CreateExecutor(service);
        SqlExecutionOptions options = new() { TimeoutSeconds = 45 };

        await executor.ExecuteAsync(CreateConfig(), "SELECT 1", options, CancellationToken.None);

        Assert.Equal(45, command.TimeoutUsed);
    }

    /// <summary>
    /// 建立连接失败时返回失败结果，错误码来自 MySql 错误号，消息脱敏不含密码段。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_OpenConnectionMySqlError_ReturnsFailureWithErrorCode()
    {
        FakeDataSourceService service = new(new FakeDbConnection(() => throw new NotSupportedException()))
        {
            OpenException = CreateMySqlException("Access denied for user 'root'@'localhost' (using password: YES)")
        };
        SqlExecutor executor = CreateExecutor(service);

        SqlExecutionResult result = await executor.ExecuteAsync(
            CreateConfig(), "SELECT 1", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("0", result.ErrorCode);
        Assert.Contains("Access denied", result.ErrorMessage);
    }

    /// <summary>
    /// PostgreSQL 服务端错误应映射 SqlState 为错误码。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CommandPostgresError_ReturnsSqlState()
    {
        PostgresException postgresException = new(
            messageText: "relation \"users\" does not exist",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: "42P01",
            detail: null,
            hint: null,
            position: 0,
            internalPosition: 0,
            internalQuery: null,
            where: null,
            schemaName: null,
            tableName: null,
            columnName: null,
            dataTypeName: null,
            constraintName: null,
            file: null,
            line: null,
            routine: null);
        FakeDbCommand command = new(CreateQueryReader()) { ExecuteException = postgresException };
        FakeDataSourceService service = new(new FakeDbConnection(() => command));
        SqlExecutor executor = CreateExecutor(service);

        SqlExecutionResult result = await executor.ExecuteAsync(
            CreateConfig(), "SELECT * FROM missing", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("42P01", result.ErrorCode);
        Assert.Contains("does not exist", result.ErrorMessage);
    }

    /// <summary>
    /// Npgsql 非服务端错误（连接/协议层）应捕获并返回脱敏消息，错误码为空。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CommandNpgsqlError_ReturnsSanitizedMessage()
    {
        FakeDbCommand command = new(CreateQueryReader())
        {
            ExecuteException = new NpgsqlException("Failed to read from the socket")
        };
        FakeDataSourceService service = new(new FakeDbConnection(() => command));
        SqlExecutor executor = CreateExecutor(service);

        SqlExecutionResult result = await executor.ExecuteAsync(
            CreateConfig(), "SELECT 1", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.ErrorCode);
        Assert.Contains("socket", result.ErrorMessage);
    }

    /// <summary>
    /// 取消令牌已触发时返回已取消结果，而非超时提示。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Cancelled_ReturnsCancelledMessage()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        FakeDbCommand command = new(CreateQueryReader())
        {
            ExecuteException = new OperationCanceledException(cts.Token)
        };
        FakeDataSourceService service = new(new FakeDbConnection(() => command));
        SqlExecutor executor = CreateExecutor(service);

        SqlExecutionResult result = await executor.ExecuteAsync(
            CreateConfig(), "SELECT 1", null, cts.Token);

        Assert.False(result.Success);
        Assert.Equal("操作已取消。", result.ErrorMessage);
    }

    /// <summary>
    /// 非调用方取消的取消异常与超时异常均应反馈执行超时提示。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Timeout_ReturnsTimeoutMessage()
    {
        FakeDbCommand command = new(CreateQueryReader())
        {
            ExecuteException = new TimeoutException("Command timeout")
        };
        FakeDataSourceService service = new(new FakeDbConnection(() => command));
        SqlExecutor executor = CreateExecutor(service);

        SqlExecutionResult result = await executor.ExecuteAsync(
            CreateConfig(), "SELECT 1", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("超时", result.ErrorMessage);
    }

    /// <summary>
    /// 错误消息中嵌入连接串的密码段应被掩码，防止凭据随错误信息泄漏。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ErrorMessage_DoesNotLeakPasswordSegment()
    {
        FakeDbCommand command = new(CreateQueryReader())
        {
            ExecuteException = CreateMySqlException(
                "无法连接到服务器 Server=127.0.0.1;Port=3306;User ID=root;Password=secret123;Database=shop")
        };
        FakeDataSourceService service = new(new FakeDbConnection(() => command));
        SqlExecutor executor = CreateExecutor(service);

        SqlExecutionResult result = await executor.ExecuteAsync(
            CreateConfig(), "SELECT 1", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain("secret123", result.ErrorMessage);
        Assert.Contains("Password=*****", result.ErrorMessage);
    }

    /// <summary>
    /// 假连接服务，固定返回同一个假连接，可注入打开异常与记录打开次数。
    /// </summary>
    private sealed class FakeDataSourceService : IDataSourceService
    {
        private readonly DbConnection _connection;

        /// <summary>
        /// 建立连接时抛出的异常，为空时正常返回假连接。
        /// </summary>
        public Exception? OpenException { get; set; }

        /// <summary>
        /// 建立连接被调用的次数。
        /// </summary>
        public int OpenCallCount { get; private set; }

        /// <summary>
        /// 以固定假连接构造连接服务。
        /// </summary>
        /// <param name="connection">假连接。</param>
        public FakeDataSourceService(DbConnection connection)
        {
            _connection = connection;
        }

        /// <inheritdoc />
        public string BuildConnectionString(DataSourceConfig config) => "fake";

        /// <inheritdoc />
        public Task<TestConnectionResult> TestConnectionAsync(TestConnectionInput input, CancellationToken ct)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<DbConnection> OpenConnectionAsync(DataSourceConfig config, CancellationToken ct)
        {
            OpenCallCount++;
            if (OpenException is not null)
            {
                return Task.FromException<DbConnection>(OpenException);
            }

            return Task.FromResult(_connection);
        }
    }

    /// <summary>
    /// 假连接，仅承载命令创建与状态流转，其余成员不参与执行。
    /// </summary>
    private sealed class FakeDbConnection : DbConnection
    {
        private readonly Func<DbCommand> _commandFactory;
        private System.Data.ConnectionState _state = System.Data.ConnectionState.Closed;

        /// <summary>
        /// 以命令工厂构造假连接。
        /// </summary>
        /// <param name="commandFactory">创建假命令的委托。</param>
        public FakeDbConnection(Func<DbCommand> commandFactory)
        {
            _commandFactory = commandFactory;
        }

        /// <inheritdoc />
        [AllowNull]
        public override string ConnectionString { get; set; } = "fake";

        /// <inheritdoc />
        public override string Database => "shop";

        /// <inheritdoc />
        public override string DataSource => "fake";

        /// <inheritdoc />
        public override string ServerVersion => "fake";

        /// <inheritdoc />
        public override System.Data.ConnectionState State => _state;

        /// <inheritdoc />
        public override void ChangeDatabase(string databaseName)
        {
        }

        /// <inheritdoc />
        public override void Close() => _state = System.Data.ConnectionState.Closed;

        /// <inheritdoc />
        public override void Open() => _state = System.Data.ConnectionState.Open;

        /// <inheritdoc />
        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            _state = System.Data.ConnectionState.Open;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        protected override DbCommand CreateDbCommand() => _commandFactory();

        /// <inheritdoc />
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// 假命令，记录超时与执行次数，可注入执行异常并返回固定读取器。
    /// </summary>
    private sealed class FakeDbCommand : DbCommand
    {
        private readonly DbDataReader _readerToReturn;

        /// <summary>
        /// 执行读取器时抛出的异常，为空时正常返回固定读取器。
        /// </summary>
        public Exception? ExecuteException { get; set; }

        /// <summary>
        /// 执行读取器时记录的命令超时秒数。
        /// </summary>
        public int? TimeoutUsed { get; private set; }

        /// <summary>
        /// 执行读取器的次数。
        /// </summary>
        public int ExecutedTimes { get; private set; }

        /// <summary>
        /// 以固定读取器构造假命令。
        /// </summary>
        /// <param name="readerToReturn">执行时返回的读取器。</param>
        public FakeDbCommand(DbDataReader readerToReturn)
        {
            _readerToReturn = readerToReturn;
        }

        /// <inheritdoc />
        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        /// <inheritdoc />
        public override int CommandTimeout { get; set; }

        /// <inheritdoc />
        public override CommandType CommandType { get; set; }

        /// <inheritdoc />
        public override bool DesignTimeVisible { get; set; }

        /// <inheritdoc />
        public override UpdateRowSource UpdatedRowSource { get; set; }

        /// <inheritdoc />
        [AllowNull]
        protected override DbConnection DbConnection { get; set; } = null!;

        /// <inheritdoc />
        [AllowNull]
        protected override DbTransaction DbTransaction { get; set; } = null!;

        /// <inheritdoc />
        protected override DbParameterCollection DbParameterCollection => null!;

        /// <inheritdoc />
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior, CancellationToken cancellationToken)
        {
            ExecutedTimes++;
            TimeoutUsed = CommandTimeout;
            if (ExecuteException is not null)
            {
                return Task.FromException<DbDataReader>(ExecuteException);
            }

            return Task.FromResult(_readerToReturn);
        }

        /// <inheritdoc />
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public override int ExecuteNonQuery() => throw new NotSupportedException();

        /// <inheritdoc />
        public override object? ExecuteScalar() => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Prepare()
        {
        }

        /// <inheritdoc />
        public override void Cancel()
        {
        }

        /// <inheritdoc />
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
    }

    /// <summary>
    /// 假读取器，按注入的列与行提供查询读取，RecordsAffected 仅在关闭后可用以验证读取时序。
    /// </summary>
    private sealed class FakeDbDataReader : DbDataReader
    {
        private readonly string[] _columnNames;
        private readonly Type[] _columnTypes;
        private readonly IReadOnlyList<object?[]> _rows;
        private readonly int _recordsAffected;
        private int _rowIndex = -1;
        private bool _closed;

        /// <summary>
        /// RecordsAffected 是否在关闭后读取，验证执行分支的读取时序。
        /// </summary>
        public bool WasClosedBeforeRecordsAffectedRead { get; private set; }

        /// <summary>
        /// 以列名、列类型与结果行构造假读取器。
        /// </summary>
        /// <param name="columnNames">列名集合。</param>
        /// <param name="columnTypes">列类型集合。</param>
        /// <param name="rows">结果行集合。</param>
        /// <param name="recordsAffected">影响行数，默认 -1 表示不适用。</param>
        public FakeDbDataReader(
            string[] columnNames, Type[] columnTypes, IReadOnlyList<object?[]> rows, long recordsAffected = -1)
        {
            _columnNames = columnNames;
            _columnTypes = columnTypes;
            _rows = rows;
            _recordsAffected = (int)recordsAffected;
        }

        /// <inheritdoc />
        public override int FieldCount => _columnNames.Length;

        /// <inheritdoc />
        public override int RecordsAffected
        {
            get
            {
                if (!_closed)
                {
                    throw new InvalidOperationException("RecordsAffected 仅能在读取器关闭后读取。");
                }

                WasClosedBeforeRecordsAffectedRead = true;
                return _recordsAffected;
            }
        }

        /// <inheritdoc />
        public override bool HasRows => _rows.Count > 0;

        /// <inheritdoc />
        public override bool IsClosed => _closed;

        /// <inheritdoc />
        public override int Depth => 0;

        /// <inheritdoc />
        public override object this[int ordinal] => GetValue(ordinal);

        /// <inheritdoc />
        public override object this[string name] => GetValue(GetOrdinal(name));

        /// <inheritdoc />
        public override bool Read()
        {
            _rowIndex++;
            return _rowIndex < _rows.Count;
        }

        /// <inheritdoc />
        public override Task<bool> ReadAsync(CancellationToken cancellationToken)
            => Task.FromResult(Read());

        /// <inheritdoc />
        public override string GetName(int ordinal) => _columnNames[ordinal];

        /// <inheritdoc />
        public override Type GetFieldType(int ordinal) => _columnTypes[ordinal];

        /// <inheritdoc />
        public override object GetValue(int ordinal) => _rows[_rowIndex][ordinal] ?? DBNull.Value;

        /// <inheritdoc />
        public override int GetValues(object[] values)
        {
            object?[] row = _rows[_rowIndex];
            int count = Math.Min(values.Length, row.Length);
            for (int index = 0; index < count; index++)
            {
                values[index] = row[index] ?? DBNull.Value;
            }

            return count;
        }

        /// <inheritdoc />
        public override bool IsDBNull(int ordinal) => _rows[_rowIndex][ordinal] is null;

        /// <inheritdoc />
        public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken)
            => Task.FromResult(IsDBNull(ordinal));

        /// <inheritdoc />
        public override int GetOrdinal(string name)
        {
            for (int index = 0; index < _columnNames.Length; index++)
            {
                if (string.Equals(_columnNames[index], name, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            throw new IndexOutOfRangeException($"未找到列 {name}。");
        }

        /// <inheritdoc />
        public override string GetDataTypeName(int ordinal) => _columnTypes[ordinal].Name;

        /// <inheritdoc />
        public override void Close() => _closed = true;

        /// <inheritdoc />
        public override Task CloseAsync()
        {
            _closed = true;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override bool NextResult() => false;

        /// <inheritdoc />
        public override IEnumerator GetEnumerator() => throw new NotSupportedException();

        /// <inheritdoc />
        public override bool GetBoolean(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override byte GetByte(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public override char GetChar(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override double GetDouble(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override float GetFloat(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override Guid GetGuid(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override short GetInt16(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override int GetInt32(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override long GetInt64(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override string GetString(int ordinal) => throw new NotSupportedException();
    }
}
