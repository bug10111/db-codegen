using DbCodeGen.Core.Templates.Packages;

namespace DbCodeGen.Core.Templates;

/// <summary>
/// 渲染上下文的 package 侧，由模板包运行时信息派生，注入模板的 package 变量。
/// 承载包名、基础包名、输出目录占位与当前包类型映射表，供 tool.type 实时映射使用。
/// </summary>
public sealed class TemplatePackageContext
{
    /// <summary>
    /// 包名，与模板包目录名一致。
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// 基础包名，可为空；为空时模板内 package.basePackage 渲染为空串。
    /// </summary>
    public string? BasePackage { get; }

    /// <summary>
    /// 输出目录占位，由基础包名按点号转斜杠派生（如 com.example → com/example），供 {{package.dir}} 占位。
    /// </summary>
    public string Dir { get; }

    /// <summary>
    /// 当前包的类型映射表快照，数据库原始类型到目标语言类型，供 tool.type 实时计算。
    /// </summary>
    public IReadOnlyDictionary<string, string> TypeMap { get; }

    /// <summary>
    /// 使用完整字段构造渲染上下文，仅由工厂方法调用。
    /// </summary>
    private TemplatePackageContext(string name, string? basePackage, string dir, IReadOnlyDictionary<string, string> typeMap)
    {
        Name = name;
        BasePackage = basePackage;
        Dir = dir;
        TypeMap = typeMap;
    }

    /// <summary>
    /// 由模板包运行时信息派生渲染上下文：拷贝类型映射快照，并按基础包名计算输出目录占位。
    /// </summary>
    /// <param name="package">模板包运行时信息。</param>
    /// <returns>package 侧渲染上下文。</returns>
    /// <exception cref="ArgumentNullException">package 为 null 时抛出。</exception>
    public static TemplatePackageContext From(TemplatePackageInfo package)
    {
        ArgumentNullException.ThrowIfNull(package);

        string dir = BuildDir(package.BasePackage);
        var typeMapSnapshot = new Dictionary<string, string>(package.TypeMap ?? new Dictionary<string, string>());
        return new TemplatePackageContext(package.Name, package.BasePackage, dir, typeMapSnapshot);
    }

    /// <summary>
    /// 将基础包名按点号拆分后以斜杠连接为输出目录，空值返回空串。
    /// </summary>
    /// <param name="basePackage">基础包名，如 com.example。</param>
    /// <returns>输出目录占位。</returns>
    private static string BuildDir(string? basePackage)
    {
        if (string.IsNullOrWhiteSpace(basePackage))
        {
            return string.Empty;
        }

        string[] parts = basePackage.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("/", parts);
    }
}
