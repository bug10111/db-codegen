using DbCodeGen.Core.Templates;
using Scriban;
using Scriban.Runtime;

namespace DbCodeGen.Core.Tests.Templates;

/// <summary>
/// 空迭代告警分析器单元测试，覆盖空集合/未提供变量的零迭代告警、非空集合零告警、
/// 守卫伴随降噪标记、静态不可取值形态不告警，以及渲染管线 Warnings 透出（三入口共用管线单点）。
/// </summary>
public sealed class EmptyIterationGuardTests
{
    /// <summary>
    /// 构造渲染根上下文并注入 vars 子对象。
    /// </summary>
    /// <param name="vars">vars 内键值集合，可为 null 表示不注入 vars。</param>
    /// <returns>渲染根上下文。</returns>
    private static ScriptObject BuildRoot(IReadOnlyDictionary<string, object?>? vars)
    {
        var root = new ScriptObject();
        if (vars is not null)
        {
            var varsObject = new ScriptObject();
            foreach (KeyValuePair<string, object?> pair in vars)
            {
                varsObject.SetValue(pair.Key, pair.Value, true);
            }

            root.SetValue("vars", varsObject, true);
        }

        return root;
    }

    /// <summary>
    /// 解析模板并执行空迭代分析。
    /// </summary>
    /// <param name="templateText">模板文本。</param>
    /// <param name="root">渲染根上下文。</param>
    /// <returns>告警条目列表。</returns>
    private static IReadOnlyList<EmptyIterationWarning> Analyze(string templateText, ScriptObject root)
    {
        Template template = Template.Parse(templateText, "test.scriban");
        Assert.False(template.HasErrors, string.Join(";", template.Messages.Select(message => message.Message)));
        return EmptyIterationGuard.FindEmptyIterations(template, root);
    }

    /// <summary>
    /// 迭代源 vars 集合为空数组时应产出一条告警，定位到迭代表达式行列并含迭代源路径。
    /// </summary>
    [Fact]
    public void FindEmptyIterations_EmptyArray_ProducesWarning()
    {
        ScriptObject root = BuildRoot(new Dictionary<string, object?> { ["items"] = new ScriptArray() });

        IReadOnlyList<EmptyIterationWarning> warnings = Analyze(
            "前导行\n{{ for x in vars.items }}row:{{ x }}{{ end }}\n", root);

        EmptyIterationWarning warning = Assert.Single(warnings);
        Assert.Equal("vars.items", warning.SourcePath);
        Assert.Contains("集合为空", warning.Message);
        Assert.Equal(2, warning.Line);
        Assert.False(warning.GuardedBySameVariable);
    }

    /// <summary>
    /// 迭代源变量未提供（缺失即 null，Scriban 静默零迭代）时应产出告警并注明未提供。
    /// </summary>
    [Fact]
    public void FindEmptyIterations_MissingVariable_ProducesNullWarning()
    {
        ScriptObject root = BuildRoot(new Dictionary<string, object?>());

        IReadOnlyList<EmptyIterationWarning> warnings = Analyze(
            "{{ for m in modules }}{{ m.name }}{{ end }}", root);

        EmptyIterationWarning warning = Assert.Single(warnings);
        Assert.Equal("modules", warning.SourcePath);
        Assert.Contains("未提供或为 null", warning.Message);
    }

    /// <summary>
    /// 迭代源为非空集合时不应产出告警。
    /// </summary>
    [Fact]
    public void FindEmptyIterations_NonEmptyArray_NoWarning()
    {
        var array = new ScriptArray();
        array.Add("a");
        array.Add("b");
        ScriptObject root = BuildRoot(new Dictionary<string, object?> { ["items"] = array });

        IReadOnlyList<EmptyIterationWarning> warnings = Analyze(
            "{{ for x in vars.items }}row:{{ x }}{{ end }}", root);

        Assert.Empty(warnings);
    }

    /// <summary>
    /// 迭代源被同变量条件守卫包裹（守卫文案设计）时应标记守卫伴随且文案含可忽略指引。
    /// </summary>
    [Fact]
    public void FindEmptyIterations_GuardedBySameVariable_MarksGuardedAndDegradesMessage()
    {
        ScriptObject root = BuildRoot(new Dictionary<string, object?>());

        IReadOnlyList<EmptyIterationWarning> warnings = Analyze(
            "{{ if modules }}守卫文案\n{{ for m in modules }}{{ m.name }}{{ end }}\n{{ end }}", root);

        EmptyIterationWarning warning = Assert.Single(warnings);
        Assert.True(warning.GuardedBySameVariable);
        Assert.Contains("可忽略", warning.Message);
        Assert.Contains("守卫", warning.Message);
    }

    /// <summary>
    /// 迭代源为函数调用等静态无法取值的形态时不应告警，防误报。
    /// </summary>
    [Fact]
    public void FindEmptyIterations_FunctionCallSource_NoWarning()
    {
        ScriptObject root = BuildRoot(new Dictionary<string, object?>());

        IReadOnlyList<EmptyIterationWarning> warnings = Analyze(
            "{{ for x in array.range 1 3 }}{{ x }}{{ end }}", root);

        Assert.Empty(warnings);
    }

    /// <summary>
    /// 迭代源为字符串标量时不应告警（标量误迭代归 CollectionIterationGuard 结构化拒绝射程，防双报）。
    /// </summary>
    [Fact]
    public void FindEmptyIterations_ScalarSource_NoEmptyIterationWarning()
    {
        ScriptObject root = BuildRoot(new Dictionary<string, object?> { ["items"] = "abc" });

        IReadOnlyList<EmptyIterationWarning> warnings = Analyze(
            "{{ for x in vars.items }}{{ x }}{{ end }}", root);

        Assert.Empty(warnings);
    }

    /// <summary>
    /// vars 成员链中途缺失（vars 对象存在但子键缺失）应产出 null 取值告警。
    /// </summary>
    [Fact]
    public void FindEmptyIterations_MissingMemberInChain_ProducesNullWarning()
    {
        ScriptObject root = BuildRoot(new Dictionary<string, object?>());

        IReadOnlyList<EmptyIterationWarning> warnings = Analyze(
            "{{ for t in vars.project.tables }}{{ t.name }}{{ end }}", root);

        EmptyIterationWarning warning = Assert.Single(warnings);
        Assert.Equal("vars.project.tables", warning.SourcePath);
    }

    /// <summary>
    /// 渲染管线（三入口共用单点）应把空迭代告警透出到 PreviewResult.Warnings，渲染结果本身不受影响。
    /// </summary>
    [Fact]
    public void Render_EmptyIteration_SurfacesWarningsInPreviewResult()
    {
        var engine = new TemplateEngine();
        var package = new DbCodeGen.Core.Templates.Packages.TemplatePackageInfo
        {
            Name = "pkg",
            BasePackage = "com.example"
        };
        var table = new DbCodeGen.Core.Model.TableInfo
        {
            RawName = "sys_user",
            ClassName = "SysUser",
            VariableName = "sysUser"
        };
        ScopeRenderInput scopeInput = ScopeRenderInput.Create(
            new Dictionary<string, object?>(), null, new[] { table });

        PreviewResult result = engine.Render(
            "header\n{{ for x in vars.items }}row:{{ x }}{{ end }}\ntail\n",
            table,
            null,
            TemplatePackageContext.From(package),
            CancellationToken.None,
            "demo.scriban",
            scopeInput);

        Assert.True(result.IsSuccess);
        string warning = Assert.Single(result.Warnings);
        Assert.Contains("vars.items", warning);
        Assert.Contains("header", result.Output);
        Assert.Contains("tail", result.Output);
    }
}
