using DbCodeGen.Core.Generation;
using DbCodeGen.Core.Model;

namespace DbCodeGen.Core.Templates;

/// <summary>
/// 作用域渲染输入：承载 module / project 作用域模板渲染所需的 vars 变量、当前模块与本次全表汇总。
/// 由调用方经 Create 工厂构造，构造时执行变量命名空间冲突校验；module 作用域必须携带当前模块。
/// </summary>
public sealed class ScopeRenderInput
{
    /// <summary>
    /// 渲染上下文保留命名空间名单：vars.* 键与名单冲突时结构化拒绝，
    /// 防止调用方变量遮蔽 table / column / package / tool / vars / module / tables 根变量。
    /// </summary>
    public static readonly IReadOnlyList<string> ReservedNames = new[]
    {
        "table", "column", "package", "tool", "vars", "module", "tables"
    };

    /// <summary>
    /// 调用方注入变量集合，逐键注入渲染上下文 vars.* 命名空间；为空时上下文不注入 vars。
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Variables { get; }

    /// <summary>
    /// 当前模块上下文，注入渲染上下文 module.* 命名空间；仅 module 作用域渲染携带，project 作用域为 null。
    /// </summary>
    public ModuleContext? Module { get; }

    /// <summary>
    /// 本次调用的全表汇总，注入渲染上下文 tables 集合（元素字段与 table.* 同构）供模板循环登记胶水代码；可为 null。
    /// </summary>
    public IReadOnlyList<TableInfo>? Tables { get; }

    /// <summary>
    /// 使用冲突校验后的变量集合、当前模块与全表汇总构造作用域渲染输入。
    /// </summary>
    /// <param name="variables">调用方注入变量集合，可为空。</param>
    /// <param name="module">当前模块上下文，可为空。</param>
    /// <param name="tables">本次全表汇总，可为空。</param>
    /// <exception cref="GenerationException">variables 键与保留命名空间冲突时抛出。</exception>
    private ScopeRenderInput(
        IReadOnlyDictionary<string, object?>? variables,
        ModuleContext? module,
        IReadOnlyList<TableInfo>? tables)
    {
        Variables = variables;
        Module = module;
        Tables = tables;
    }

    /// <summary>
    /// 创建作用域渲染输入：对 variables 逐键执行保留命名空间冲突校验，冲突即结构化拒绝并列出全部冲突键。
    /// </summary>
    /// <param name="variables">调用方注入变量集合，可为空。</param>
    /// <param name="module">当前模块上下文，可为空。</param>
    /// <param name="tables">本次全表汇总，可为空。</param>
    /// <returns>校验通过的作用域渲染输入。</returns>
    /// <exception cref="GenerationException">variables 键与保留命名空间冲突时抛出。</exception>
    public static ScopeRenderInput Create(
        IReadOnlyDictionary<string, object?>? variables,
        ModuleContext? module = null,
        IReadOnlyList<TableInfo>? tables = null)
    {
        if (variables is not null && variables.Count > 0)
        {
            List<string> conflicts = new();
            foreach (string key in variables.Keys)
            {
                if (ReservedNames.Contains(key, StringComparer.Ordinal))
                {
                    conflicts.Add(key);
                }
            }

            if (conflicts.Count > 0)
            {
                throw new GenerationException(
                    $"variables 变量名与保留命名空间冲突：{string.Join("、", conflicts)}；保留命名空间为 {string.Join(" / ", ReservedNames)}，请改用其它变量名。");
            }
        }

        return new ScopeRenderInput(variables, module, tables);
    }
}
