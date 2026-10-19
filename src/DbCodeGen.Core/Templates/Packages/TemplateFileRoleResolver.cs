namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板条目产出角色解析器：把 manifest 声明的 role 与按输出路径/模板文件名推断的产出角色统一为一套枚举文本，
/// 供能力元数据（hasBackendView/hasWebPage）、coverageWarnings 判定与 Wave 1 模板契约消费。
/// 声明 role 优先；未声明时按输出路径与模板文件名推断。
/// </summary>
public static class TemplateFileRoleResolver
{
    /// <summary>
    /// 后端实体角色。
    /// </summary>
    public const string BackendEntity = "backendEntity";

    /// <summary>
    /// 后端视图层角色（View 层，只做数据处理）。
    /// </summary>
    public const string BackendView = "backendView";

    /// <summary>
    /// 后端服务层角色。
    /// </summary>
    public const string BackendService = "backendService";

    /// <summary>
    /// 后端控制层角色。
    /// </summary>
    public const string BackendController = "backendController";

    /// <summary>
    /// 后端数据访问角色（Repository / Mapper / CopyMapper）。
    /// </summary>
    public const string BackendData = "backendData";

    /// <summary>
    /// 后端其它 Java 产出角色。
    /// </summary>
    public const string BackendOther = "backendOther";

    /// <summary>
    /// DDL / SQL 脚本角色。
    /// </summary>
    public const string Ddl = "ddl";

    /// <summary>
    /// 前端 API 封装角色。
    /// </summary>
    public const string WebApi = "webApi";

    /// <summary>
    /// 前端 DTO 角色。
    /// </summary>
    public const string WebDto = "webDto";

    /// <summary>
    /// 前端业务页面角色（列表页/表单弹窗）。
    /// </summary>
    public const string WebPage = "webPage";

    /// <summary>
    /// 前端路由角色。
    /// </summary>
    public const string WebRoute = "webRoute";

    /// <summary>
    /// 前端菜单角色。
    /// </summary>
    public const string WebMenu = "webMenu";

    /// <summary>
    /// 前端其它产出角色。
    /// </summary>
    public const string WebOther = "webOther";

    /// <summary>
    /// 脚手架 / 工程骨架角色。
    /// </summary>
    public const string Scaffold = "scaffold";

    /// <summary>
    /// 配置类产出角色。
    /// </summary>
    public const string Config = "config";

    /// <summary>
    /// 契约文档角色。
    /// </summary>
    public const string Contract = "contract";

    /// <summary>
    /// 其它产出角色。
    /// </summary>
    public const string Other = "other";

    /// <summary>
    /// 解析条目产出角色：manifest 声明非空时取声明值（小写归一），否则按输出路径与模板文件名推断。
    /// </summary>
    /// <param name="declaredRole">manifest files[].role 声明，可空。</param>
    /// <param name="templateRelativePath">模板相对包根路径。</param>
    /// <param name="outputPathTemplate">输出路径模板，可为空。</param>
    /// <returns>角色文本（小写驼峰，如 backendView / webPage）。</returns>
    public static string Resolve(string? declaredRole, string templateRelativePath, string? outputPathTemplate)
    {
        if (!string.IsNullOrWhiteSpace(declaredRole))
        {
            return declaredRole.Trim();
        }

        return Infer(templateRelativePath, outputPathTemplate);
    }

    /// <summary>
    /// 按输出路径（优先）与模板文件名推断产出角色。
    /// </summary>
    /// <param name="templateRelativePath">模板相对包根路径。</param>
    /// <param name="outputPathTemplate">输出路径模板，可为空。</param>
    /// <returns>推断出的角色文本。</returns>
    public static string Infer(string templateRelativePath, string? outputPathTemplate)
    {
        string path = string.IsNullOrWhiteSpace(outputPathTemplate) ? templateRelativePath : outputPathTemplate;
        string normalized = (path ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
        string fileName = GetLastSegment(normalized);

        if (normalized.EndsWith(".java", StringComparison.Ordinal))
        {
            return InferJavaRole(normalized, fileName);
        }

        if (normalized.EndsWith(".sql", StringComparison.Ordinal))
        {
            return Ddl;
        }

        if (normalized.EndsWith(".ts", StringComparison.Ordinal) || normalized.EndsWith(".tsx", StringComparison.Ordinal))
        {
            return InferWebRole(normalized, fileName);
        }

        if (normalized.EndsWith(".md", StringComparison.Ordinal))
        {
            return fileName.Contains("readme", StringComparison.Ordinal) ? Contract : Other;
        }

        if (normalized.EndsWith(".xml", StringComparison.Ordinal)
            || normalized.EndsWith(".yaml", StringComparison.Ordinal)
            || normalized.EndsWith(".yml", StringComparison.Ordinal)
            || normalized.EndsWith(".json", StringComparison.Ordinal)
            || normalized.EndsWith(".toml", StringComparison.Ordinal)
            || normalized.EndsWith(".conf", StringComparison.Ordinal)
            || normalized.EndsWith(".properties", StringComparison.Ordinal)
            || normalized.EndsWith(".gitignore", StringComparison.Ordinal)
            || normalized.EndsWith(".env", StringComparison.Ordinal)
            || normalized.EndsWith(".css", StringComparison.Ordinal)
            || normalized.EndsWith(".html", StringComparison.Ordinal))
        {
            return Config;
        }

        return Other;
    }

    /// <summary>
    /// 判断角色是否属于后端角色集合（backendEntity/backendView/backendService/backendController/backendData/backendOther）。
    /// </summary>
    /// <param name="role">角色文本。</param>
    /// <returns>属于后端角色时为 true。</returns>
    public static bool IsBackendRole(string role)
    {
        return role is BackendEntity or BackendView or BackendService or BackendController or BackendData or BackendOther;
    }

    /// <summary>
    /// 判断角色是否属于前端角色集合（webApi/webDto/webPage/webRoute/webMenu/webOther）。
    /// </summary>
    /// <param name="role">角色文本。</param>
    /// <returns>属于前端角色时为 true。</returns>
    public static bool IsWebRole(string role)
    {
        return role is WebApi or WebDto or WebPage or WebRoute or WebMenu or WebOther;
    }

    /// <summary>
    /// 按路径片段推断 Java 产出角色。
    /// </summary>
    /// <param name="normalizedPath">小写归一后的输出路径。</param>
    /// <param name="fileName">文件名（小写）。</param>
    /// <returns>角色文本。</returns>
    private static string InferJavaRole(string normalizedPath, string fileName)
    {
        if (normalizedPath.Contains("/view/", StringComparison.Ordinal)
            || fileName.EndsWith("view.java", StringComparison.Ordinal))
        {
            return BackendView;
        }

        if (normalizedPath.Contains("/entity/", StringComparison.Ordinal))
        {
            return BackendEntity;
        }

        if (normalizedPath.Contains("/controller/", StringComparison.Ordinal))
        {
            return BackendController;
        }

        if (normalizedPath.Contains("/service/", StringComparison.Ordinal))
        {
            return BackendService;
        }

        if (normalizedPath.Contains("/repository/", StringComparison.Ordinal)
            || normalizedPath.Contains("/mapper", StringComparison.Ordinal)
            || normalizedPath.Contains("/copy/", StringComparison.Ordinal))
        {
            return BackendData;
        }

        return BackendOther;
    }

    /// <summary>
    /// 按路径片段推断前端产出角色。
    /// </summary>
    /// <param name="normalizedPath">小写归一后的输出路径。</param>
    /// <param name="fileName">文件名（小写）。</param>
    /// <returns>角色文本。</returns>
    private static string InferWebRole(string normalizedPath, string fileName)
    {
        if (normalizedPath.Contains("/pages/", StringComparison.Ordinal))
        {
            return WebPage;
        }

        if (normalizedPath.Contains("/api/", StringComparison.Ordinal))
        {
            return WebApi;
        }

        if (normalizedPath.Contains("/dto/", StringComparison.Ordinal))
        {
            return WebDto;
        }

        if (fileName.Contains("router", StringComparison.Ordinal) || fileName.Contains("routes", StringComparison.Ordinal))
        {
            return WebRoute;
        }

        if (fileName.Contains("menu", StringComparison.Ordinal))
        {
            return WebMenu;
        }

        return WebOther;
    }

    /// <summary>
    /// 取路径最后一段（文件名）；空路径返回空串。
    /// </summary>
    /// <param name="normalizedPath">归一后的路径。</param>
    /// <returns>最后一段。</returns>
    private static string GetLastSegment(string normalizedPath)
    {
        if (string.IsNullOrEmpty(normalizedPath))
        {
            return string.Empty;
        }

        int index = normalizedPath.LastIndexOf('/');
        return index < 0 ? normalizedPath : normalizedPath[(index + 1)..];
    }
}
