namespace DbCodeGen.Core.Config;

/// <summary>
/// AI 参考文件限制配置子模型，持久化为 config.json 的 aiReferenceFileLimits 对象，
/// 写模板与改模板上传参考文件时按数量上限/单文件上限/总大小上限共用校验。
/// 默认常量以 public const 声明于本类，作为 BuildDefaultConfig 与 Normalize 的单一默认值来源。
/// </summary>
public class AiReferenceFileLimits
{
    /// <summary>
    /// 默认参考文件数量上限，20 个。
    /// </summary>
    public const int DefaultMaxFileCount = 20;

    /// <summary>
    /// 默认单文件大小上限（字节），1MB。
    /// </summary>
    public const long DefaultMaxSingleFileBytes = 1 * 1024 * 1024;

    /// <summary>
    /// 默认参考文件总大小上限（字节），10MB。
    /// </summary>
    public const long DefaultMaxTotalBytes = 10 * 1024 * 1024;

    /// <summary>
    /// 参考文件数量上限，默认 20 个。
    /// </summary>
    public int MaxFileCount { get; set; } = DefaultMaxFileCount;

    /// <summary>
    /// 单文件大小上限（字节），默认 1MB；上传参考文件时逐文件与此值比对。
    /// </summary>
    public long MaxSingleFileBytes { get; set; } = DefaultMaxSingleFileBytes;

    /// <summary>
    /// 参考文件总大小上限（字节），默认 10MB；上传参考文件时所有文件大小合计与此值比对。
    /// </summary>
    public long MaxTotalBytes { get; set; } = DefaultMaxTotalBytes;
}
