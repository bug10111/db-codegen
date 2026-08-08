namespace DbCodeGen.Core.Model;

/// <summary>
/// dry-run 单个待写条目的目标文件动作分类：新增 / 覆盖 / 跳过。
/// 目标不存在为新增，存在且内容相同为跳过，存在且内容不同为覆盖。
/// </summary>
public enum GenerationAction
{
    /// <summary>
    /// 目标文件不存在，写盘时新建。
    /// </summary>
    New,

    /// <summary>
    /// 目标文件存在且内容与渲染结果不同，写盘时覆盖。
    /// </summary>
    Overwrite,

    /// <summary>
    /// 目标文件存在且内容与渲染结果相同，写盘时跳过。
    /// </summary>
    Skip
}

/// <summary>
/// dry-run 单个待写条目，承载渲染后内容、目标相对路径与绝对路径、动作分类与写盘失败追溯信息。
/// 渲染失败即整单失败不产出条目；条目 Error 仅在写盘异常时填充，供界面逐条展示失败原因。
/// </summary>
public sealed class GenerationFileEntry
{
    /// <summary>
    /// 使用完整字段构造单个待写条目。
    /// </summary>
    /// <param name="tableName">来源表名，渲染上下文归属。</param>
    /// <param name="relativePath">渲染后相对输出根路径，已解析 {{变量}} 占位。</param>
    /// <param name="absolutePath">绝对路径，等于工作区根/相对输出根/RelativePath 拼接且已校验防目录穿越。</param>
    /// <param name="action">dry-run 动作分类。</param>
    /// <param name="content">渲染后的文件内容。</param>
    /// <param name="error">写盘失败的条目级异常信息，无异常为 null。</param>
    /// <exception cref="ArgumentNullException">tableName、relativePath、absolutePath 或 content 为 null 时抛出。</exception>
    public GenerationFileEntry(
        string tableName,
        string relativePath,
        string absolutePath,
        GenerationAction action,
        string content,
        string? error = null)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(absolutePath);
        ArgumentNullException.ThrowIfNull(content);
        TableName = tableName;
        RelativePath = relativePath;
        AbsolutePath = absolutePath;
        Action = action;
        Content = content;
        Error = error;
    }

    /// <summary>
    /// 来源表名，渲染上下文归属。
    /// </summary>
    public string TableName { get; }

    /// <summary>
    /// 渲染后相对输出根路径，已解析 {{变量}} 占位。
    /// </summary>
    public string RelativePath { get; }

    /// <summary>
    /// 绝对路径，等于工作区根/相对输出根/RelativePath 拼接，已校验防目录穿越。
    /// </summary>
    public string AbsolutePath { get; }

    /// <summary>
    /// dry-run 动作分类：新增 / 覆盖 / 跳过。
    /// </summary>
    public GenerationAction Action { get; }

    /// <summary>
    /// 渲染后的文件内容。
    /// </summary>
    public string Content { get; }

    /// <summary>
    /// 写盘失败的条目级异常信息，供界面逐条展示失败原因；无异常为 null。
    /// </summary>
    public string? Error { get; set; }
}
