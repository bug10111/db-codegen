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
    /// 本次调用的全模块清单，注入渲染上下文 modules 集合（元素字段与 module.* 同构）供 project 模板循环模块聚合段；
    /// 批量生成管线中仅 project 作用域输入携带（module 作用域自身即单模块上下文，经 Module 注入），
    /// 单模板预览等调用方可按需显式携带以预览聚合段；可为 null。
    /// 与用户显式传入的 vars.modules 分属不同命名空间（根变量 vs vars 子键），同名互不干扰。
    /// </summary>
    public IReadOnlyList<ModuleContext>? Modules { get; }

    /// <summary>
    /// 使用冲突校验后的变量集合、当前模块、全表汇总与全模块清单构造作用域渲染输入。
    /// </summary>
    /// <param name="variables">调用方注入变量集合，可为空。</param>
    /// <param name="module">当前模块上下文，可为空。</param>
    /// <param name="tables">本次全表汇总，可为空。</param>
    /// <param name="modules">本次全模块清单，project 作用域渲染消费，可为空。</param>
    /// <exception cref="GenerationException">variables 键与保留命名空间冲突时抛出。</exception>
    private ScopeRenderInput(
        IReadOnlyDictionary<string, object?>? variables,
        ModuleContext? module,
        IReadOnlyList<TableInfo>? tables,
        IReadOnlyList<ModuleContext>? modules)
    {
        Variables = variables;
        Module = module;
        Tables = tables;
        Modules = modules;
    }

    /// <summary>
    /// 创建作用域渲染输入：对 variables 逐键执行保留命名空间冲突校验，冲突即结构化拒绝并列出全部冲突键。
    /// </summary>
    /// <param name="variables">调用方注入变量集合，可为空。</param>
    /// <param name="module">当前模块上下文，可为空。</param>
    /// <param name="tables">本次全表汇总，可为空。</param>
    /// <param name="modules">本次全模块清单，project 作用域渲染时注入 modules 集合，可为空。</param>
    /// <returns>校验通过的作用域渲染输入。</returns>
    /// <exception cref="GenerationException">variables 键与保留命名空间冲突时抛出。</exception>
    public static ScopeRenderInput Create(
        IReadOnlyDictionary<string, object?>? variables,
        ModuleContext? module = null,
        IReadOnlyList<TableInfo>? tables = null,
        IReadOnlyList<ModuleContext>? modules = null)
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

        return new ScopeRenderInput(variables, module, tables, modules);
    }
}
