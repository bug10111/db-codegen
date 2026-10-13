namespace DbCodeGen.Core.Model;

/// <summary>
/// 生成预检发现的输出路径告警，供 dry-run 回执提示坏路径风险（不阻断生成）。
/// 同一模板同一告警种类跨表跨模块多次出现时归并为一条，记录首次出现位置与总出现次数，
/// 归并口径与未映射类型清单一致。
/// </summary>
public sealed class PathWarningInfo
{
    /// <summary>
    /// 告警种类：输出路径模板引用包段占位（package.dir）但本次包段解析为空。
    /// </summary>
    public const string EmptyPackageDir = "EmptyPackageDir";

    /// <summary>
    /// 告警种类：渲染后输出路径含连续路径分隔符（如 src/main/java//X.cs）。
    /// </summary>
    public const string ConsecutiveSeparators = "ConsecutiveSeparators";

    /// <summary>
    /// 告警种类：模板内 for 循环迭代源为空集合 / null，循环体零迭代（渲染产物中该段为空）。
    /// 非阻断告警；守卫文案设计（空集合时产物含说明文字属预期）场景按告警文案指引忽略。
    /// </summary>
    public const string EmptyIteration = "EmptyIteration";

    /// <summary>
    /// 告警种类标识：EmptyPackageDir（包段为空）/ ConsecutiveSeparators（连续分隔符）/ EmptyIteration（模板空迭代）。
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// 首次出现该告警的模板文件相对路径，供定位产出坏路径的模板。
    /// </summary>
    public string TemplateName { get; set; } = string.Empty;

    /// <summary>
    /// 首次出现该告警的渲染后相对输出路径样例，供直观核对坏路径形态。
    /// </summary>
    public string SamplePath { get; set; } = string.Empty;

    /// <summary>
    /// 该告警在本次生成范围内跨表跨模块的总出现次数。
    /// </summary>
    public int Occurrences { get; set; }

    /// <summary>
    /// 人类可读告警说明（中文），供回执直接展示与修复指引。
    /// </summary>
    public string Message { get; set; } = string.Empty;
}
