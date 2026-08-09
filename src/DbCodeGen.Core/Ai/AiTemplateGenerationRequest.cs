using DbCodeGen.Core.Model;

namespace DbCodeGen.Core.Ai;

/// <summary>
/// AI 模板生成目标模式：追加到现有用户包（默认）或新建用户包。
/// 追加模式下 AI 的包级元数据（packageName/basePackage/typeMap）被丢弃，仅取 files[] 写入目标包；
/// 新建模式下走原临时包导入流程，包名可显式指定或留空由 AI 自定。
/// </summary>
public enum AiGenerationTargetMode
{
    /// <summary>
    /// 追加到现有用户包（可指定分组目录前缀），默认模式。
    /// </summary>
    AppendToPackage,

    /// <summary>
    /// 新建用户包，包名由 RequestedPackageName 指定或留空由 AI 自定。
    /// </summary>
    NewPackage
}

/// <summary>
/// AI 模板生成请求，承载生成说明（原技术栈描述，升级为自由指令）、样例表真实元数据、参考文件内容快照清单，
/// 以及生成目标定位（追加到现有包 / 新建包）所需的目标信息。
/// </summary>
public sealed class AiTemplateGenerationRequest
{
    /// <summary>
    /// 生成说明，必填，自由文本指令，如"Java + MyBatis-Plus，三层分层，生成实体与 Mapper"。
    /// 由模型按说明决定生成 1 个模板还是整套模板包，是生成范围的最高优先级依据。
    /// </summary>
    public string TechStackDescription { get; set; } = string.Empty;

    /// <summary>
    /// 样例表真实元数据，来自表浏览与选择功能，含列集合与主键等信息，注入提示词前序列化为 JSON。
    /// </summary>
    public TableInfo SampleTable { get; set; } = new();

    /// <summary>
    /// 参考文件内容快照清单，由窗口级共享参考文件上下文在发送时快照传入；默认空集合。
    /// 内容仅注入本次对话提示词，不写盘不进日志；参考文件作为约定蓝本要求逐文件镜像并翻译为 Scriban。
    /// </summary>
    public IReadOnlyList<AiReferenceFileItem> ReferenceFiles { get; set; } = Array.Empty<AiReferenceFileItem>();

    /// <summary>
    /// 生成目标模式，默认追加到现有用户包。
    /// </summary>
    public AiGenerationTargetMode TargetMode { get; set; } = AiGenerationTargetMode.AppendToPackage;

    /// <summary>
    /// 追加模式下目标现有用户包名，必填；须为模板库中已存在的用户包（内置包只读拒绝）。
    /// </summary>
    public string? TargetPackageName { get; set; }

    /// <summary>
    /// 可选分组目录前缀，追加到模板相对路径，仅影响模板文件组织，不影响生成代码落盘路径。
    /// </summary>
    public string? TargetGroup { get; set; }

    /// <summary>
    /// 新建模式下用户显式指定的包名，为空时由 AI 自定；指定时仍须通过包名合法性校验。
    /// </summary>
    public string? RequestedPackageName { get; set; }
}
