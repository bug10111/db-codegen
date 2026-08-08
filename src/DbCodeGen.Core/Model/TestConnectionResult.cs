namespace DbCodeGen.Core.Model;

/// <summary>
/// 测试连接结果，向调用方返回成功与否、可读信息与服务端信息。
/// </summary>
public class TestConnectionResult
{
    /// <summary>
    /// 连接是否成功。
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// 成功或失败的可读信息，失败含异常原因但不含密码。
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// 服务端版本，成功时可选。
    /// </summary>
    public string? ServerVersion { get; set; }

    /// <summary>
    /// 连接耗时。
    /// </summary>
    public TimeSpan Elapsed { get; set; }
}
