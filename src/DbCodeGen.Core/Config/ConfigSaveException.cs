namespace DbCodeGen.Core.Config;

/// <summary>
/// 配置保存失败时抛出的结构化异常，表示写盘异常且内存中的原值已保留，调用方可提示用户重试。
/// </summary>
public sealed class ConfigSaveException : Exception
{
    /// <summary>
    /// 使用指定消息构造异常。
    /// </summary>
    /// <param name="message">异常的可读描述，不含任何明文密钥。</param>
    public ConfigSaveException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// 使用指定消息与内部异常构造异常。
    /// </summary>
    /// <param name="message">异常的可读描述，不含任何明文密钥。</param>
    /// <param name="innerException">触发保存失败的底层异常。</param>
    public ConfigSaveException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
