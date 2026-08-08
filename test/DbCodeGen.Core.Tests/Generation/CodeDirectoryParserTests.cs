using DbCodeGen.Core.Generation;
using Xunit;

namespace DbCodeGen.Core.Tests.Generation;

/// <summary>
/// 代码目录解析器单元测试：覆盖单体、微服务、无包名、纯包路径与路径分隔符归一。
/// </summary>
public class CodeDirectoryParserTests
{
    /// <summary>
    /// 单体项目：源根标记 java 之后的段应转为包名，之前作为相对输出根。
    /// </summary>
    [Theory]
    [InlineData("src/main/java/com/example/common", "com.example.common", "src/main/java")]
    [InlineData("src/main/java/com/example", "com.example", "src/main/java")]
    public void Split_Monolith_DerivesPackageAndRoot(string codeDirectory, string expectedPackage, string expectedRoot)
    {
        (string package, string root) = CodeDirectoryParser.Split(codeDirectory);

        Assert.Equal(expectedPackage, package);
        Assert.Equal(expectedRoot, root);
    }

    /// <summary>
    /// 微服务项目：模块前缀 + 源根标记 java 之前的段作为输出根，其后作为包名。
    /// </summary>
    [Fact]
    public void Split_Microservice_KeepsModulePrefixAsRoot()
    {
        (string package, string root) = CodeDirectoryParser.Split("order-service/src/main/java/com/example/order");

        Assert.Equal("com.example.order", package);
        Assert.Equal("order-service/src/main/java", root);
    }

    /// <summary>
    /// 无包名部分（仅到源根）时包名为空串，相对输出根为源根，回落模板包 manifest 包名。
    /// </summary>
    [Theory]
    [InlineData("src/main/java")]
    [InlineData("src/main/kotlin")]
    public void Split_NoPackagePart_ReturnsEmptyPackage(string codeDirectory)
    {
        (string package, string root) = CodeDirectoryParser.Split(codeDirectory);

        Assert.Equal(string.Empty, package);
        Assert.Equal(codeDirectory, root);
    }

    /// <summary>
    /// 纯 package（无源根标记，斜杠或点号分隔）时整段作为包名，输出根固定为 src/main/java。
    /// </summary>
    [Theory]
    [InlineData("com/example/common")]
    [InlineData("com.example.common")]
    public void Split_PurePackage_UsesSourceRootAsOutputRoot(string codeDirectory)
    {
        (string package, string root) = CodeDirectoryParser.Split(codeDirectory);

        Assert.Equal("com.example.common", package);
        Assert.Equal(CodeDirectoryParser.SourceRoot, root);
    }

    /// <summary>
    /// 反斜杠路径与首尾斜杠应归一化处理。
    /// </summary>
    [Theory]
    [InlineData("src\\main\\java\\com\\example\\common", "com.example.common", "src/main/java")]
    [InlineData("/src/main/java/com/example/common/", "com.example.common", "src/main/java")]
    public void Split_BackslashAndTrailingSlash_Normalized(string codeDirectory, string expectedPackage, string expectedRoot)
    {
        (string package, string root) = CodeDirectoryParser.Split(codeDirectory);

        Assert.Equal(expectedPackage, package);
        Assert.Equal(expectedRoot, root);
    }
}
