using System.Runtime.Versioning;
using DbCodeGen.Core.DataSource;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Npgsql;

namespace DbCodeGen.Core.Tests.DataSource;

/// <summary>
/// DataSourceService 连接服务的单元测试，覆盖连接串组装防注入、密码解析二选一、端口校验与脱敏。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DataSourceServiceTests
{
    private readonly CredentialProtector _protector = new();

    /// <summary>
    /// 创建连接服务实例，注入空日志器，密码保护器使用真实 DPAPI 以贴近生产。
    /// </summary>
    /// <returns>连接服务实例。</returns>
    private DataSourceService CreateService()
    {
        return new DataSourceService(_protector, NullLogger<DataSourceService>.Instance);
    }

    /// <summary>
    /// 构造密码已加密的数据源配置。
    /// </summary>
    /// <param name="type">数据库类型。</param>
    /// <param name="port">端口号。</param>
    /// <param name="password">明文密码，加密后写入配置。</param>
    /// <returns>数据源配置。</returns>
    private DataSourceConfig CreateConfig(DataSourceType type, int port = 3306, string password = "p@ssw0rd")
    {
        return new DataSourceConfig
        {
            Name = "dev",
            Type = type,
            Host = "127.0.0.1",
            Port = port,
            Database = "shop",
            UserId = "root",
            PasswordCipher = _protector.Encrypt(password)
        };
    }

    /// <summary>
    /// 构造测试连接输入。
    /// </summary>
    /// <param name="type">数据库类型。</param>
    /// <param name="port">端口号。</param>
    /// <param name="plainPassword">明文密码。</param>
    /// <param name="savedPasswordCipher">已保存密文。</param>
    /// <returns>测试连接输入。</returns>
    private static TestConnectionInput CreateInput(
        DataSourceType type, int port, string? plainPassword, string? savedPasswordCipher)
    {
        return new TestConnectionInput
        {
            Type = type,
            Host = "127.0.0.1",
            Port = port,
            Database = "shop",
            UserId = "root",
            PlainPassword = plainPassword,
            SavedPasswordCipher = savedPasswordCipher
        };
    }

    /// <summary>
    /// MySql 连接串应包含主机、端口、库、用户与解密后的密码，且可被驱动解析回原值。
    /// </summary>
    [Fact]
    public void BuildConnectionString_MySql_ContainsExpectedSegments()
    {
        DataSourceService service = CreateService();
        DataSourceConfig config = CreateConfig(DataSourceType.MySql, port: 3306, password: "secret123");

        string connectionString = service.BuildConnectionString(config);

        var builder = new MySqlConnectionStringBuilder(connectionString);
        Assert.Equal("127.0.0.1", builder.Server);
        Assert.Equal(3306u, builder.Port);
        Assert.Equal("shop", builder.Database);
        Assert.Equal("root", builder.UserID);
        Assert.Equal("secret123", builder.Password);
    }

    /// <summary>
    /// PostgreSql 连接串应包含主机、端口、库、用户与解密后的密码，且可被驱动解析回原值。
    /// </summary>
    [Fact]
    public void BuildConnectionString_PostgreSql_ContainsExpectedSegments()
    {
        DataSourceService service = CreateService();
        DataSourceConfig config = CreateConfig(DataSourceType.PostgreSql, port: 5432, password: "secret123");

        string connectionString = service.BuildConnectionString(config);

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        Assert.Equal("127.0.0.1", builder.Host);
        Assert.Equal(5432, builder.Port);
        Assert.Equal("shop", builder.Database);
        Assert.Equal("root", builder.Username);
        Assert.Equal("secret123", builder.Password);
    }

    /// <summary>
    /// 含分号与等号的密码应作为单一值被连接串构建器转义，解析回原值且不产生额外连接键。
    /// </summary>
    [Fact]
    public void BuildConnectionString_PasswordWithSpecialCharacters_IsParsedAsSingleValue()
    {
        DataSourceService service = CreateService();
        const string trickyPassword = "p@ss;Server=evil;Port=6666;=x";
        DataSourceConfig config = CreateConfig(DataSourceType.MySql, port: 3306, password: trickyPassword);

        string connectionString = service.BuildConnectionString(config);

        var builder = new MySqlConnectionStringBuilder(connectionString);
        Assert.Equal(trickyPassword, builder.Password);
        Assert.Equal("127.0.0.1", builder.Server);
        Assert.Equal(3306u, builder.Port);
    }

    /// <summary>
    /// 端口越界时组装连接串应抛出参数越界异常，非法端口不进入连接串组装。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void BuildConnectionString_InvalidPort_ThrowsArgumentOutOfRangeException(int invalidPort)
    {
        DataSourceService service = CreateService();
        DataSourceConfig config = CreateConfig(DataSourceType.MySql, port: invalidPort);

        Assert.Throws<ArgumentOutOfRangeException>(() => service.BuildConnectionString(config));
    }

    /// <summary>
    /// 密文为空时按空密码组装连接串，解析回的密码应为空串。
    /// </summary>
    [Fact]
    public void BuildConnectionString_EmptyCipher_ResolvesToEmptyPassword()
    {
        DataSourceService service = CreateService();
        DataSourceConfig config = CreateConfig(DataSourceType.MySql);
        config.PasswordCipher = string.Empty;

        string connectionString = service.BuildConnectionString(config);

        var builder = new MySqlConnectionStringBuilder(connectionString);
        Assert.Equal(string.Empty, builder.Password);
    }

    /// <summary>
    /// 明文与密文同时存在时明文应优先，密文分支不被使用。
    /// </summary>
    [Fact]
    public void ResolvePassword_PlainPasswordNonNull_TakesPriorityOverCipher()
    {
        DataSourceService service = CreateService();
        TestConnectionInput input = CreateInput(
            DataSourceType.MySql, 3306, "plain-secret", _protector.Encrypt("cipher-secret"));

        string resolved = service.ResolvePassword(input);

        Assert.Equal("plain-secret", resolved);
    }

    /// <summary>
    /// 明文为空时应对已保存密文解密，还原出保存时写入的明文密码。
    /// </summary>
    [Fact]
    public void ResolvePassword_PlainPasswordNull_DecryptsSavedCipher()
    {
        DataSourceService service = CreateService();
        TestConnectionInput input = CreateInput(
            DataSourceType.MySql, 3306, null, _protector.Encrypt("cipher-secret"));

        string resolved = service.ResolvePassword(input);

        Assert.Equal("cipher-secret", resolved);
    }

    /// <summary>
    /// 明文与密文皆空时按空密码解析，保证未保存连接按空密码尝试的契约。
    /// </summary>
    [Fact]
    public void ResolvePassword_BothNull_ReturnsEmptyString()
    {
        DataSourceService service = CreateService();
        TestConnectionInput input = CreateInput(DataSourceType.PostgreSql, 5432, null, null);

        string resolved = service.ResolvePassword(input);

        Assert.Equal(string.Empty, resolved);
    }

    /// <summary>
    /// 测试连接遇越界端口应直接返回失败结果，不发起真实网络连接。
    /// </summary>
    [Fact]
    public async Task TestConnectionAsync_InvalidPort_ReturnsFailureResultWithoutConnecting()
    {
        DataSourceService service = CreateService();
        TestConnectionInput input = CreateInput(DataSourceType.MySql, 70000, "secret", null);

        TestConnectionResult result = await service.TestConnectionAsync(input, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("端口", result.Message);
    }

    /// <summary>
    /// 建立连接遇越界端口应抛出参数越界异常，端口校验前置保护调用方。
    /// </summary>
    [Fact]
    public async Task OpenConnectionAsync_InvalidPort_ThrowsArgumentOutOfRangeException()
    {
        DataSourceService service = CreateService();
        DataSourceConfig config = CreateConfig(DataSourceType.MySql, port: 0);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.OpenConnectionAsync(config, CancellationToken.None));
    }

    /// <summary>
    /// 连接本机未监听端口应返回失败结果，且失败信息不含明文密码与连接串密码段。
    /// </summary>
    [Fact]
    public async Task TestConnectionAsync_UnreachableLocalPort_ReturnsFailureWithoutPassword()
    {
        DataSourceService service = CreateService();
        TestConnectionInput input = CreateInput(DataSourceType.MySql, 1, "secret", null);

        TestConnectionResult result = await service.TestConnectionAsync(input, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain("secret", result.Message);
        Assert.DoesNotContain("Password=", result.Message);
    }

    /// <summary>
    /// 建立连接时本机未监听端口应抛出 MySql 驱动连接异常，由调用方按连接失败处理。
    /// </summary>
    [Fact]
    public async Task OpenConnectionAsync_UnreachableMySqlPort_ThrowsMySqlException()
    {
        DataSourceService service = CreateService();
        DataSourceConfig config = CreateConfig(DataSourceType.MySql, port: 1, password: "secret");

        await Assert.ThrowsAsync<MySqlException>(
            () => service.OpenConnectionAsync(config, CancellationToken.None));
    }

    /// <summary>
    /// 建立连接时本机未监听端口应抛出 Npgsql 驱动连接异常，由调用方按连接失败处理。
    /// </summary>
    [Fact]
    public async Task OpenConnectionAsync_UnreachablePostgreSqlPort_ThrowsNpgsqlException()
    {
        DataSourceService service = CreateService();
        DataSourceConfig config = CreateConfig(DataSourceType.PostgreSql, port: 1, password: "secret");

        await Assert.ThrowsAsync<NpgsqlException>(
            () => service.OpenConnectionAsync(config, CancellationToken.None));
    }

    /// <summary>
    /// 脱敏应仅掩码 Password 键值段，保留其余连接段与段前分隔符。
    /// </summary>
    [Fact]
    public void MaskPassword_MasksPasswordSegmentAndKeepsOtherSegments()
    {
        const string connectionString =
            "Server=127.0.0.1;Port=3306;User ID=root;Password=secret123;Database=shop;Connection Timeout=10";

        string masked = DataSourceService.MaskPassword(connectionString);

        Assert.Contains(";Password=*****", masked);
        Assert.DoesNotContain("secret123", masked);
        Assert.Contains("Server=127.0.0.1", masked);
        Assert.Contains("Database=shop", masked);
    }

    /// <summary>
    /// 引号包裹的密码段应整体掩码，段内分号不破坏脱敏结果。
    /// </summary>
    [Fact]
    public void MaskPassword_QuotedPasswordSegment_IsMaskedEntirely()
    {
        const string connectionString = "Server=127.0.0.1;Port=3306;Password=\"a;b=1\";Database=shop";

        string masked = DataSourceService.MaskPassword(connectionString);

        Assert.Contains("Password=*****", masked);
        Assert.DoesNotContain("a;b=1", masked);
    }
}
