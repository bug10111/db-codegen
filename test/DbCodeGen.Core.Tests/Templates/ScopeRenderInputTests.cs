using DbCodeGen.Core.Generation;
using DbCodeGen.Core.Templates;

namespace DbCodeGen.Core.Tests.Templates;

/// <summary>
/// 作用域渲染输入单元测试：保留命名空间冲突结构化拒绝、合法变量通过、工厂缺省行为。
/// </summary>
public sealed class ScopeRenderInputTests
{
    /// <summary>
    /// variables 键与保留命名空间冲突时应结构化拒绝，错误消息列出全部冲突键与保留名单。
    /// </summary>
    [Theory]
    [InlineData("table")]
    [InlineData("column")]
    [InlineData("package")]
    [InlineData("tool")]
    [InlineData("vars")]
    [InlineData("module")]
    [InlineData("tables")]
    public void Create_VariableKeyConflictsReservedNamespace_ThrowsWithConflictList(string reservedKey)
    {
        var variables = new Dictionary<string, object?> { [reservedKey] = "value", ["appName"] = "demo" };

        GenerationException exception = Assert.Throws<GenerationException>(() => ScopeRenderInput.Create(variables));

        Assert.Contains(reservedKey, exception.Message);
        Assert.Contains("保留命名空间", exception.Message);
    }

    /// <summary>
    /// variables 键不与保留命名空间冲突时应通过校验，变量与模块原样承载。
    /// </summary>
    [Fact]
    public void Create_NonConflictingVariables_Passes()
    {
        var variables = new Dictionary<string, object?> { ["appName"] = "demo", ["version"] = 1 };
        var module = new ModuleContext("account");

        ScopeRenderInput input = ScopeRenderInput.Create(variables, module);

        Assert.Same(variables, input.Variables);
        Assert.Same(module, input.Module);
    }

    /// <summary>
    /// 全部入参为空时应构造空输入（上下文不注入任何作用域变量）。
    /// </summary>
    [Fact]
    public void Create_AllEmpty_ReturnsEmptyInput()
    {
        ScopeRenderInput input = ScopeRenderInput.Create(null);

        Assert.Null(input.Variables);
        Assert.Null(input.Module);
        Assert.Null(input.Tables);
    }

    /// <summary>
    /// 保留命名空间名单应恰好包含七个根变量名。
    /// </summary>
    [Fact]
    public void ReservedNames_ContainsExactSevenNames()
    {
        string[] expected = { "table", "column", "package", "tool", "vars", "module", "tables" };

        Assert.Equal(expected, ScopeRenderInput.ReservedNames);
    }

    /// <summary>
    /// 模块上下文名为空白时应拒绝。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ModuleContext_BlankName_Throws(string name)
    {
        Assert.Throws<ArgumentException>(() => new ModuleContext(name));
    }
}
