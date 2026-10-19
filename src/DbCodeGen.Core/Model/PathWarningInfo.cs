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
    /// 告警种类：输出路径的 Java 包段不是合法 Java 标识符（连字符 / 非法字符 / 数字开头 / Java 关键字）。
    /// 非阻断告警；Java 包段/模块目录/artifactId 应经 tool.javaIdent 归一化 kebab 模块名，
    /// 数字开头或关键字需改模块名。
    /// </summary>
    public const string InvalidJavaPackageSegment = "InvalidJavaPackageSegment";

    /// <summary>
    /// 告警种类：src/main/java 之后出现连续重复包段（如 com.simple.xiaoguan.common/common），
    /// 通常由模板无条件下硬拼层段（common）与基础包结尾重复导致。
    /// </summary>
    public const string DuplicatePackageSegment = "DuplicatePackageSegment";

    /// <summary>
    /// 告警种类：src/main/java 之后的包段含大写字母（如 commonFile），Java 包名约定全小写；
    /// 应改用 packageSegmentCase=lower 或 tool.packagePath 组装。
    /// </summary>
    public const string NonLowercasePackageSegment = "NonLowercasePackageSegment";

    /// <summary>
    /// 告警种类：模板正文静态使用 hump2Underline/hump3Underline(table.className) 反推物理表名；
    /// 物理表名必须使用 table.rawName（表前缀剥离与单复数归一后反推会失真）。
    /// </summary>
    public const string TableNameRoundTripMismatch = "TableNameRoundTripMismatch";

    /// <summary>
    /// 告警种类：显式权限模块（请求 vars.permissionModule / manifest.permissionModule）与 Hump3Underline(modules[0].name) 不一致；
    /// 由 modules 自动派生的情形与权限点清单同源，不告警。带告警禁止交付（运行期会 403）。
    /// </summary>
    public const string PermissionKeyMismatch = "PermissionKeyMismatch";

    /// <summary>
    /// 告警种类：workspaceRoot 自身含带 &lt;modules&gt; 的 pom.xml（疑似 Maven 聚合根），且 codeDirectory 未落入其下模块目录；
    /// 应传工程父目录而非聚合根本身作为 workspaceRoot。
    /// </summary>
    public const string WorkspaceRootMayBeAggregate = "WorkspaceRootMayBeAggregate";

    /// <summary>
    /// 告警种类标识：EmptyPackageDir（包段为空）/ ConsecutiveSeparators（连续分隔符）/
    /// EmptyIteration（模板空迭代）/ InvalidJavaPackageSegment（非法 Java 包段）/
    /// DuplicatePackageSegment（连续重复包段）/ NonLowercasePackageSegment（非小写包段）/
    /// TableNameRoundTripMismatch（物理表名反推）/ PermissionKeyMismatch（权限键派生不一致）/
    /// WorkspaceRootMayBeAggregate（工作区根疑似聚合根）。
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
