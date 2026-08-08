namespace DbCodeGen.Core.Model;

/// <summary>
/// 生成日志级别，用于底栏日志分级展示。设计文档中 GenerationLogEntry.Level 的 LogLevel 枚举，
/// 为避免与 Microsoft.Extensions.Logging.LogLevel 命名冲突，本工程命名为 GenerationLogLevel。
/// </summary>
public enum GenerationLogLevel
{
    /// <summary>
    /// 信息级别，如文件已生成、已覆盖、已跳过。
    /// </summary>
    Info,

    /// <summary>
    /// 警告级别，如配置回写失败但不阻断主流程。
    /// </summary>
    Warning,

    /// <summary>
    /// 错误级别，如单文件写盘失败。
    /// </summary>
    Error
}

/// <summary>
/// 生成日志条目，承载日志时间、级别与消息。
/// 日志消息不得包含密码、密钥、连接串或模板正文等敏感信息，只记录目标文件相对路径与异常描述。
/// </summary>
public sealed class GenerationLogEntry
{
    /// <summary>
    /// 使用时间、级别与消息构造日志条目。
    /// </summary>
    /// <param name="timestamp">日志时间。</param>
    /// <param name="level">日志级别。</param>
    /// <param name="message">日志消息，不含敏感信息。</param>
    /// <exception cref="ArgumentNullException">message 为 null 时抛出。</exception>
    public GenerationLogEntry(DateTime timestamp, GenerationLogLevel level, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Timestamp = timestamp;
        Level = level;
        Message = message;
    }

    /// <summary>
    /// 日志时间。
    /// </summary>
    public DateTime Timestamp { get; }

    /// <summary>
    /// 日志级别：信息 / 警告 / 错误。
    /// </summary>
    public GenerationLogLevel Level { get; }

    /// <summary>
    /// 日志消息，含目标文件相对路径与异常描述，不含敏感信息。
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// 创建以当前时间为时间戳的信息级别日志条目。
    /// </summary>
    /// <param name="message">日志消息。</param>
    /// <returns>信息级别日志条目。</returns>
    public static GenerationLogEntry Info(string message)
    {
        return new GenerationLogEntry(DateTime.Now, GenerationLogLevel.Info, message);
    }

    /// <summary>
    /// 创建以当前时间为时间戳的警告级别日志条目。
    /// </summary>
    /// <param name="message">日志消息。</param>
    /// <returns>警告级别日志条目。</returns>
    public static GenerationLogEntry Warning(string message)
    {
        return new GenerationLogEntry(DateTime.Now, GenerationLogLevel.Warning, message);
    }

    /// <summary>
    /// 创建以当前时间为时间戳的错误级别日志条目。
    /// </summary>
    /// <param name="message">日志消息。</param>
    /// <returns>错误级别日志条目。</returns>
    public static GenerationLogEntry Error(string message)
    {
        return new GenerationLogEntry(DateTime.Now, GenerationLogLevel.Error, message);
    }
}
