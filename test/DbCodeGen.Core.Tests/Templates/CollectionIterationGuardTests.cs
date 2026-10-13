using DbCodeGen.Core.Generation;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;

namespace DbCodeGen.Core.Tests.Templates;

/// <summary>
/// 集合消费守卫单元测试：vars.* 集合消费键传入字符串/布尔/数值等标量时必须结构化失败，
/// 禁止 Scriban 静默逐字符迭代产出垃圾内容；真实数组与 Core 注入的根变量 modules 不受影响。
/// </summary>
public sealed class CollectionIterationGuardTests
{
    private readonly TemplateEngine _engine = new();

    /// <summary>
    /// render_template 等价路径（TemplateEngine.Render）：vars 集合键传字符串时应结构化拒绝，
    /// 错误含模板名、键名、行列与数组修正指引，且不产出逐字符内容。
    /// </summary>
    [Fact]
    public void Render_StringCollectionKey_ReturnsStructuredError()
    {
        var scope = ScopeRenderInput.Create(new Dictionary<string, object?> { ["ssoModules"] = "asset" });

        PreviewResult result = _engine.Render(
            "{{ for m in vars.ssoModules }}<module>{{ m }}</module>{{ end }}",
            CreateTestTable(),
            null,
            CreateTestPackageContext(),
            CancellationToken.None,
            templateName: "pom-aggregate.xml.scriban",
            scopeContext: scope);

        Assert.False(result.IsSuccess);
        Assert.Equal(string.Empty, result.Output);
        Assert.Contains("pom-aggregate.xml.scriban", result.ErrorMessage);
        Assert.Contains("vars.ssoModules", result.ErrorMessage);
        Assert.Contains("字符串(string)", result.ErrorMessage);
        Assert.Contains("第 1 行", result.ErrorMessage);
        Assert.Contains("集合消费键必须传数组", result.ErrorMessage);
        Assert.DoesNotContain("<module>a</module>", result.Output);
    }

    /// <summary>
    /// preview/generate 等价路径（TemplateEngine.RenderScoped）：vars 集合键传字符串时同样结构化拒绝。
    /// </summary>
    [Fact]
    public void RenderScoped_StringCollectionKey_ReturnsStructuredError()
    {
        var scope = ScopeRenderInput.Create(
            new Dictionary<string, object?> { ["modules"] = "GHOST" },
            null,
            Array.Empty<TableInfo>());

        PreviewResult result = _engine.RenderScoped(
            "{{ for m in vars.modules }}<m>{{ m }}</m>{{ end }}",
            CreateTestPackageContext(),
            scope,
            CancellationToken.None,
            templateName: "aggregate.scriban");

        Assert.False(result.IsSuccess);
        Assert.Contains("vars.modules", result.ErrorMessage);
        Assert.Contains("集合消费键必须传数组", result.ErrorMessage);
    }

    /// <summary>
    /// 布尔与数值标量同样不可被 for 集合迭代，应分别报告对应类型。
    /// </summary>
    [Fact]
    public void RenderScoped_BoolAndNumberCollectionKeys_ReturnsStructuredErrors()
    {
        var boolScope = ScopeRenderInput.Create(new Dictionary<string, object?> { ["flags"] = true });
        PreviewResult boolResult = _engine.RenderScoped(
            "{{ for f in vars.flags }}{{ f }}{{ end }}", CreateTestPackageContext(), boolScope, CancellationToken.None);
        Assert.False(boolResult.IsSuccess);
        Assert.Contains("布尔(bool)", boolResult.ErrorMessage);

        var numberScope = ScopeRenderInput.Create(new Dictionary<string, object?> { ["count"] = 3 });
        PreviewResult numberResult = _engine.RenderScoped(
            "{{ for n in vars.count }}{{ n }}{{ end }}", CreateTestPackageContext(), numberScope, CancellationToken.None);
        Assert.False(numberResult.IsSuccess);
        Assert.Contains("数值(number)", numberResult.ErrorMessage);
    }

    /// <summary>
    /// 嵌套成员路径 vars.config.items 传字符串时，错误应携带完整点分路径。
    /// </summary>
    [Fact]
    public void RenderScoped_NestedMemberPathScalar_ReturnsPathInError()
    {
        var scope = ScopeRenderInput.Create(new Dictionary<string, object?>
        {
            ["config"] = new Dictionary<string, object?> { ["items"] = "abc" }
        });

        PreviewResult result = _engine.RenderScoped(
            "{{ for item in vars.config.items }}{{ item }}{{ end }}",
            CreateTestPackageContext(),
            scope,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("vars.config.items", result.ErrorMessage);
    }

    /// <summary>
    /// 行列定位应指向模板中集合消费表达式所在行（第二行触发时报第 2 行）。
    /// </summary>
    [Fact]
    public void Render_StringCollectionKeyOnSecondLine_ReportsSecondLine()
    {
        var scope = ScopeRenderInput.Create(new Dictionary<string, object?> { ["items"] = "asset" });

        PreviewResult result = _engine.Render(
            "header\n{{ for item in vars.items }}{{ item }}{{ end }}",
            CreateTestTable(),
            null,
            CreateTestPackageContext(),
            CancellationToken.None,
            scopeContext: scope);

        Assert.False(result.IsSuccess);
        Assert.Contains("第 2 行", result.ErrorMessage);
    }

    /// <summary>
    /// vars 集合键传真实数组时应正常渲染，守卫不产生误伤。
    /// </summary>
    [Fact]
    public void Render_ArrayCollectionKey_RendersNormally()
    {
        var scope = ScopeRenderInput.Create(new Dictionary<string, object?>
        {
            ["modules"] = new[] { "asset", "report" }
        });

        PreviewResult result = _engine.Render(
            "{{ for m in vars.modules }}<module>{{ m }}</module>{{ end }}",
            CreateTestTable(),
            null,
            CreateTestPackageContext(),
            CancellationToken.None,
            scopeContext: scope);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("<module>asset</module><module>report</module>", result.Output);
    }

    /// <summary>
    /// Core 注入的根变量 modules（ScriptArray）消费不受守卫影响，仍按集合迭代渲染。
    /// </summary>
    [Fact]
    public void RenderScoped_RootModulesArray_RendersNormally()
    {
        var scope = ScopeRenderInput.Create(
            null,
            null,
            Array.Empty<TableInfo>(),
            new[] { new ModuleContext("asset", new Dictionary<string, object?>()) });

        PreviewResult result = _engine.RenderScoped(
            "{{ for m in modules }}<module>{{ m.name }}</module>{{ end }}",
            CreateTestPackageContext(),
            scope,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("<module>asset</module>", result.Output);
    }

    /// <summary>
    /// vars 内对象（ScriptObject）与缺失键不触发守卫：对象成员访问与空迭代保持既有渲染行为。
    /// </summary>
    [Fact]
    public void RenderScoped_ObjectVarsAndMissingKey_KeepExistingBehavior()
    {
        var scope = ScopeRenderInput.Create(new Dictionary<string, object?>
        {
            ["config"] = new Dictionary<string, object?> { ["appName"] = "demo" }
        });

        PreviewResult result = _engine.RenderScoped(
            "app={{ vars.config.appName }} items={{ for item in vars.missing }}{{ item }}{{ end }}done",
            CreateTestPackageContext(),
            scope,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("app=demo items=done", result.Output);
    }

    /// <summary>
    /// 构造守卫测试样例表元数据。
    /// </summary>
    /// <returns>最小可渲染表。</returns>
    private static TableInfo CreateTestTable()
    {
        var table = new TableInfo
        {
            RawName = "sys_user",
            ClassName = "SysUser",
            VariableName = "sysUser"
        };
        table.SetColumns(new[]
        {
            new ColumnInfo { RawName = "id", PropertyName = "id", RawDbType = "bigint", IsPrimaryKey = true }
        });
        return table;
    }

    /// <summary>
    /// 构造守卫测试用 package 侧渲染上下文。
    /// </summary>
    /// <returns>样例渲染上下文。</returns>
    private static TemplatePackageContext CreateTestPackageContext()
    {
        var package = new TemplatePackageInfo
        {
            Name = "guard-tests",
            TypeMap = new Dictionary<string, string>()
        };
        return TemplatePackageContext.From(package);
    }
}
