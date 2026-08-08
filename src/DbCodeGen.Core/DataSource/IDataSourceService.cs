using System.Data.Common;
using DbCodeGen.Core.Model;

namespace DbCodeGen.Core.DataSource;

/// <summary>
/// 数据源连接服务契约，统一承载连接串组装、测试连接与建立连接，供表浏览/SQL 执行面板复用。
/// 密码明文只在解析与连接串构建的瞬态存在，调用方不得将返回值直接落日志或错误信息。
/// </summary>
public interface IDataSourceService
{
    /// <summary>
    /// 依据数据源配置组装数据库连接串，密码经 DPAPI 解密后通过驱动连接串构建器写入，规避连接串注入。
    /// </summary>
    /// <param name="config">数据源连接配置，内部解密 PasswordCipher 密文。</param>
    /// <returns>含明文密码段的连接串，调用方不得直接落日志。</returns>
    /// <exception cref="ArgumentNullException">config 为 null 时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">端口不在 1-65535 范围时抛出。</exception>
    string BuildConnectionString(DataSourceConfig config);

    /// <summary>
    /// 测试数据源连接是否可用，连接超时默认 10 秒，失败返回可读信息且不含密码。
    /// </summary>
    /// <param name="input">测试连接输入，含明文/密文二选一的密码契约。</param>
    /// <param name="ct">取消令牌，取消时返回失败结果而非抛出。</param>
    /// <returns>测试连接结果，含成功与否、可读信息与服务端版本。</returns>
    /// <exception cref="ArgumentNullException">input 为 null 时抛出。</exception>
    Task<TestConnectionResult> TestConnectionAsync(TestConnectionInput input, CancellationToken ct);

    /// <summary>
    /// 建立并打开数据库连接，供表浏览/SQL 执行面板复用；连接生命周期归调用方以 await using 释放。
    /// </summary>
    /// <param name="config">数据源连接配置，内部解密密码并组装连接串。</param>
    /// <param name="ct">取消令牌，贯穿连接建立全程。</param>
    /// <returns>已打开的数据库连接。</returns>
    /// <exception cref="ArgumentNullException">config 为 null 时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">端口不在 1-65535 范围时抛出。</exception>
    Task<DbConnection> OpenConnectionAsync(DataSourceConfig config, CancellationToken ct);
}
