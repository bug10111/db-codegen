namespace DbCodeGen.Core.BackupRestore;

/// <summary>
/// 备份/恢复领域异常，用于表达备份文件校验失败（格式、版本、目录穿越、解压超限等）
/// 或恢复过程中的 IO 错误，是备份/恢复服务对外抛出的统一结构化错误。
/// </summary>
public sealed class BackupValidationException : Exception
{
    /// <summary>
    /// 使用错误描述创建备份/恢复异常。
    /// </summary>
    /// <param name="message">面向用户的错误描述，不含任何敏感字段。</param>
    public BackupValidationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// 使用错误描述与内部异常创建备份/恢复异常。
    /// </summary>
    /// <param name="message">面向用户的错误描述，不含任何敏感字段。</param>
    /// <param name="innerException">导致本次异常的底层异常。</param>
    public BackupValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
