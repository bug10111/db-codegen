using System.Diagnostics.CodeAnalysis;
using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;
using Scriban.Syntax;

namespace DbCodeGen.Core.Templates;

/// <summary>
/// 集合消费守卫：渲染前定位模板中 <c>for x in vars.*</c> 形态的集合迭代表达式，并对解析后的标量值
/// （字符串/数值/布尔等）结构化拒绝，避免 Scriban 对字符串逐字符迭代产出垃圾内容。
/// 根变量 modules / tables 由 Core 注入 ScriptArray，天然可迭代，不在守卫射程内；守卫对象是调用方
/// 经 variables 注入的 vars.* 命名空间值。路径占位渲染（RenderPathTemplate / RenderScopedPathTemplate）
/// 不接入本守卫：路径模板由模板包作者编写、不消费调用方 variables 集合，无可守卫面。
/// </summary>
internal static class CollectionIterationGuard
{
    /// <summary>
    /// 扫描模板 AST，返回首个"for 迭代 vars.* 标量值"的结构化错误；无命中时返回 null。
    /// </summary>
    /// <param name="template">已解析成功的模板。</param>
    /// <param name="root">已组装的渲染根上下文，用于解析 vars.* 实际值。</param>
    /// <returns>错误消息与 1 基行列；模板无标量集合消费时为 null。</returns>
    public static (string Message, int Line, int Column)? FindScalarIteration(Template template, ScriptObject root)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(root);

        // 解析成功后 Page 理论上非空，此处仍按可空契约防御，避免语法树异常形态时崩溃
        return template.Page is null ? null : Walk(template.Page, root);
    }

    /// <summary>
    /// 递归遍历语法树：命中 for 语句时检查其迭代表达式是否为 vars.* 成员链且解析值为标量；
    /// 未命中继续遍历子节点（含函数体与嵌套语句）。
    /// </summary>
    /// <param name="node">当前语法树节点。</param>
    /// <param name="root">渲染根上下文。</param>
    /// <returns>首个结构化错误；无命中为 null。</returns>
    private static (string Message, int Line, int Column)? Walk(ScriptNode node, ScriptObject root)
    {
        if (node is ScriptForStatement forStatement)
        {
            if (TryGetVarsMemberPath(forStatement.Iterator, out string? path)
                && TryResolveScalar(root, path, out object? scalarValue))
            {
                // 行列取迭代表达式位置，精确指向被守卫拒绝的集合消费写法
                SourceSpan span = forStatement.Iterator?.Span ?? forStatement.Span;
                return (FormatMessage(path, scalarValue), span.Start.Line + 1, span.Start.Column + 1);
            }
        }

        foreach (ScriptNode child in node.Children)
        {
            (string, int, int)? found = Walk(child, root);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// 判定迭代表达式是否为 <c>vars.a.b</c> 形态的成员链：最内层目标必须是全局变量 vars，
    /// 其余逐级成员名按内层到外层还原为点分路径。
    /// </summary>
    /// <param name="expression">for 语句的迭代表达式。</param>
    /// <param name="path">还原出的 vars 内成员路径（不含 vars 前缀）；非 vars 成员链时为 null。</param>
    /// <returns>是 vars.* 成员链时为 true。</returns>
    private static bool TryGetVarsMemberPath(ScriptExpression? expression, [NotNullWhen(true)] out string? path)
    {
        path = null;
        if (expression is not ScriptMemberExpression memberExpression)
        {
            return false;
        }

        var segments = new List<string>();
        ScriptExpression? current = memberExpression;
        while (current is ScriptMemberExpression member)
        {
            if (member.Member is not ScriptVariableGlobal variable)
            {
                return false;
            }

            segments.Insert(0, variable.Name);
            current = member.Target;
        }

        if (current is not ScriptVariableGlobal rootVariable || rootVariable.Name != "vars")
        {
            return false;
        }

        path = string.Join(".", segments);
        return true;
    }

    /// <summary>
    /// 从渲染根上下文解析 vars 内成员路径的值，并判定是否为不可迭代的标量。
    /// </summary>
    /// <param name="root">渲染根上下文。</param>
    /// <param name="path">vars 内点分成员路径。</param>
    /// <param name="scalarValue">命中的标量值。</param>
    /// <returns>路径存在且值为标量时为 true。</returns>
    private static bool TryResolveScalar(ScriptObject root, string path, out object? scalarValue)
    {
        scalarValue = null;
        if (!root.TryGetValue("vars", out object? varsValue) || varsValue is not ScriptObject varsObject)
        {
            return false;
        }

        object? current = varsObject;
        foreach (string segment in path.Split('.'))
        {
            if (current is not ScriptObject scriptObject)
            {
                return false;
            }

            // Scriban TryGetValue 的出参可空：成员缺失时按未命中处理，不触发守卫
            scriptObject.TryGetValue(segment, out object? next);
            current = next;
        }

        if (IsScalar(current))
        {
            scalarValue = current;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 判定值是否为 Scriban 不可安全集合迭代的标量：字符串/字符/布尔/数值与日期时间；
    /// ScriptArray、ScriptObject、ScriptRange 等容器不属标量。
    /// </summary>
    /// <param name="value">待判定值，可为空。</param>
    /// <returns>标量时为 true。</returns>
    private static bool IsScalar(object? value)
    {
        return value is string
            or char
            or bool
            or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal
            or DateTime or DateTimeOffset or TimeSpan;
    }

    /// <summary>
    /// 组装守卫错误消息：包含键名、实际值类型与数组修正指引，供结构化错误定位。
    /// </summary>
    /// <param name="path">vars 内点分成员路径。</param>
    /// <param name="value">实际标量值。</param>
    /// <returns>错误消息文本。</returns>
    private static string FormatMessage(string path, object? value)
    {
        return $"vars.{path} 的值类型为 {DescribeType(value)}，不能被 for 集合迭代；"
            + $"集合消费键必须传数组，如 variables: {{\"{path}\": [...]}}。";
    }

    /// <summary>
    /// 将标量值映射为面向调用方的中文类型描述，便于定位误传的 variables 类型。
    /// </summary>
    /// <param name="value">实际标量值。</param>
    /// <returns>类型描述文本。</returns>
    private static string DescribeType(object? value)
    {
        return value switch
        {
            string => "字符串(string)",
            char => "字符(char)",
            bool => "布尔(bool)",
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => "数值(number)",
            DateTime or DateTimeOffset or TimeSpan => "时间(datetime)",
            _ => value?.GetType().Name ?? "null"
        };
    }
}
