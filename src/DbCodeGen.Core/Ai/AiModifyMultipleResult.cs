namespace DbCodeGen.Core.Ai;

/// <summary>
/// AI 改模板批量修改结果，承载逐文件结果清单与整体是否全部成功判定。
/// 批量级校验失败（文件清单为空、指令为空、LLM 未配置）时清单为空且携带错误清单；
/// 逐文件失败已隔离在各自结果中，不中断其它文件。
/// </summary>
public sealed class AiModifyMultipleResult
{
    /// <summary>
    /// 逐文件结果清单，按请求文件顺序排列，与 AiModifyFileItem 一一对应。
    /// </summary>
    public IReadOnlyList<AiModifyFileResult> FileResults { get; set; } = Array.Empty<AiModifyFileResult>();

    /// <summary>
    /// 是否全部文件成功：清单非空且每项均 IsSuccess 时为 true。
    /// </summary>
    public bool IsAllSucceeded { get; set; }

    /// <summary>
    /// 批量级错误清单（文件清单为空、修改指令为空、LLM 未配置等），逐文件错误在各结果中承载。
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// 按逐文件结果构造批量结果并推导整体是否全部成功。
    /// </summary>
    /// <param name="fileResults">逐文件结果清单。</param>
    /// <returns>批量结果实例。</returns>
    public static AiModifyMultipleResult Create(IReadOnlyList<AiModifyFileResult> fileResults)
    {
        bool isAllSucceeded = fileResults.Count > 0 && fileResults.All(result => result.IsSuccess);
        return new AiModifyMultipleResult
        {
            FileResults = fileResults,
            IsAllSucceeded = isAllSucceeded
        };
    }

    /// <summary>
    /// 构造批量级失败结果，携带错误清单且无逐文件结果。
    /// </summary>
    /// <param name="errors">批量级错误清单。</param>
    /// <returns>批量失败结果实例。</returns>
    public static AiModifyMultipleResult Failed(List<string> errors)
    {
        return new AiModifyMultipleResult
        {
            FileResults = Array.Empty<AiModifyFileResult>(),
            IsAllSucceeded = false,
            Errors = errors
        };
    }
}
