namespace DbCodeGen.Core.Ai;

/// <summary>
/// AI 改模板批量结果中单个文件的结果，成功携带剥离代码围栏且内容非空校验通过的完整新文件，
/// 失败携带该文件的错误原因。文件间互不影响，单文件失败不中断其它文件。
/// </summary>
public sealed class AiModifyFileResult
{
    /// <summary>
    /// 对应文件的相对包根路径（正斜杠规范化）。
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// 该文件是否成功返回完整新文件。
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// 成功时剥离代码围栏并做内容非空校验后的完整新文件。
    /// </summary>
    public string? NewContent { get; set; }

    /// <summary>
    /// 失败原因（请求校验、LLM 调用错误或返回内容为空），成功时为空。
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// 构造单文件成功结果，携带完整新文件。
    /// </summary>
    /// <param name="relativePath">文件相对包根路径。</param>
    /// <param name="newContent">完整新文件内容。</param>
    /// <returns>成功结果实例。</returns>
    public static AiModifyFileResult ForSuccess(string relativePath, string newContent)
    {
        return new AiModifyFileResult
        {
            RelativePath = relativePath,
            IsSuccess = true,
            NewContent = newContent
        };
    }

    /// <summary>
    /// 构造单文件失败结果，携带该文件的错误原因。
    /// </summary>
    /// <param name="relativePath">文件相对包根路径。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>失败结果实例。</returns>
    public static AiModifyFileResult ForFailure(string relativePath, string error)
    {
        return new AiModifyFileResult
        {
            RelativePath = relativePath,
            IsSuccess = false,
            Error = error
        };
    }
}
