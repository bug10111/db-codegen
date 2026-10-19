using DbCodeGen.Core.Generation;
using DbCodeGen.Core.Templates.Packages;

namespace DbCodeGen.Core.Templates;

/// <summary>
/// 渲染上下文的 package 侧，由模板包运行时信息派生，注入模板的 package 变量。
/// 承载包名、基础包名、归一后的包段、输出目录占位、权限模块、生效命名策略与当前包类型映射表，
/// 供 tool.type / tool.packagePath / tool.permissionKey 实时计算。
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
    /// 输出目录占位，由基础包名逐段归一后以斜杠连接（如 com.example → com/example），供 {{package.dir}} 占位。
    /// 包段按 packageSegmentCase 口径归一（缺省全小写）并做连续重复段去重。
    /// </summary>
    public string Dir { get; }

    /// <summary>
    /// 归一后的基础包段清单（全小写、kebab 归一、连续重复段去重），供 tool.packagePath 组装分层包路径。
    /// </summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>
    /// 生效权限模块（请求 vars.permissionModule &gt; manifest.permissionModule &gt; 基础包名末段 kebab 派生），
    /// 供 {{tool.permissionKey(op)}} 与 {{package.permissionModule}} 统一消费。
    /// </summary>
    public string PermissionModule { get; }

    /// <summary>
    /// 生效命名策略，供 tool.packagePath 与模板内 package.naming.* 消费。
    /// </summary>
    public ResolvedNamingPolicy Naming { get; }

    /// <summary>
    /// 包根目录绝对路径，供渲染侧定位包内共享资源（如共享函数文件）；测试直构上下文时可为 null。
    /// </summary>
    public string? RootPath { get; }

    /// <summary>
    /// 当前包的类型映射表快照，数据库原始类型到目标语言类型，供 tool.type 实时计算。
    /// </summary>
    public IReadOnlyDictionary<string, string> TypeMap { get; }

    /// <summary>
    /// 使用完整字段构造渲染上下文，仅由工厂方法调用。
    /// </summary>
    private TemplatePackageContext(
        string name,
        string? basePackage,
        string dir,
        IReadOnlyList<string> segments,
        string permissionModule,
        ResolvedNamingPolicy naming,
        string? rootPath,
        IReadOnlyDictionary<string, string> typeMap)
    {
        Name = name;
        BasePackage = basePackage;
        Dir = dir;
        Segments = segments;
        PermissionModule = permissionModule;
        Naming = naming;
        RootPath = rootPath;
        TypeMap = typeMap;
    }

    /// <summary>
    /// 由模板包运行时信息派生渲染上下文：解析生效命名策略与权限模块，拷贝类型映射快照，
    /// 并按基础包名逐段归一计算包段与输出目录占位。可传本次生成的包名覆盖值（如生成栏临时填写的完整包名），
    /// 非空时取代 manifest 基础包名。
    /// </summary>
    /// <param name="package">模板包运行时信息。</param>
    /// <param name="basePackageOverride">本次生成的包名覆盖值，可为空；为空时使用 manifest 基础包名。</param>
    /// <param name="variables">本次请求的调用方变量，可为空；同名键优先于 manifest 命名策略与权限模块声明。</param>
    /// <param name="modules">本次请求的模块清单，可为空；非空时权限模块自动取首模块名 kebab 形态（与脚手架权限点清单同源）。</param>
    /// <returns>package 侧渲染上下文。</returns>
    /// <exception cref="ArgumentNullException">package 为 null 时抛出。</exception>
    /// <exception cref="GenerationException">packageSegmentCase 取值非法时抛出。</exception>
    public static TemplatePackageContext From(
        TemplatePackageInfo package,
        string? basePackageOverride = null,
        IReadOnlyDictionary<string, object?>? variables = null,
        IReadOnlyList<ModuleContext>? modules = null)
    {
        ArgumentNullException.ThrowIfNull(package);

        string? basePackage = string.IsNullOrWhiteSpace(basePackageOverride) ? package.BasePackage : basePackageOverride.Trim();
        ResolvedNamingPolicy naming = ResolvedNamingPolicy.Resolve(package.Naming, variables);
        IReadOnlyList<string> segments = PackagePathBuilder.NormalizeBaseSegments(basePackage, naming.PackageSegmentCase);
        string permissionModule = PermissionModuleResolver.Resolve(basePackage, package.PermissionModule, variables, modules);
        var typeMapSnapshot = new Dictionary<string, string>(package.TypeMap ?? new Dictionary<string, string>());
        return new TemplatePackageContext(
            package.Name,
            basePackage,
            PackagePathBuilder.ToDirectoryPath(segments),
            segments,
            permissionModule,
            naming,
            package.RootPath,
            typeMapSnapshot);
    }
}
