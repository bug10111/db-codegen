namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 内置模板包目录定位器。内置包随应用分发至输出目录 Templates\Builtin，只读不可变，
/// 与用户模板库（模板搜索目录）分离，保证只读边界。
/// </summary>
public static class BuiltinTemplatePackages
{
    /// <summary>
    /// 内置包根目录相对应用基目录的相对路径。
    /// </summary>
    public const string RelativeBuiltinRoot = "Templates\\Builtin";

    /// <summary>
    /// 计算默认内置包根目录绝对路径（应用基目录\Templates\Builtin）。
    /// </summary>
    /// <returns>内置包根目录绝对路径。</returns>
    public static string GetDefaultRootPath()
    {
        return Path.Combine(AppContext.BaseDirectory, RelativeBuiltinRoot);
    }
}
