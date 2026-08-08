using DbCodeGen.Core.Model;

namespace DbCodeGen.Core.Templates;

/// <summary>
/// 输出路径模板渲染上下文，承载路径占位所需的表信息与 package 侧上下文。
/// RenderPathTemplate 仅读取其中 table.variableName / table.className / package.dir 等路径占位字段。
/// </summary>
public sealed class TemplateRenderContext
{
    /// <summary>
    /// 当前表元数据，路径占位 {{table.variableName}} / {{table.className}} / {{table.rawName}} 取自此对象。
    /// </summary>
    public TableInfo Table { get; }

    /// <summary>
    /// package 侧渲染上下文，路径占位 {{package.dir}} / {{package.name}} / {{package.basePackage}} 取自此对象。
    /// </summary>
    public TemplatePackageContext Package { get; }

    /// <summary>
    /// 使用表信息与 package 上下文构造路径渲染上下文。
    /// </summary>
    /// <param name="table">当前表元数据。</param>
    /// <param name="package">package 侧渲染上下文。</param>
    /// <exception cref="ArgumentNullException">table 或 package 为 null 时抛出。</exception>
    public TemplateRenderContext(TableInfo table, TemplatePackageContext package)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(package);
        Table = table;
        Package = package;
    }
}
