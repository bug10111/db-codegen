namespace DbCodeGen.Core.Model;

/// <summary>
/// 命名报告条目：dry-run 回执逐表列出真实物理表名、派生实体名/变量名、包路径、代码内表名与权限键，
/// 供调用方在写盘前核对命名契约（尤其是表前缀剥离、单复数与包段去重结果）。
/// </summary>
public sealed class NamingReportItem
{
    /// <summary>
    /// 使用完整字段构造命名报告条目。
    /// </summary>
    /// <param name="rawName">数据库真实物理表名，永不变形。</param>
    /// <param name="entityName">派生实体名（PascalCase，可含 entitySuffix）。</param>
    /// <param name="variableName">派生变量名（camelCase）。</param>
    /// <param name="packagePath">该表业务代码的点号形态包路径（基础包 + layerPrefix + variableName，包段已小写归一与连续去重）。</param>
    /// <param name="tableNameInCode">代码内物理表名（恒等于 rawName，供核对 @TableName / DDL / @Schema）。</param>
    /// <param name="permissionKey">权限键模块段（{permissionKey}:{op} 的 permissionKey，即 permissionModule）。</param>
    public NamingReportItem(
        string rawName,
        string entityName,
        string variableName,
        string packagePath,
        string tableNameInCode,
        string permissionKey)
    {
        RawName = rawName;
        EntityName = entityName;
        VariableName = variableName;
        PackagePath = packagePath;
        TableNameInCode = tableNameInCode;
        PermissionKey = permissionKey;
    }

    /// <summary>
    /// 数据库真实物理表名，永不变形。
    /// </summary>
    public string RawName { get; }

    /// <summary>
    /// 派生实体名（PascalCase，可含 entitySuffix）。
    /// </summary>
    public string EntityName { get; }

    /// <summary>
    /// 派生变量名（camelCase）。
    /// </summary>
    public string VariableName { get; }

    /// <summary>
    /// 该表业务代码的点号形态包路径。
    /// </summary>
    public string PackagePath { get; }

    /// <summary>
    /// 代码内物理表名（恒等于 rawName）。
    /// </summary>
    public string TableNameInCode { get; }

    /// <summary>
    /// 权限键模块段（{permissionKey}:{op} 的 permissionKey）。
    /// </summary>
    public string PermissionKey { get; }
}

/// <summary>
/// 生成覆盖告警：按所选模板的 role 集合判定本次生成面是否缺后端视图层、缺前端页面或仅生成了后端。
/// 非阻断，供调用方在写盘前确认生成面覆盖是否满足设计。
/// </summary>
public sealed class CoverageWarningInfo
{
    /// <summary>
    /// 告警种类：所选模板含后端产出但缺后端 View 层（生成覆盖未达到 View 层下限）。
    /// </summary>
    public const string NoBackendView = "NoBackendView";

    /// <summary>
    /// 告警种类：模板包具备前端页面能力（hasWebPage=true）但本次所选模板缺前端页面条目。
    /// </summary>
    public const string NoWebPage = "NoWebPage";

    /// <summary>
    /// 告警种类：本次所选模板只产出后端，未选中任何前端 API/DTO/页面/路由/菜单条目。
    /// </summary>
    public const string OnlyBackendGenerated = "OnlyBackendGenerated";

    /// <summary>
    /// 使用告警种类与说明构造覆盖告警。
    /// </summary>
    /// <param name="kind">告警种类常量。</param>
    /// <param name="message">人类可读告警说明（含处置指引）。</param>
    public CoverageWarningInfo(string kind, string message)
    {
        Kind = kind;
        Message = message;
    }

    /// <summary>
    /// 告警种类（NoBackendView / NoWebPage / OnlyBackendGenerated）。
    /// </summary>
    public string Kind { get; }

    /// <summary>
    /// 人类可读告警说明（中文，含处置指引）。
    /// </summary>
    public string Message { get; }
}

/// <summary>
/// 平台表/排除表命中记录：dry-run 回执列出被模板包平台表规则或排除表规则命中的表，
/// 以及是否因 includePlatformTables 显式覆盖而实际参与生成。
/// </summary>
public sealed class PlatformTableExclusion
{
    /// <summary>
    /// 使用完整字段构造命中记录。
    /// </summary>
    /// <param name="tableName">数据库真实物理表名。</param>
    /// <param name="rule">命中规则描述（platform:prefix:sys_ / exclude:exact:xxx）。</param>
    /// <param name="excluded">是否被排除（false 表示平台表经 includePlatformTables 显式覆盖后仍参与生成）。</param>
    public PlatformTableExclusion(string tableName, string rule, bool excluded)
    {
        TableName = tableName;
        Rule = rule;
        Excluded = excluded;
    }

    /// <summary>
    /// 数据库真实物理表名。
    /// </summary>
    public string TableName { get; }

    /// <summary>
    /// 命中规则描述。
    /// </summary>
    public string Rule { get; }

    /// <summary>
    /// 是否被排除；includePlatformTables=true 时平台表命中记录为 false（已显式允许生成）。
    /// </summary>
    public bool Excluded { get; }
}
