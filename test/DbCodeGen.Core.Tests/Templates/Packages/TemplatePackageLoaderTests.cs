using System.Runtime.Versioning;
using System.Text.Json;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;
using Scriban;
using Scriban.Runtime;
using Scriban.Syntax;

namespace DbCodeGen.Core.Tests.Templates.Packages;

/// <summary>
/// TemplatePackageLoader 加载与校验单元测试，覆盖合法加载、缺失清单、非法引擎、目录穿越、包名校验与内置包渲染冒烟。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TemplatePackageLoaderTests : IDisposable
{
    private readonly string _tempRoot;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// 为每个测试实例创建独立临时目录，避免用例间包目录互相污染。
    /// </summary>
    public TemplatePackageLoaderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// 递归删除测试临时目录。
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// 在指定目录下创建含一个模板文件的合法测试包。
    /// </summary>
    /// <param name="rootDir">包根目录。</param>
    /// <param name="packageName">包名。</param>
    /// <param name="includeFile">是否写入模板文件。</param>
    /// <param name="engine">引擎名，默认 scriban。</param>
    /// <param name="templatePath">模板相对路径。</param>
    /// <param name="outputPath">输出相对路径。</param>
    /// <returns>包目录绝对路径。</returns>
    private static async Task<string> CreatePackageAsync(
        string rootDir,
        string packageName,
        bool includeFile = true,
        string? engine = null,
        string? templatePath = null,
        string? outputPath = null)
    {
        string packageDir = Path.Combine(rootDir, packageName);
        Directory.CreateDirectory(packageDir);

        string templateFile = templatePath ?? "entity.java.scriban";
        var manifest = new TemplateManifest
        {
            Name = packageName,
            Description = "测试包",
            Engine = engine ?? "scriban",
            BasePackage = "com.example",
            TypeMap = new Dictionary<string, string> { ["bigint"] = "Long" },
            Files = new List<TemplateFileEntry>
            {
                new()
                {
                    Template = templateFile,
                    Output = outputPath ?? "{{package.dir}}/entity/{{table.className}}.java",
                    Enabled = true
                }
            }
        };

        await File.WriteAllTextAsync(
            Path.Combine(packageDir, TemplatePackageLoader.ManifestFileName),
            JsonSerializer.Serialize(manifest, JsonOptions));

        if (includeFile)
        {
            string filePath = Path.Combine(packageDir, templateFile);
            string? fileDirectory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(fileDirectory))
            {
                Directory.CreateDirectory(fileDirectory);
            }

            await File.WriteAllTextAsync(filePath, "public class {{table.className}} { }");
        }

        return packageDir;
    }

    /// <summary>
    /// 合法包应加载成功，返回的包信息字段与 manifest 一致。
    /// </summary>
    [Fact]
    public async Task LoadFromDirectoryAsync_ValidPackage_ReturnsInfo()
    {
        string packageDir = await CreatePackageAsync(_tempRoot, "sys-user");

        TemplatePackageInfo package = await TemplatePackageLoader.LoadFromDirectoryAsync(packageDir, isBuiltin: false, CancellationToken.None);

        Assert.Equal("sys-user", package.Name);
        Assert.Equal("测试包", package.Description);
        Assert.Equal("scriban", package.Engine);
        Assert.Equal("com.example", package.BasePackage);
        Assert.False(package.IsBuiltin);
        Assert.Equal(Path.GetFullPath(packageDir), package.RootPath);
        Assert.True(File.Exists(package.ManifestPath));
        TemplateFileInfo file = Assert.Single(package.Files);
        Assert.Equal("entity.java.scriban", file.RelativeTemplatePath);
        Assert.True(file.IsEnabled);
        Assert.Equal("{{package.dir}}/entity/{{table.className}}.java", file.OutputPath);
        Assert.True(File.Exists(file.TemplatePath));
        Assert.Equal("Long", package.TypeMap["bigint"]);
    }

    /// <summary>
    /// 缺少 template.json 的目录应抛模板包异常。
    /// </summary>
    [Fact]
    public async Task LoadFromDirectoryAsync_MissingManifest_Throws()
    {
        string emptyDir = Path.Combine(_tempRoot, "empty");
        Directory.CreateDirectory(emptyDir);

        TemplatePackageException exception = await Assert.ThrowsAsync<TemplatePackageException>(
            () => TemplatePackageLoader.LoadFromDirectoryAsync(emptyDir, false, CancellationToken.None));

        Assert.Contains("template.json", exception.Message);
    }

    /// <summary>
    /// 不存在的根目录应抛模板包异常。
    /// </summary>
    [Fact]
    public async Task LoadFromDirectoryAsync_NotExists_Throws()
    {
        string missing = Path.Combine(_tempRoot, "missing");

        await Assert.ThrowsAsync<TemplatePackageException>(
            () => TemplatePackageLoader.LoadFromDirectoryAsync(missing, false, CancellationToken.None));
    }

    /// <summary>
    /// 非 scriban 引擎应被拒绝。
    /// </summary>
    [Fact]
    public async Task LoadFromDirectoryAsync_UnsupportedEngine_Throws()
    {
        string packageDir = await CreatePackageAsync(_tempRoot, "velocity-pkg", engine: "velocity");

        TemplatePackageException exception = await Assert.ThrowsAsync<TemplatePackageException>(
            () => TemplatePackageLoader.LoadFromDirectoryAsync(packageDir, false, CancellationToken.None));

        Assert.Contains("不支持的模板引擎", exception.Message);
    }

    /// <summary>
    /// manifest 引用的模板文件缺失应被拒绝。
    /// </summary>
    [Fact]
    public async Task LoadFromDirectoryAsync_MissingTemplateFile_Throws()
    {
        string packageDir = await CreatePackageAsync(_tempRoot, "missing-file", includeFile: false);

        TemplatePackageException exception = await Assert.ThrowsAsync<TemplatePackageException>(
            () => TemplatePackageLoader.LoadFromDirectoryAsync(packageDir, false, CancellationToken.None));

        Assert.Contains("模板文件不存在", exception.Message);
    }

    /// <summary>
    /// 模板路径含 .. 段应被拒绝（目录穿越）。
    /// </summary>
    [Fact]
    public async Task LoadFromDirectoryAsync_TemplateTraversal_Throws()
    {
        string packageDir = await CreatePackageAsync(_tempRoot, "slip", templatePath: "../evil.java.scriban");

        TemplatePackageException exception = await Assert.ThrowsAsync<TemplatePackageException>(
            () => TemplatePackageLoader.LoadFromDirectoryAsync(packageDir, false, CancellationToken.None));

        Assert.Contains("..", exception.Message);
    }

    /// <summary>
    /// 输出路径静态骨架含 .. 段应被拒绝（目录穿越）。
    /// </summary>
    [Fact]
    public async Task LoadFromDirectoryAsync_OutputTraversal_Throws()
    {
        string packageDir = await CreatePackageAsync(_tempRoot, "output-slip", outputPath: "{{package.dir}}/../evil.java");

        TemplatePackageException exception = await Assert.ThrowsAsync<TemplatePackageException>(
            () => TemplatePackageLoader.LoadFromDirectoryAsync(packageDir, false, CancellationToken.None));

        Assert.Contains("输出路径不合法", exception.Message);
    }

    /// <summary>
    /// 清单缺省 enabled 的模板文件应默认勾选参与生成。
    /// </summary>
    [Fact]
    public async Task LoadFromDirectoryAsync_EnabledDefaultTrueWhenOmitted()
    {
        string packageDir = Path.Combine(_tempRoot, "default-enabled");
        Directory.CreateDirectory(packageDir);

        // 手写清单 JSON，省略 files[].enabled 字段
        await File.WriteAllTextAsync(Path.Combine(packageDir, TemplatePackageLoader.ManifestFileName),
            """{"name":"default-enabled","description":"缺省勾选","engine":"scriban","files":[{"template":"t.java.scriban","output":"out/{{table.className}}.java"}]}""");
        await File.WriteAllTextAsync(Path.Combine(packageDir, "t.java.scriban"), "content");

        TemplatePackageInfo package = await TemplatePackageLoader.LoadFromDirectoryAsync(packageDir, false, CancellationToken.None);

        TemplateFileInfo file = Assert.Single(package.Files);
        Assert.True(file.IsEnabled);
    }

    /// <summary>
    /// 清单 files 含空条目应判为非法，不产生空引用崩溃。
    /// </summary>
    [Fact]
    public async Task LoadFromDirectoryAsync_NullFileEntry_Throws()
    {
        string packageDir = Path.Combine(_tempRoot, "null-entry");
        Directory.CreateDirectory(packageDir);
        await File.WriteAllTextAsync(Path.Combine(packageDir, TemplatePackageLoader.ManifestFileName),
            """{"name":"null-entry","engine":"scriban","files":[null]}""");

        TemplatePackageException exception = await Assert.ThrowsAsync<TemplatePackageException>(
            () => TemplatePackageLoader.LoadFromDirectoryAsync(packageDir, false, CancellationToken.None));

        Assert.Contains("空条目", exception.Message);
    }

    /// <summary>
    /// 清单 files 为空数组或缺省时应加载通过，返回的 Files 为空（空模板包合法）。
    /// </summary>
    [Theory]
    [InlineData("""{"name":"empty-array","engine":"scriban","files":[]}""", "empty-array")]
    [InlineData("""{"name":"no-files","engine":"scriban"}""", "no-files")]
    public async Task LoadFromDirectoryAsync_EmptyFilesList_LoadsWithNoFiles(string manifestJson, string expectedName)
    {
        string packageDir = Path.Combine(_tempRoot, expectedName);
        Directory.CreateDirectory(packageDir);
        await File.WriteAllTextAsync(Path.Combine(packageDir, TemplatePackageLoader.ManifestFileName), manifestJson);

        TemplatePackageInfo package = await TemplatePackageLoader.LoadFromDirectoryAsync(packageDir, false, CancellationToken.None);

        Assert.Equal(expectedName, package.Name);
        Assert.Empty(package.Files);
        Assert.Equal(Path.GetFullPath(packageDir), package.RootPath);
    }

    /// <summary>
    /// 包名合法用例应全部通过。
    /// </summary>
    [Theory]
    [InlineData("java-mybatis-plus")]
    [InlineData("my_pkg")]
    [InlineData("User")]
    [InlineData("pkg1")]
    [InlineData("-leading-dash")]
    public void IsValidPackageName_ValidNames_ReturnTrue(string name)
    {
        Assert.True(TemplatePackageLoader.IsValidPackageName(name));
    }

    /// <summary>
    /// 包名非法用例应全部被拒绝。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a b")]
    [InlineData("a:b")]
    [InlineData("-")]
    [InlineData("_")]
    [InlineData("C:evil")]
    public void IsValidPackageName_InvalidNames_ReturnFalse(string? name)
    {
        Assert.False(TemplatePackageLoader.IsValidPackageName(name));
    }

    /// <summary>
    /// 内置包 java-mybatis-plus 应从输出目录加载成功，共 6 个模板文件且 controller 默认不参与生成。
    /// </summary>
    [Fact]
    public async Task BuiltinPackage_LoadsFromOutputDirectory()
    {
        string builtinRoot = BuiltinTemplatePackages.GetDefaultRootPath();
        string builtinDir = Path.Combine(builtinRoot, "java-mybatis-plus");
        Assert.True(Directory.Exists(builtinDir), $"内置包未复制到输出目录：{builtinDir}");

        TemplatePackageInfo package = await TemplatePackageLoader.LoadFromDirectoryAsync(builtinDir, isBuiltin: true, CancellationToken.None);

        Assert.Equal("java-mybatis-plus", package.Name);
        Assert.Equal("scriban", package.Engine);
        Assert.True(package.IsBuiltin);
        Assert.Equal(6, package.Files.Count);
        TemplateFileInfo controller = package.Files.Single(file => file.RelativeTemplatePath == "controller.java.scriban");
        Assert.False(controller.IsEnabled);
        TemplateFileInfo entity = package.Files.Single(file => file.RelativeTemplatePath == "entity.java.scriban");
        Assert.True(entity.IsEnabled);
    }

    /// <summary>
    /// 类型映射应按 typeMap 命中返回目标类型。
    /// </summary>
    [Fact]
    public void MapType_KnownType_ReturnsMappedType()
    {
        var typeMap = new Dictionary<string, string> { ["bigint"] = "Long", ["varchar"] = "String" };

        Assert.Equal("Long", TypeMapper.MapType("bigint", typeMap));
        Assert.Equal("String", TypeMapper.MapType("varchar", typeMap));
    }

    /// <summary>
    /// 类型映射应大小写不敏感，并去除长度与无符号修饰。
    /// </summary>
    [Theory]
    [InlineData("VARCHAR", "String")]
    [InlineData("varchar(255)", "String")]
    [InlineData("BIGINT UNSIGNED", "Long")]
    [InlineData("BigInt", "Long")]
    public void MapType_WithQualifiersAndCase_ReturnsMappedType(string rawDbType, string expected)
    {
        var typeMap = new Dictionary<string, string> { ["bigint"] = "Long", ["varchar"] = "String" };

        Assert.Equal(expected, TypeMapper.MapType(rawDbType, typeMap));
    }

    /// <summary>
    /// 类型映射未命中或输入为空时应返回默认类型。
    /// </summary>
    [Fact]
    public void MapType_UnknownOrEmpty_ReturnsFallback()
    {
        var typeMap = new Dictionary<string, string> { ["bigint"] = "Long" };

        Assert.Equal("String", TypeMapper.MapType("jsonb", typeMap));
        Assert.Equal("String", TypeMapper.MapType(string.Empty, typeMap));
        Assert.Equal("String", TypeMapper.MapType(null, typeMap));
        Assert.Equal("String", TypeMapper.MapType("bigint", null));
    }

    /// <summary>
    /// 内置包 6 个 Scriban 模板应全部可解析并渲染成功，输出包含类名。
    /// </summary>
    [Fact]
    public async Task BuiltinPackage_AllTemplatesRenderWithSampleContext()
    {
        string builtinRoot = BuiltinTemplatePackages.GetDefaultRootPath();
        string builtinDir = Path.Combine(builtinRoot, "java-mybatis-plus");
        Assert.True(Directory.Exists(builtinDir), $"内置包未复制到输出目录：{builtinDir}");

        TemplatePackageInfo package = await TemplatePackageLoader.LoadFromDirectoryAsync(builtinDir, isBuiltin: true, CancellationToken.None);
        ScriptObject context = BuildSampleRenderContext();

        foreach (TemplateFileInfo file in package.Files)
        {
            string templateText = await File.ReadAllTextAsync(file.TemplatePath);
            Template template = Scriban.Template.Parse(templateText);
            Assert.False(template.HasErrors, $"模板 {file.RelativeTemplatePath} 解析失败：{template.Messages}");

            string output = await template.RenderAsync(context);
            Assert.Contains("SysUser", output);
        }
    }

    /// <summary>
    /// 构造与 04 §七 渲染上下文对齐的样例脚本对象，供内置包模板渲染冒烟测试使用。
    /// </summary>
    /// <returns>包含 table/package/tool 节点的根脚本对象。</returns>
    private static ScriptObject BuildSampleRenderContext()
    {
        var table = new ScriptObject();
        table.SetValue("className", "SysUser", true);
        table.SetValue("variableName", "sysUser", true);
        table.SetValue("rawName", "sys_user", true);
        table.SetValue("comment", "系统用户", true);

        var id = new ScriptObject();
        id.SetValue("propertyName", "id", true);
        id.SetValue("rawName", "id", true);
        id.SetValue("comment", "主键ID", true);
        id.SetValue("rawDbType", "bigint", true);
        id.SetValue("isPrimaryKey", true, true);

        var userName = new ScriptObject();
        userName.SetValue("propertyName", "userName", true);
        userName.SetValue("rawName", "user_name", true);
        userName.SetValue("comment", "用户名", true);
        userName.SetValue("rawDbType", "varchar", true);
        userName.SetValue("isPrimaryKey", false, true);

        var createdTime = new ScriptObject();
        createdTime.SetValue("propertyName", "createdTime", true);
        createdTime.SetValue("rawName", "created_time", true);
        createdTime.SetValue("comment", "创建时间", true);
        createdTime.SetValue("rawDbType", "datetime", true);
        createdTime.SetValue("isPrimaryKey", false, true);

        table.SetValue("primaryKeys", new ScriptArray { id }, true);
        table.SetValue("fullColumn", new ScriptArray { id, userName, createdTime }, true);
        table.SetValue("otherColumn", new ScriptArray { userName, createdTime }, true);

        var package = new ScriptObject();
        package.SetValue("name", "java-mybatis-plus", true);
        package.SetValue("basePackage", "com.example", true);

        var tool = new ScriptObject();
        tool.SetValue("type", new ScriptDelegateFunction(args => TypeMapper.MapType(args.FirstOrDefault()?.ToString(), new Dictionary<string, string>
        {
            ["bigint"] = "Long",
            ["varchar"] = "String",
            ["datetime"] = "LocalDateTime"
        })), true);
        tool.SetValue("firstLowerCase", new ScriptDelegateFunction(args => FirstCase(args.FirstOrDefault()?.ToString(), lower: true)), true);
        tool.SetValue("firstUpperCase", new ScriptDelegateFunction(args => FirstCase(args.FirstOrDefault()?.ToString(), lower: false)), true);
        tool.SetValue("hump2Underline", new ScriptDelegateFunction(args => args.FirstOrDefault()?.ToString() ?? string.Empty), true);
        tool.SetValue("hump3Underline", new ScriptDelegateFunction(args => args.FirstOrDefault()?.ToString() ?? string.Empty), true);

        // 样例上下文未注入全局映射服务，导包函数返回空串即可保证内置包模板可渲染
        tool.SetValue("typeImport", new ScriptDelegateFunction(args => string.Empty), true);
        tool.SetValue("imports", new ScriptDelegateFunction(args => string.Empty), true);

        var root = new ScriptObject();
        root.SetValue("table", table, true);
        root.SetValue("package", package, true);
        root.SetValue("tool", tool, true);
        return root;
    }

    /// <summary>
    /// 将字符串首字母转换为大写或小写，空串原样返回。
    /// </summary>
    private static string FirstCase(string? value, bool lower)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        return lower ? char.ToLowerInvariant(value[0]) + value[1..] : char.ToUpperInvariant(value[0]) + value[1..];
    }

    /// <summary>
    /// Scriban 脚本函数包装器：实现 IScriptCustomFunction，将任意参数数组委托包装为可被模板调用的函数。
    /// 本工程 Scriban 7.2.6 不直接接受 Func 委托，须经本接口包装后方可在模板中调用。
    /// </summary>
    private sealed class ScriptDelegateFunction : IScriptCustomFunction
    {
        private readonly Func<object?[], object?> _implementation;

        /// <summary>
        /// 使用参数数组委托创建脚本函数包装器。
        /// </summary>
        /// <param name="implementation">接收参数数组并返回结果的委托。</param>
        public ScriptDelegateFunction(Func<object?[], object?> implementation)
        {
            _implementation = implementation;
        }

        /// <inheritdoc />
        public int RequiredParameterCount => 0;

        /// <inheritdoc />
        public int ParameterCount => 0;

        /// <inheritdoc />
        public ScriptVarParamKind VarParamKind => ScriptVarParamKind.Direct;

        /// <inheritdoc />
        public Type ReturnType => typeof(object);

        /// <inheritdoc />
        public ScriptParameterInfo GetParameterInfo(int index)
        {
            return new ScriptParameterInfo(typeof(object), $"arg{index}");
        }

        /// <inheritdoc />
        public object? Invoke(TemplateContext context, ScriptNode? callerContext, ScriptArray arguments, ScriptBlockStatement? blockStatement)
        {
            return _implementation(arguments.ToArray());
        }

        /// <inheritdoc />
        public ValueTask<object?> InvokeAsync(TemplateContext context, ScriptNode? callerContext, ScriptArray arguments, ScriptBlockStatement? blockStatement)
        {
            return new ValueTask<object?>(_implementation(arguments.ToArray()));
        }
    }
}
