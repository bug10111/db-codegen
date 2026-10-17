using DbCodeGen.Core.Templates;

namespace DbCodeGen.Core.Tests.Templates;

/// <summary>
/// tool 函数集单元测试：javaIdent 的 kebab 归一化语义（合法 Java 标识符形态），
/// 覆盖连字符删除、大小写归一、下划线保留与非 Java 字符不被静默吞掉。
/// </summary>
public sealed class ToolFunctionsTests
{
    /// <summary>
    /// javaIdent 应把命名统一降为小写并删除 kebab 连字符（下划线等合法标识符字符保留），
    /// 驼峰输入大小写归一后的结果与 hump3Underline 断词再删连字符一致。
    /// </summary>
    /// <param name="input">命名来源。</param>
    /// <param name="expected">期望的 Java 标识符形态。</param>
    [Theory]
    [InlineData("asset", "asset")]
    [InlineData("user-center", "usercenter")]
    [InlineData("org-member", "orgmember")]
    [InlineData("userCenter", "usercenter")]
    [InlineData("UserCenter", "usercenter")]
    [InlineData("underscore_name", "underscore_name")]
    [InlineData("user center", "user center")]
    [InlineData("", "")]
    public void JavaIdent_NormalizesToJavaIdentifierForm(string input, string expected)
    {
        Assert.Equal(expected, ToolFunctions.JavaIdent(input));
    }

    /// <summary>
    /// 入参为 null 时 javaIdent 返回空串，不抛异常。
    /// </summary>
    [Fact]
    public void JavaIdent_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ToolFunctions.JavaIdent(null));
    }
}
