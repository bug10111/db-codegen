using DbCodeGen.Core.Model;

namespace DbCodeGen.Core.Ai;

/// <summary>
/// AI 模板生成请求，承载技术栈描述、样例表真实元数据与参考文件内容快照清单。
/// </summary>
public sealed class AiTemplateGenerationRequest
{
    /// <summary>
    /// 技术栈描述，必填，如"Java + MyBatis-Plus，三层分层"。
    /// </summary>
    public string TechStackDescription { get; set; } = string.Empty;

    /// <summary>
    /// 样例表真实元数据，来自表浏览与选择功能，含列集合与主键等信息，注入提示词前序列化为 JSON。
    /// </summary>
    public TableInfo SampleTable { get; set; } = new();

    /// <summary>
    /// 参考文件内容快照清单，由窗口级共享参考文件上下文在发送时快照传入；默认空集合。
    /// 内容仅注入本次对话提示词，不写盘不进日志。
    /// </summary>
    public IReadOnlyList<AiReferenceFileItem> ReferenceFiles { get; set; } = Array.Empty<AiReferenceFileItem>();
}
