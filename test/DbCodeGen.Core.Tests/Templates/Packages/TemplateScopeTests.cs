using DbCodeGen.Core.Templates.Packages;

namespace DbCodeGen.Core.Tests.Templates.Packages;

/// <summary>
/// 模板作用域单元测试：scope 文本解析（缺省 table / 合法三值 / 非法值结构化拒绝）与包加载集成（无 scope 字段既有包行为不变）。
/// </summary>
public sealed class TemplateScopeTests : IDisposable
{
    private readonly List<string> _tempRoots = new();

    /// <summary>
    /// 清理全部测试临时目录。
    /// </summary>
    public void Dispose()
    {
        foreach (string root in _tempRoots)
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// scope 文本为 null / 空白时应解析为缺省 table（向后兼容：无 scope 字段的既有包行为不变）。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_BlankScope_DefaultsToTable(string? scopeText)
    {
        Assert.Equal(TemplateScope.Table, TemplateScopeParser.Parse(scopeText, "entity.java.scriban"));
    }

    /// <summary>
    /// scope 合法三值（大小写不敏感）应解析为对应枚举。
    /// </summary>
    [Theory]
    [InlineData("table", TemplateScope.Table)]
    [InlineData("module", TemplateScope.Module)]
    [InlineData("project", TemplateScope.Project)]
    [InlineData("MODULE", TemplateScope.Module)]
    [InlineData(" Project ", TemplateScope.Project)]
    public void Parse_KnownValues_ReturnsMatchingScope(string scopeText, TemplateScope expected)
    {
        Assert.Equal(expected, TemplateScopeParser.Parse(scopeText, "entity.java.scriban"));
    }

    /// <summary>
    /// scope 非法枚举值应在包加载阶段结构化拒绝，错误消息列出合法取值并定位模板文件。
    /// </summary>
    [Fact]
    public async Task Load_InvalidScope_ThrowsWithLegalValues()
    {
        (string packageDir, string tempRoot) = CreatePackageDirectory();
        _tempRoots.Add(tempRoot);
        File.WriteAllText(Path.Combine(packageDir, "entity.java.scriban"), "public class {{table.className}} {}\n");
        File.WriteAllText(
            Path.Combine(packageDir, TemplatePackageLoader.ManifestFileName),
            """
            {
              "name": "scope-pkg",
              "description": "scope test",
              "engine": "scriban",
              "files": [
                { "template": "entity.java.scriban", "output": "out/{{table.className}}.java", "scope": "solution" }
              ]
            }
            """);

        TemplatePackageException exception = await Assert.ThrowsAsync<TemplatePackageException>(
            () => TemplatePackageLoader.LoadFromDirectoryAsync(packageDir, isBuiltin: false, CancellationToken.None));

        Assert.Contains("scope", exception.Message);
        Assert.Contains("solution", exception.Message);
        Assert.Contains("table / module / project", exception.Message);
    }

    /// <summary>
    /// 无 scope 字段的既有包加载后各文件作用域应为缺省 table，运行时信息其余字段不变。
    /// </summary>
    [Fact]
    public async Task Load_ManifestWithoutScope_AllFilesDefaultTable()
    {
        (string packageDir, string tempRoot) = CreatePackageDirectory();
        _tempRoots.Add(tempRoot);
        File.WriteAllText(Path.Combine(packageDir, "entity.java.scriban"), "public class {{table.className}} {}\n");
        File.WriteAllText(Path.Combine(packageDir, "app-context.cs.scriban"), "context {{vars.appName}}\n");
        File.WriteAllText(
            Path.Combine(packageDir, TemplatePackageLoader.ManifestFileName),
            """
            {
              "name": "scope-pkg",
              "description": "scope test",
              "engine": "scriban",
              "files": [
                { "template": "entity.java.scriban", "output": "out/{{table.className}}.java" },
                { "template": "app-context.cs.scriban", "output": "Data/AppDbContext.cs", "enabled": true }
              ]
            }
            """);

        TemplatePackageInfo package = await TemplatePackageLoader.LoadFromDirectoryAsync(packageDir, isBuiltin: false, CancellationToken.None);

        Assert.Equal(2, package.Files.Count);
        Assert.All(package.Files, file => Assert.Equal(TemplateScope.Table, file.Scope));
    }

    /// <summary>
    /// scope 三值混声明的包加载后各文件作用域应逐条对应。
    /// </summary>
    [Fact]
    public async Task Load_MixedScopes_ParsesPerFile()
    {
        (string packageDir, string tempRoot) = CreatePackageDirectory();
        _tempRoots.Add(tempRoot);
        File.WriteAllText(Path.Combine(packageDir, "entity.java.scriban"), "public class {{table.className}} {}\n");
        File.WriteAllText(Path.Combine(packageDir, "module-info.cs.scriban"), "module {{module.name}}\n");
        File.WriteAllText(Path.Combine(packageDir, "app-context.cs.scriban"), "context {{vars.appName}}\n");
        File.WriteAllText(
            Path.Combine(packageDir, TemplatePackageLoader.ManifestFileName),
            """
            {
              "name": "scope-pkg",
              "description": "scope test",
              "engine": "scriban",
              "files": [
                { "template": "entity.java.scriban", "output": "out/{{table.className}}.java" },
                { "template": "module-info.cs.scriban", "output": "Modules/{{module.name}}/Info.cs", "scope": "module" },
                { "template": "app-context.cs.scriban", "output": "Data/AppDbContext.cs", "scope": "project" }
              ]
            }
            """);

        TemplatePackageInfo package = await TemplatePackageLoader.LoadFromDirectoryAsync(packageDir, isBuiltin: false, CancellationToken.None);

        Assert.Equal(TemplateScope.Table, package.Files[0].Scope);
        Assert.Equal(TemplateScope.Module, package.Files[1].Scope);
        Assert.Equal(TemplateScope.Project, package.Files[2].Scope);
    }

    /// <summary>
    /// 在临时目录创建最小合法包目录结构。
    /// </summary>
    /// <returns>包目录与临时根目录。</returns>
    private static (string PackageDir, string TempRoot) CreatePackageDirectory()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
        string packageDir = Path.Combine(tempRoot, "scope-pkg");
        Directory.CreateDirectory(packageDir);
        return (packageDir, tempRoot);
    }
}
