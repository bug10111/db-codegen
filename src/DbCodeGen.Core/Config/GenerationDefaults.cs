namespace DbCodeGen.Core.Config;

/// <summary>
/// 批量代码生成读取入口的返回值 DTO，承载工作区根与最近相对输出根的快照。
/// </summary>
public sealed class GenerationDefaults
{
    /// <summary>
    /// 使用指定工作区根与最近相对输出根构造默认值快照。
    /// </summary>
    /// <param name="workspaceRoot">工作区根，绝对输出路径的根前缀。</param>
    /// <param name="lastRelativeOutputRoot">最近相对输出根，相对输出路径的默认值。</param>
    public GenerationDefaults(string workspaceRoot, string lastRelativeOutputRoot)
    {
        WorkspaceRoot = workspaceRoot;
        LastRelativeOutputRoot = lastRelativeOutputRoot;
    }

    /// <summary>
    /// 工作区根，绝对输出路径的根前缀。
    /// </summary>
    public string WorkspaceRoot { get; }

    /// <summary>
    /// 最近相对输出根，相对输出路径的默认值。
    /// </summary>
    public string LastRelativeOutputRoot { get; }
}
