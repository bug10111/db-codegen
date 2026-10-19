using DbCodeGen.Core.Templates.Packages;

namespace DbCodeGen.Core.Tests.Templates.Packages;

/// <summary>
/// 模板包能力元数据单元测试：新字段加载与推断（role/stack/scopes/hasBackendView/hasWebPage/contractPath/
/// deployModes/platformTables/excludeTables/naming/files[].root/role），旧清单零字段向后兼容，
/// 非法取值（role/root/packageSegmentCase/正则）结构化拒绝。
/// </summary>
public sealed class TemplateCapabilityManifestTests : IDisposable
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
    /// 旧清单（无任何新字段）照常加载，能力元数据按产物推断：role=backend、stack=java、scopes=[table]、
    /// hasBackendView=true（view 路径）、hasWebPage=false、contractPath=README.md.scriban。
    /// </summary>
    [Fact]
    public async Task LoadFromDirectoryAsync_LegacyManifest_InfersCapabilities()
    {
        string packageRoot = await WritePackageAsync(
            """
            {
              "name": "legacy-pkg",
              "description": "旧清单",
              "engine": "scriban",
              "basePackage": "com.example",
              "typeMap": {},
              "files": [
                { "template": "view.java.scriban", "output": "src/main/java/{{package.dir}}/view/{{table.className}}View.java", "enabled": true },
                { "template": "entity.java.scriban", "output": "src/main/java/{{package.dir}}/entity/{{table.className}}.java", "enabled": true }
              ]
            }
            """,
            ("view.java.scriban", "class {{table.className}}View {}\n"),
            ("entity.java.scriban", "class {{table.className}} {}\n"));

        TemplatePackageInfo package = await TemplatePackageLoader.LoadFromDirectoryAsync(packageRoot, isBuiltin: false, CancellationToken.None);

        Assert.Equal("backend", package.Role);
        Assert.Equal("java", package.Stack);
        Assert.Equal(new[] { "table" }, package.Scopes);
        Assert.True(package.HasBackendView);
        Assert.False(package.HasWebPage);
        Assert.Equal("README.md.scriban", package.ContractPath);
        Assert.Empty(package.DeployModes);
        Assert.True(package.PlatformTables.IsEmpty);
        Assert.True(package.ExcludeTables.IsEmpty);
        Assert.All(package.Files, file => Assert.Null(file.OutputRoot));
        Assert.Equal(TemplateFileRoleResolver.BackendView, package.Files[0].Role);
    }

    /// <summary>
    /// 新字段完整加载：role/stack/scopes/hasWebPage/contractPath/deployModes/platformTables/excludeTables/naming，
    /// 以及 files[].root/role 与作用域去重派生。
    /// </summary>
    [Fact]
    public async Task LoadFromDirectoryAsync_NewFields_LoadedAndNormalized()
    {
        string packageRoot = await WritePackageAsync(
            """
            {
              "name": "full-pkg",
              "description": "全字段清单",
              "engine": "scriban",
              "basePackage": "com.simple.xiaoguan.common",
              "files": [
                { "template": "entity.java.scriban", "output": "src/main/java/{{tool.packagePath(package.naming.layerPrefix, table.variableName)}}/{{table.className}}.java", "enabled": true, "role": "backendEntity" },
                { "template": "api.ts.scriban", "output": "api/{{table.variableName}}.ts", "enabled": true, "root": "web", "role": "webApi" },
                { "template": "pom.xml.scriban", "output": "pom.xml", "enabled": true, "scope": "project", "root": "workspace" },
                { "template": "module.ts.scriban", "output": "{{module.name}}/info.ts", "enabled": true, "scope": "module" }
              ],
              "role": "fullstack",
              "stack": "mixed",
              "hasBackendView": false,
              "hasWebPage": true,
              "contractPath": "docs/contract.md.scriban",
              "deployModes": ["standalone", "cloud"],
              "permissionModule": "usercenter",
              "platformTables": { "exact": ["sys_config"], "prefixes": ["sys_"], "regex": ["^qrtz_"] },
              "excludeTables": { "exact": ["t_temp"] },
              "naming": {
                "stripTablePrefix": true,
                "tablePrefixes": ["t_", "sys_"],
                "singularize": true,
                "entitySuffix": "Entity",
                "packageSegmentCase": "lower",
                "layerPrefix": "common"
              }
            }
            """,
            ("entity.java.scriban", "class {{table.className}} {}\n"),
            ("api.ts.scriban", "export const api = 1;\n"),
            ("pom.xml.scriban", "<project/>\n"),
            ("module.ts.scriban", "export const m = 1;\n"));

        TemplatePackageInfo package = await TemplatePackageLoader.LoadFromDirectoryAsync(packageRoot, isBuiltin: false, CancellationToken.None);

        Assert.Equal("fullstack", package.Role);
        Assert.Equal("mixed", package.Stack);
        Assert.Equal(new[] { "table", "module", "project" }, package.Scopes);
        Assert.False(package.HasBackendView);
        Assert.True(package.HasWebPage);
        Assert.Equal("docs/contract.md.scriban", package.ContractPath);
        Assert.Equal(new[] { "standalone", "cloud" }, package.DeployModes);
        Assert.Equal("usercenter", package.PermissionModule);
        Assert.Equal("sys_config", Assert.Single(package.PlatformTables.Exact));
        Assert.Equal("^qrtz_", Assert.Single(package.PlatformTables.Regex));
        Assert.Equal("t_temp", Assert.Single(package.ExcludeTables.Exact));

        Assert.NotNull(package.Naming);
        Assert.True(package.Naming!.StripTablePrefix);
        Assert.Equal(new[] { "t_", "sys_" }, package.Naming.TablePrefixes);
        Assert.True(package.Naming.Singularize);
        Assert.Equal("Entity", package.Naming.EntitySuffix);
        Assert.Equal("lower", package.Naming.PackageSegmentCase);
        Assert.Equal("common", package.Naming.LayerPrefix);

        Assert.Equal(TemplateOutputRoot.Web, package.Files[1].OutputRoot);
        Assert.Equal("webApi", package.Files[1].Role);
        Assert.Equal(TemplateOutputRoot.Workspace, package.Files[2].OutputRoot);
        Assert.Equal(TemplateScope.Project, package.Files[2].Scope);
        Assert.Null(package.Files[3].OutputRoot);
        Assert.Equal(TemplateFileRoleResolver.WebOther, package.Files[3].Role);
    }

    /// <summary>
    /// 非法 role / root / packageSegmentCase / 正则模式在包加载阶段结构化拒绝。
    /// </summary>
    [Theory]
    [InlineData("{\"role\":\"unknown\"}")]
    [InlineData("{\"stack\":\"rust\"}")]
    [InlineData("{\"files\":[{\"template\":\"x.scriban\",\"output\":\"x\",\"root\":\"../escape\",\"enabled\":true}]}")]
    [InlineData("{\"files\":[{\"template\":\"x.scriban\",\"output\":\"../x\",\"root\":\"workspace\",\"enabled\":true}]}")]
    [InlineData("{\"files\":[{\"template\":\"x.scriban\",\"output\":\"web/../x.ts\",\"root\":\"web\",\"enabled\":true}]}")]
    [InlineData("{\"naming\":{\"packageSegmentCase\":\"upper\"}}")]
    [InlineData("{\"platformTables\":{\"regex\":[\"[unclosed\"]}}")]
    public async Task LoadFromDirectoryAsync_InvalidCapabilityValue_Rejected(string extraJson)
    {
        string packageRoot = await WritePackageRawAsync(
            $$"""
            {
              "name": "bad-pkg",
              "description": "非法取值",
              "engine": "scriban",
              {{extraJson.TrimStart('{').TrimEnd('}')}}
            }
            """,
            ("x.scriban", "x\n"));

        await Assert.ThrowsAsync<TemplatePackageException>(
            () => TemplatePackageLoader.LoadFromDirectoryAsync(packageRoot, isBuiltin: false, CancellationToken.None));
    }

    /// <summary>
    /// 在临时目录写入清单与模板文件（清单经模型序列化，保证 camelCase 契约）。
    /// </summary>
    /// <param name="manifestJson">清单 JSON 文本。</param>
    /// <param name="files">模板相对路径与正文。</param>
    /// <returns>包根目录。</returns>
    private async Task<string> WritePackageRawAsync(string manifestJson, params (string Template, string Content)[] files)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
        _tempRoots.Add(tempRoot);
        string packageRoot = Path.Combine(tempRoot, "pkg");
        Directory.CreateDirectory(packageRoot);
        await File.WriteAllTextAsync(Path.Combine(packageRoot, TemplatePackageLoader.ManifestFileName), manifestJson);
        foreach ((string template, string content) in files)
        {
            string fullPath = Path.Combine(packageRoot, template);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(fullPath, content);
        }

        return packageRoot;
    }

    /// <summary>
    /// 在临时目录写入清单与模板文件。
    /// </summary>
    /// <param name="manifestJson">清单 JSON 文本。</param>
    /// <param name="files">模板相对路径与正文。</param>
    /// <returns>包根目录。</returns>
    private Task<string> WritePackageAsync(string manifestJson, params (string Template, string Content)[] files)
    {
        return WritePackageRawAsync(manifestJson, files);
    }
}
