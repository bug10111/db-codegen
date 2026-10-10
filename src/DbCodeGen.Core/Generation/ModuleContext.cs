namespace DbCodeGen.Core.Generation;

/// <summary>
/// 一次生成调用中的单个模块上下文：name 必填，其余字段原样注入渲染上下文 module.* 供模板消费。
/// 由 MCP 调用方以 modules 数组元素（如 {"name":"account"}）显式传入，Core 不感知模块的业务含义。
/// </summary>
public sealed class ModuleContext
{
    /// <summary>
    /// 使用模块名与可选附加属性构造模块上下文。
    /// </summary>
    /// <param name="name">模块名（如 account），必填；模板内 {{module.name}} 取此值。</param>
    /// <param name="properties">name 之外的附加属性，原样注入 module.* 命名空间，可空。</param>
    /// <exception cref="ArgumentException">name 为空白时抛出。</exception>
    public ModuleContext(string name, IReadOnlyDictionary<string, object?>? properties = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("模块名（modules[].name）不能为空。", nameof(name));
        }

        Name = name.Trim();
        Properties = properties ?? new Dictionary<string, object?>();
    }

    /// <summary>
    /// 模块名，必填；模板内 {{module.name}} 取此值。
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// name 之外的附加属性，原样注入 module.* 命名空间（值经 Scriban 值转换后可被模板按字段访问）。
    /// </summary>
    public IReadOnlyDictionary<string, object?> Properties { get; }
}
