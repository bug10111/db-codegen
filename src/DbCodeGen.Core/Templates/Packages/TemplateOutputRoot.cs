namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板条目的输出根语义：声明渲染后相对路径以哪个基准目录解析。
/// 缺省 Output 保持既有行为（工作区根 + 相对输出根），Workspace 以工作区根为基准，
/// Web 以工作区根下的 webDirectory（缺省 web）为基准。
/// </summary>
public enum TemplateOutputRoot
{
    /// <summary>
    /// 输出根：相对输出根解析，允许 .. 段在工作区根内越出相对输出根（既有行为）。
    /// 未声明 root 时，table/module 作用域沿用本语义；project 作用域沿用工作区根语义（legacy）。
    /// </summary>
    Output,

    /// <summary>
    /// 工作区根：以 workspaceRoot 为基准解析，禁止 .. 段，最终路径必须落在 workspaceRoot 内。
    /// </summary>
    Workspace,

    /// <summary>
    /// web 根：以 workspaceRoot/webDirectory 为基准解析，禁止 .. 段，最终路径必须落在 workspaceRoot 内。
    /// </summary>
    Web
}

/// <summary>
/// 输出根声明解析器：manifest files[].root 文本与枚举的双向转换，空白缺省 output，非法取值结构化拒绝。
/// </summary>
public static class TemplateOutputRootParser
{
    /// <summary>
    /// 合法输出根文本（小写），与枚举成员一一对应。
    /// </summary>
    private static readonly Dictionary<string, TemplateOutputRoot> KnownRoots = new(StringComparer.Ordinal)
    {
        ["output"] = TemplateOutputRoot.Output,
        ["workspace"] = TemplateOutputRoot.Workspace,
        ["web"] = TemplateOutputRoot.Web
    };

    /// <summary>
    /// 解析 manifest files[].root 文本：空白与 null 返回 null（未声明，按作用域沿用既有缺省语义：
    /// table/module = 相对输出根，project = 工作区根）。
    /// </summary>
    /// <param name="rootText">清单声明的 root 原始文本，可空。</param>
    /// <param name="templateRelativePath">模板相对路径，供错误消息定位。</param>
    /// <returns>解析后的输出根枚举；未声明时为 null。</returns>
    /// <exception cref="TemplatePackageException">root 文本非空白且不属于合法取值时抛出。</exception>
    public static TemplateOutputRoot? Parse(string? rootText, string templateRelativePath)
    {
        if (string.IsNullOrWhiteSpace(rootText))
        {
            return null;
        }

        string normalized = rootText.Trim().ToLowerInvariant();
        if (KnownRoots.TryGetValue(normalized, out TemplateOutputRoot root))
        {
            return root;
        }

        throw new TemplatePackageException(
            $"模板文件 {templateRelativePath} 的 root 非法：{rootText}；合法取值为 output / workspace / web（缺省按作用域沿用既有语义）。");
    }

    /// <summary>
    /// 将输出根枚举转为清单契约文本（小写：output / workspace / web），供工具面与诊断回执透出。
    /// </summary>
    /// <param name="root">输出根枚举。</param>
    /// <returns>契约文本。</returns>
    public static string ToContractText(TemplateOutputRoot root)
    {
        return root switch
        {
            TemplateOutputRoot.Workspace => "workspace",
            TemplateOutputRoot.Web => "web",
            _ => "output"
        };
    }
}
