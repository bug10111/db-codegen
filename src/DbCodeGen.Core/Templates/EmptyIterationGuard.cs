using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Scriban;
using Scriban.Runtime;
using Scriban.Syntax;

namespace DbCodeGen.Core.Templates;

/// <summary>
/// 空迭代告警分析器：渲染前定位模板中 <c>for x in &lt;集合&gt;</c> 形态的循环，解析迭代源变量当前取值，
/// 对空集合 / null / 未提供变量的零迭代循环产出结构化告警（非阻断），消除"空循环静默零迭代、产物关键段为空无信号"。
/// 误报控制：迭代源变量被同变量条件守卫包裹（祖先 #if 条件引用同一根变量，如「守卫文案 + for」的模板设计）时，
/// 告警文案切换为守卫伴随变体并注明可忽略，供调用方按模板设计意图降噪。分析对象与 CollectionIterationGuard 同族
/// （该守卫负责标量误迭代的结构化拒绝，本分析器负责可迭代空值的零迭代告警），共用渲染前 AST 分析入口。
/// </summary>
internal static class EmptyIterationGuard
{
    /// <summary>
    /// 扫描模板 AST，返回全部"for 迭代空集合 / null / 未提供变量"的告警条目；无命中时返回空列表。
    /// </summary>
    /// <param name="template">已解析成功的模板。</param>
    /// <param name="root">已组装的渲染根上下文，用于解析迭代源实际值。</param>
    /// <returns>告警条目列表（含行列、迭代源路径与完整文案），无空迭代时为空列表。</returns>
    public static IReadOnlyList<EmptyIterationWarning> FindEmptyIterations(Template template, ScriptObject root)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(root);

        var warnings = new List<EmptyIterationWarning>();
        if (template.Page is null)
        {
            return warnings;
        }

        Walk(template.Page, root, new List<ScriptIfStatement>(), warnings);
        return warnings;
    }

    /// <summary>
    /// 递归遍历语法树：命中 for 语句时解析迭代源取值，空值零迭代即收集告警；
    /// 进入 #if 语句时把条件压入祖先栈（供守卫伴随判定），统一走 children 递归
    /// （条件表达式子树无 for 语句子节点，入栈前后遍历顺序不影响语句收集）。
    /// </summary>
    /// <param name="node">当前语法树节点。</param>
    /// <param name="root">渲染根上下文。</param>
    /// <param name="ifAncestors">当前遍历路径上的 #if 祖先栈。</param>
    /// <param name="warnings">告警收集列表。</param>
    private static void Walk(
        ScriptNode node,
        ScriptObject root,
        List<ScriptIfStatement> ifAncestors,
        List<EmptyIterationWarning> warnings)
    {
        bool pushedIf = false;
        if (node is ScriptIfStatement ifStatement)
        {
            ifAncestors.Add(ifStatement);
            pushedIf = true;
        }

        if (node is ScriptForStatement forStatement)
        {
            TryCollectWarning(forStatement, root, ifAncestors, warnings);
        }

        foreach (ScriptNode child in node.Children)
        {
            Walk(child, root, ifAncestors, warnings);
        }

        if (pushedIf)
        {
            ifAncestors.RemoveAt(ifAncestors.Count - 1);
        }
    }

    /// <summary>
    /// 解析单条 for 语句的迭代源：成员链或根变量可解析且取值为空集合 / null 时收集告警；
    /// 标量取值不在此告警（CollectionIterationGuard 已在渲染前结构化拒绝），静态无法取值的表达式形态不告警防误报。
    /// </summary>
    /// <param name="forStatement">for 语句节点。</param>
    /// <param name="root">渲染根上下文。</param>
    /// <param name="ifAncestors">祖先 #if 栈，供守卫伴随判定。</param>
    /// <param name="warnings">告警收集列表。</param>
    private static void TryCollectWarning(
        ScriptForStatement forStatement,
        ScriptObject root,
        List<ScriptIfStatement> ifAncestors,
        List<EmptyIterationWarning> warnings)
    {
        if (!TryResolveIteratedSource(root, forStatement.Iterator, out string? sourcePath, out object? value))
        {
            return;
        }

        if (!TryDescribeEmpty(value, out string? emptyReason))
        {
            return;
        }

        // 行列取迭代表达式位置，与 CollectionIterationGuard 的定位口径一致（Scriban 0 基转 1 基）
        Scriban.Parsing.SourceSpan span = forStatement.Iterator?.Span ?? forStatement.Span;
        bool guardedBySameVariable = IsGuardedBySameVariable(ifAncestors, sourcePath);
        warnings.Add(new EmptyIterationWarning(
            sourcePath,
            emptyReason,
            span.Start.Line + 1,
            span.Start.Column + 1,
            guardedBySameVariable,
            FormatMessage(sourcePath, emptyReason, guardedBySameVariable)));
    }

    /// <summary>
    /// 解析 for 迭代表达式为「根变量.成员路径」并从渲染根上下文取值：根变量（tables / modules 等）
    /// 与 vars.* 成员链均可解析；函数调用、管道表达式等静态无法可靠取值的形态返回 false 不告警。
    /// </summary>
    /// <param name="root">渲染根上下文。</param>
    /// <param name="expression">for 语句的迭代表达式。</param>
    /// <param name="sourcePath">还原出的点分路径（根变量名起始，无成员时即根变量名）。</param>
    /// <param name="value">解析到的当前取值。</param>
    /// <returns>可解析并取到值时为 true。</returns>
    private static bool TryResolveIteratedSource(
        ScriptObject root,
        ScriptExpression? expression,
        [NotNullWhen(true)] out string? sourcePath,
        out object? value)
    {
        sourcePath = null;
        value = null;

        var segments = new List<string>();
        ScriptExpression? current = expression;
        while (current is ScriptMemberExpression member)
        {
            if (member.Member is not ScriptVariableGlobal memberVariable)
            {
                return false;
            }

            segments.Insert(0, memberVariable.Name);
            current = member.Target;
        }

        if (current is not ScriptVariableGlobal rootVariable)
        {
            return false;
        }

        segments.Insert(0, rootVariable.Name);

        if (!root.TryGetValue(segments[0], out object? rootValue))
        {
            // 根变量未提供：Scriban 对 null 迭代为静默零迭代，正是本守卫要消除的静默面
            sourcePath = string.Join(".", segments);
            value = null;
            return true;
        }

        object? cursor = rootValue;
        for (int index = 1; index < segments.Count; index++)
        {
            if (cursor is not ScriptObject cursorObject || !cursorObject.TryGetValue(segments[index], out object? next))
            {
                // 中途成员缺失同样表现为 null，Scriban 零迭代
                sourcePath = string.Join(".", segments);
                value = null;
                return true;
            }

            cursor = next;
        }

        sourcePath = string.Join(".", segments);
        value = cursor;
        return true;
    }

    /// <summary>
    /// 判定取值是否构成"空迭代"并产出原因描述：null / 空 ScriptArray / 空可迭代为空迭代；
    /// 标量与 ScriptObject 等其它形态不构成（标量误迭代归 CollectionIterationGuard 射程，防双报）。
    /// </summary>
    /// <param name="value">迭代源当前取值。</param>
    /// <param name="emptyReason">空迭代原因描述；非空迭代时为 null。</param>
    /// <returns>构成空迭代时为 true。</returns>
    private static bool TryDescribeEmpty(object? value, [NotNullWhen(true)] out string? emptyReason)
    {
        if (value is null)
        {
            emptyReason = "未提供或为 null";
            return true;
        }

        if (value is ScriptArray array && array.Count == 0)
        {
            emptyReason = "集合为空（0 个元素）";
            return true;
        }

        if (value is IEnumerable sequence && IsSequenceEmpty(sequence))
        {
            emptyReason = "集合为空（0 个元素）";
            return true;
        }

        emptyReason = null;
        return false;
    }

    /// <summary>
    /// 判定通用可迭代对象是否不含元素；ScriptArray 已在上游按 Count 判定，此处兜底范围与宿主集合类型。
    /// </summary>
    /// <param name="sequence">待判定可迭代对象。</param>
    /// <returns>不含元素时为 true。</returns>
    private static bool IsSequenceEmpty(IEnumerable sequence)
    {
        foreach (object? _ in sequence)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 判定 for 语句是否被"引用同一根变量的 #if 条件"守卫包裹：祖先栈内任一 #if 的条件子树
    /// 引用了迭代源路径的根变量名即视为守卫伴随（如 #if modules 包裹 for m in modules 的守卫文案设计）。
    /// </summary>
    /// <param name="ifAncestors">祖先 #if 栈。</param>
    /// <param name="sourcePath">迭代源点分路径。</param>
    /// <returns>守卫伴随时为 true。</returns>
    private static bool IsGuardedBySameVariable(List<ScriptIfStatement> ifAncestors, string sourcePath)
    {
        string rootVariableName = sourcePath.Split('.')[0];
        foreach (ScriptIfStatement ifStatement in ifAncestors)
        {
            if (ifStatement.Condition is not null
                && ConditionReferencesVariable(ifStatement.Condition, rootVariableName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 判定条件表达式子树（含表达式自身）是否引用指定全局变量名。
    /// </summary>
    /// <param name="expression">条件表达式节点。</param>
    /// <param name="variableName">待匹配的全局变量名。</param>
    /// <returns>引用该变量时为 true。</returns>
    private static bool ConditionReferencesVariable(ScriptExpression expression, string variableName)
    {
        if (expression is ScriptVariableGlobal variable
            && string.Equals(variable.Name, variableName, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (ScriptNode child in expression.Children)
        {
            if (child is ScriptExpression childExpression && ConditionReferencesVariable(childExpression, variableName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 组装告警消息：包含迭代源路径、空值原因与守卫模式降噪指引，供回执直接展示。
    /// </summary>
    /// <param name="sourcePath">迭代源点分路径。</param>
    /// <param name="emptyReason">空迭代原因。</param>
    /// <param name="guardedBySameVariable">是否被同变量条件守卫包裹。</param>
    /// <returns>告警消息文本。</returns>
    private static string FormatMessage(string sourcePath, string emptyReason, bool guardedBySameVariable)
    {
        if (guardedBySameVariable)
        {
            return $"for 循环迭代源 {sourcePath} 当前{emptyReason}，循环体零迭代（该循环由同变量条件守卫包裹）；"
                + "若为守卫文案设计（空集合时产物含说明文字属预期），本告警可忽略；否则请核对变量是否漏传或键名是否正确。";
        }

        return $"for 循环迭代源 {sourcePath} 当前{emptyReason}，循环体零迭代，渲染产物中该段为空；"
            + "请核对变量是否漏传或键名是否正确。若模板为守卫文案设计（空集合时产物含说明文字属预期），本告警可忽略。";
    }
}

/// <summary>
/// 空迭代告警条目：定位信息、迭代源路径、空值原因、守卫伴随标记与完整告警文案。
/// </summary>
/// <param name="SourcePath">迭代源点分路径（根变量名起始，如 vars.modules / tables）。</param>
/// <param name="EmptyReason">空迭代原因（未提供或为 null / 集合为空）。</param>
/// <param name="Line">告警所在模板行号（从 1 开始）。</param>
/// <param name="Column">告警所在模板列号（从 1 开始）。</param>
/// <param name="GuardedBySameVariable">迭代源是否被同变量条件守卫包裹（守卫文案设计降噪标记）。</param>
/// <param name="Message">完整告警文案（中文，含降噪指引）。</param>
internal sealed record EmptyIterationWarning(
    string SourcePath,
    string EmptyReason,
    int Line,
    int Column,
    bool GuardedBySameVariable,
    string Message);
