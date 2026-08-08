using DbCodeGen.Core.Model;

namespace DbCodeGen.Core.Ai;

/// <summary>
/// AI 模板生成请求，承载技术栈描述、样例表真实元数据与参考素材开关。
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
    /// 是否注入 easycode 参考素材作为转写参照。
    /// </summary>
    public bool IncludeEasyCodeReference { get; set; }
}
