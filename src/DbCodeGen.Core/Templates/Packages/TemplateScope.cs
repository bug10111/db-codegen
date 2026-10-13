namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板条目作用域：声明单个模板文件按何种展开方式参与生成。
/// table 为缺省作用域（按表集笛卡尔积，现状行为）；module 按模块清单逐个渲染；project 整次生成渲染一次。
/// </summary>
public enum TemplateScope
{
    /// <summary>
    /// 表作用域（缺省）：按勾选表集逐表渲染，上下文注入 table / column? / package / tool。
    /// </summary>
    Table,

    /// <summary>
    /// 模块作用域：按本次调用传入的模块清单逐模块渲染，上下文注入 module / vars / tables / package / tool。
    /// </summary>
    Module,

    /// <summary>
    /// 工程作用域：整次生成仅渲染一次，上下文注入 vars / tables / package / tool。
    /// </summary>
    Project
}

/// <summary>
/// 模板作用域解析与校验器：manifest files[].scope 文本与枚举的双向转换，非法取值结构化拒绝。
/// </summary>
public static class TemplateScopeParser
{
    /// <summary>
    /// 合法作用域文本（小写），与枚举成员一一对应。
    /// </summary>
    private static readonly Dictionary<string, TemplateScope> KnownScopes = new(StringComparer.Ordinal)
    {
        ["table"] = TemplateScope.Table,
        ["module"] = TemplateScope.Module,
        ["project"] = TemplateScope.Project
    };

    /// <summary>
    /// 解析 manifest files[].scope 文本为作用域枚举：空白与 null 视为缺省 table（向后兼容：无 scope 字段的既有包行为不变）。
    /// </summary>
    /// <param name="scopeText">清单声明的 scope 原始文本，可空。</param>
    /// <param name="templateRelativePath">模板相对路径，供错误消息定位。</param>
    /// <returns>解析后的作用域枚举。</returns>
    /// <exception cref="TemplatePackageException">scope 文本非空白且不属于合法取值时抛出。</exception>
    public static TemplateScope Parse(string? scopeText, string templateRelativePath)
    {
        if (string.IsNullOrWhiteSpace(scopeText))
        {
            return TemplateScope.Table;
        }

        string normalized = scopeText.Trim().ToLowerInvariant();
        if (KnownScopes.TryGetValue(normalized, out TemplateScope scope))
        {
            return scope;
        }

        throw new TemplatePackageException(
            $"模板文件 {templateRelativePath} 的 scope 非法：{scopeText}；合法取值为 table / module / project（缺省 table）。");
    }

    /// <summary>
    /// 将作用域枚举转为清单契约文本（小写：table / module / project），供工具面透出条目作用域。
    /// </summary>
    /// <param name="scope">作用域枚举。</param>
    /// <returns>契约文本。</returns>
    public static string ToContractText(TemplateScope scope)
    {
        return scope switch
        {
            TemplateScope.Module => "module",
            TemplateScope.Project => "project",
            _ => "table"
        };
    }
}
