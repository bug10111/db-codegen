namespace DbCodeGen.Core.Model;

/// <summary>
/// 生成进度阶段，批量生成全流程的阶段取值。
/// BuildPreviewAsync 报告渲染与 dry-run 分类，GenerateAsync 在二者基础上追加写盘阶段，三取值全部可达。
/// </summary>
public enum GenerationStage
{
    /// <summary>
    /// 渲染阶段：直读模板内容、内容渲染与输出路径占位渲染。
    /// </summary>
    Rendering,

    /// <summary>
    /// dry-run 分类阶段：绝对路径解析、防目录穿越校验与新增/覆盖/跳过分类。
    /// </summary>
    Previewing,

    /// <summary>
    /// 写盘阶段：自动建目录与 UTF-8 无 BOM 异步写盘。
    /// </summary>
    Writing
}

/// <summary>
/// 生成进度推送，承载阶段、已完成条目数、总条目数与当前处理的相对路径，供界面更新进度条。
/// </summary>
public sealed class GenerationProgress
{
    /// <summary>
    /// 使用完整字段构造进度推送。
    /// </summary>
    /// <param name="stage">当前所处阶段。</param>
    /// <param name="completed">已完成条目数。</param>
    /// <param name="total">总条目数。</param>
    /// <param name="currentFile">当前处理的相对路径。</param>
    /// <exception cref="ArgumentNullException">currentFile 为 null 时抛出。</exception>
    public GenerationProgress(GenerationStage stage, int completed, int total, string currentFile)
    {
        ArgumentNullException.ThrowIfNull(currentFile);
        Stage = stage;
        Completed = completed;
        Total = total;
        CurrentFile = currentFile;
    }

    /// <summary>
    /// 当前所处阶段：渲染 / dry-run 分类 / 写盘。
    /// </summary>
    public GenerationStage Stage { get; }

    /// <summary>
    /// 已完成条目数。
    /// </summary>
    public int Completed { get; }

    /// <summary>
    /// 总条目数，等于勾选表数乘以勾选模板文件数。
    /// </summary>
    public int Total { get; }

    /// <summary>
    /// 当前处理的相对路径。
    /// </summary>
    public string CurrentFile { get; }
}
