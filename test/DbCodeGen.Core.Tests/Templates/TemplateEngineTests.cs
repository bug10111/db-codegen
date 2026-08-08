using DbCodeGen.Core.Config;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbCodeGen.Core.Tests.Templates;

/// <summary>
/// 共享模板引擎单元测试，覆盖内容渲染上下文、tool 函数集、类型映射、结构化错误与路径占位渲染。
/// </summary>
public sealed class TemplateEngineTests
{
    private readonly TemplateEngine _engine = new();

    /// <summary>
    /// 内容渲染应注入 table 上下文，类名占位输出正确。
    /// </summary>
    [Fact]
    public void Render_TableContext_OutputsClassName()
    {
        PreviewResult result = _engine.Render("public class {{ table.className }} { }", CreateTable(), null, CreatePackageContext(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains("public class SysUser { }", result.Output);
    }

    /// <summary>
    /// 内容渲染应支持全量列遍历，for 循环元数据 last 判定输出逗号分隔。
    /// </summary>
    [Fact]
    public void Render_FullColumnLoop_JoinsWithComma()
    {
        string template = "{{ for column in table.fullColumn }}{{ column.rawName }}{{ if for.last == false }}, {{ end }}{{ end }}";

        PreviewResult result = _engine.Render(template, CreateTable(), null, CreatePackageContext(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("id, user_name, created_time", result.Output);
    }

    /// <summary>
    /// 内容渲染应支持主键列遍历与列字段访问。
    /// </summary>
    [Fact]
    public void Render_PrimaryKeysLoop_OutputsPropertyNames()
    {
        string template = "{{ for column in table.primaryKeys }}{{ column.propertyName }}({{ column.rawDbType }}){{ end }}";

        PreviewResult result = _engine.Render(template, CreateTable(), null, CreatePackageContext(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("id(bigint)", result.Output);
    }

    /// <summary>
    /// 内容渲染应注册 tool 函数集，首字母大小写与驼峰转下划线输出正确。
    /// </summary>
    [Fact]
    public void Render_ToolFunctions_OutputsTransformedValues()
    {
        string template = "{{ tool.firstLowerCase(table.className) }}|{{ tool.firstUpperCase(table.className) }}|{{ tool.hump2Underline(table.className) }}|{{ tool.hump3Underline(table.className) }}";

        PreviewResult result = _engine.Render(template, CreateTable(), null, CreatePackageContext(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("sysUser|SysUser|sys_user|SYS_USER", result.Output);
    }

    /// <summary>
    /// tool.type 应按当前包 manifest 类型映射表实时计算映射结果。
    /// </summary>
    [Fact]
    public void Render_ToolType_MapByCurrentPackage()
    {
        PreviewResult result = _engine.Render("{{ tool.type('bigint') }}|{{ tool.type('varchar') }}|{{ tool.type('jsonb') }}", CreateTable(), null, CreatePackageContext(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Long|String|String", result.Output);
    }

    /// <summary>
    /// tool.type 应按列原始类型映射，且不同模板包 typeMap 各自计算。
    /// </summary>
    [Fact]
    public void Render_ToolTypeWithColumn_RespectsPackageTypeMap()
    {
        var column = new ColumnInfo { RawName = "id", PropertyName = "id", RawDbType = "bigint" };
        var customPackage = new TemplatePackageInfo
        {
            Name = "custom",
            BasePackage = "com.custom",
            TypeMap = new Dictionary<string, string> { ["bigint"] = "java.lang.Long" }
        };

        PreviewResult result = _engine.Render("{{ tool.type(column.rawDbType) }}", CreateTable(), column, TemplatePackageContext.From(customPackage), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("java.lang.Long", result.Output);
    }

    /// <summary>
    /// 注入全局映射服务时 tool.typeImport 返回映射条目声明的导包，tool.imports 对列集合去重后生成导包块。
    /// </summary>
    [Fact]
    public void Render_ToolTypeImportAndImports_WithGlobalMappingService()
    {
        var config = new StubConfigService
        {
            Current = new AppConfig
            {
                TypeMappings = new List<TypeMappingEntry>
                {
                    new() { DbType = "datetime", TargetType = "LocalDateTime", Import = "java.time.LocalDateTime" },
                    new() { DbType = "varchar", TargetType = "String" }
                }
            }
        };
        var engine = new TemplateEngine(new TypeMappingService(config, NullLogger<TypeMappingService>.Instance));

        // 样例表列含 bigint/varchar/datetime：bigint 走包 typeMap 无导包，varchar 无导包，datetime 有导包
        PreviewResult result = engine.Render("{{ tool.typeImport('datetime') }}|{{ tool.imports(table.fullColumn) }}", CreateTable(), null, CreatePackageContext(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.StartsWith("java.time.LocalDateTime|", result.Output);
        Assert.Contains("import java.time.LocalDateTime;", result.Output);
        Assert.DoesNotContain("import java.lang.Long;", result.Output);
    }

    /// <summary>
    /// tool.type 应透传表所属数据库类型：MySQL 表命中 MySQL 专属条目，PG 表回落通用条目，互不串用。
    /// </summary>
    [Fact]
    public void Render_ToolType_UsesTableDatabaseType()
    {
        var config = new StubConfigService
        {
            Current = new AppConfig
            {
                TypeMappings = new List<TypeMappingEntry>
                {
                    new() { DbType = "int", TargetType = "Long" },
                    new() { DbType = "int", TargetType = "Integer", DatabaseType = DataSourceType.MySql }
                }
            }
        };
        var engine = new TemplateEngine(new TypeMappingService(config, NullLogger<TypeMappingService>.Instance));

        TableInfo mySqlTable = CreateTable();
        mySqlTable.DatabaseType = DataSourceType.MySql;
        TableInfo postgreSqlTable = CreateTable();
        postgreSqlTable.DatabaseType = DataSourceType.PostgreSql;

        PreviewResult mySql = engine.Render("{{ tool.type('int') }}", mySqlTable, null, CreatePackageContext(), CancellationToken.None);
        PreviewResult postgreSql = engine.Render("{{ tool.type('int') }}", postgreSqlTable, null, CreatePackageContext(), CancellationToken.None);

        Assert.Equal("Integer", mySql.Output);
        Assert.Equal("Long", postgreSql.Output);
    }

    /// <summary>
    /// 全局映射表应优先于内置包 typeMap：内置包声明 datetime→LocalDateTime，全局表声明 datetime→Date 时渲染结果为 Date。
    /// </summary>
    [Fact]
    public void Render_ToolType_GlobalMappingBeatsPackageTypeMap()
    {
        var config = new StubConfigService
        {
            Current = new AppConfig
            {
                TypeMappings = new List<TypeMappingEntry>
                {
                    new() { DbType = "datetime", TargetType = "Date", Import = "java.util.Date" }
                }
            }
        };
        var engine = new TemplateEngine(new TypeMappingService(config, NullLogger<TypeMappingService>.Instance));

        // CreatePackageContext 内置包 typeMap 含 datetime→LocalDateTime，全局表 datetime→Date 应优先命中
        PreviewResult result = engine.Render("{{ tool.type('datetime') }}", CreateTable(), null, CreatePackageContext(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Date", result.Output);
    }

    /// <summary>
    /// 传入 column 时模板内 column 变量应可用，未传入时应渲染为空。
    /// </summary>
    [Fact]
    public void Render_ColumnContext_OnlyWhenProvided()
    {
        var column = new ColumnInfo { RawName = "user_name", PropertyName = "userName", RawDbType = "varchar" };

        PreviewResult withColumn = _engine.Render("{{ column.propertyName }}", CreateTable(), column, CreatePackageContext(), CancellationToken.None);
        PreviewResult withoutColumn = _engine.Render("{{ column.propertyName }}", CreateTable(), null, CreatePackageContext(), CancellationToken.None);

        Assert.Equal("userName", withColumn.Output);
        Assert.Equal(string.Empty, withoutColumn.Output);
    }

    /// <summary>
    /// 内容渲染应注入 package 上下文，包名与基础包名输出正确。
    /// </summary>
    [Fact]
    public void Render_PackageContext_OutputsPackageFields()
    {
        PreviewResult result = _engine.Render("{{ package.name }}|{{ package.basePackage }}", CreateTable(), null, CreatePackageContext(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("java-mybatis-plus|com.example", result.Output);
    }

    /// <summary>
    /// 模板语法错误应结构化返回，携带模板名与行列定位。
    /// </summary>
    [Fact]
    public void Render_ParseError_ReturnsStructuredError()
    {
        PreviewResult result = _engine.Render("{{ if }}", CreateTable(), null, CreatePackageContext(), CancellationToken.None, "entity.java.scriban");

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorLine);
        Assert.NotNull(result.ErrorColumn);
        Assert.Equal(1, result.ErrorLine);
        Assert.Contains("entity.java.scriban", result.ErrorMessage);
        Assert.Contains("第 1 行", result.ErrorMessage);
    }

    /// <summary>
    /// 渲染运行期错误（调用未注册函数）应结构化返回，定位到错误所在行。
    /// </summary>
    [Fact]
    public void Render_RuntimeError_ReturnsStructuredErrorWithLine()
    {
        string template = "第一行\n{{ unknownFn(1) }}";

        PreviewResult result = _engine.Render(template, CreateTable(), null, CreatePackageContext(), CancellationToken.None, "mapper.xml.scriban");

        Assert.False(result.IsSuccess);
        Assert.Equal(2, result.ErrorLine);
        Assert.NotNull(result.ErrorColumn);
        Assert.Contains("mapper.xml.scriban", result.ErrorMessage);
        Assert.Contains("第 2 行", result.ErrorMessage);
    }

    /// <summary>
    /// 渲染前已取消应抛出 OperationCanceledException，供调用方区分取消与失败。
    /// </summary>
    [Fact]
    public void Render_CancelledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => _engine.Render("{{ table.className }}", CreateTable(), null, CreatePackageContext(), cts.Token));
    }

    /// <summary>
    /// 路径占位渲染应解析表变量与 package.dir 占位。
    /// </summary>
    [Theory]
    [InlineData("{{table.variableName}}/entity/{{table.className}}.java", "sysUser/entity/SysUser.java")]
    [InlineData("{{package.dir}}/entity/{{table.className}}.java", "com/example/entity/SysUser.java")]
    [InlineData("{{table.rawName}}/{{table.className}}.txt", "sys_user/SysUser.txt")]
    public void RenderPathTemplate_ResolvesPlaceholders(string pathTemplate, string expected)
    {
        string output = _engine.RenderPathTemplate(pathTemplate, CreatePathContext());

        Assert.Equal(expected, output);
    }

    /// <summary>
    /// 无基础包名时 package.dir 应为空串，路径占位按原样渲染。
    /// </summary>
    [Fact]
    public void RenderPathTemplate_EmptyBasePackage_DirIsEmpty()
    {
        var package = new TemplatePackageInfo { Name = "plain", BasePackage = null, TypeMap = new Dictionary<string, string>() };
        var context = new TemplateRenderContext(CreateTable(), TemplatePackageContext.From(package));

        string output = _engine.RenderPathTemplate("{{package.dir}}/out/{{table.className}}.java", context);

        Assert.Equal("/out/SysUser.java", output);
    }

    /// <summary>
    /// 生成栏基础包名覆盖应取代 manifest 包名，package.dir 与 package.basePackage 同步变化。
    /// </summary>
    [Fact]
    public void RenderPathTemplate_BasePackageOverride_ReplacesManifest()
    {
        var package = new TemplatePackageInfo { Name = "java-mybatis-plus", BasePackage = "com.example", TypeMap = new Dictionary<string, string>() };
        TemplatePackageContext context = TemplatePackageContext.From(package, "com.example.common");

        string dir = _engine.RenderPathTemplate("{{package.dir}}", new TemplateRenderContext(CreateTable(), context));
        string basePackage = _engine.RenderPathTemplate("{{package.basePackage}}", new TemplateRenderContext(CreateTable(), context));

        Assert.Equal("com/example/common", dir);
        Assert.Equal("com.example.common", basePackage);
    }

    /// <summary>
    /// 基础包名覆盖为空或空白时回退使用 manifest 包名，保持原有生成行为。
    /// </summary>
    [Fact]
    public void RenderPathTemplate_BasePackageOverrideEmpty_FallsBackToManifest()
    {
        var package = new TemplatePackageInfo { Name = "java-mybatis-plus", BasePackage = "com.example", TypeMap = new Dictionary<string, string>() };
        TemplatePackageContext context = TemplatePackageContext.From(package, "  ");

        string dir = _engine.RenderPathTemplate("{{package.dir}}", new TemplateRenderContext(CreateTable(), context));

        Assert.Equal("com/example", dir);
    }

    /// <summary>
    /// 内容渲染中 package.basePackage 应反映生成栏覆盖值。
    /// </summary>
    [Fact]
    public void Render_BasePackageOverride_AppliesToPackageVariable()
    {
        var package = new TemplatePackageInfo { Name = "java-mybatis-plus", BasePackage = "com.example", TypeMap = new Dictionary<string, string>() };
        TemplatePackageContext context = TemplatePackageContext.From(package, "cn.foo.bar");

        PreviewResult result = _engine.Render("package {{package.basePackage}};", CreateTable(), null, context, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("package cn.foo.bar;", result.Output);
    }

    /// <summary>
    /// 路径模板语法错误应抛结构化渲染异常，携带行列定位。
    /// </summary>
    [Fact]
    public void RenderPathTemplate_ParseError_ThrowsTemplateRenderException()
    {
        TemplateRenderException exception = Assert.Throws<TemplateRenderException>(
            () => _engine.RenderPathTemplate("{{ if }}", CreatePathContext()));

        Assert.Equal(1, exception.Line);
        Assert.NotNull(exception.Column);
        Assert.Contains("输出路径模板", exception.Message);
    }

    /// <summary>
    /// 高亮语言应由模板文件名语言段推导，未识别回退 Plain。
    /// </summary>
    [Theory]
    [InlineData("entity.java.scriban", HighlightLanguage.Java)]
    [InlineData("mapper.xml.scriban", HighlightLanguage.Xml)]
    [InlineData("entity.cs.scriban", HighlightLanguage.CSharp)]
    [InlineData("entity.csharp.scriban", HighlightLanguage.CSharp)]
    [InlineData("query.sql.scriban", HighlightLanguage.Sql)]
    [InlineData("config.json.scriban", HighlightLanguage.Json)]
    [InlineData("plain.txt", HighlightLanguage.Plain)]
    [InlineData("entity.scriban", HighlightLanguage.Plain)]
    public void HighlightResolver_FromFileName_ReturnsLanguage(string fileName, HighlightLanguage expected)
    {
        Assert.Equal(expected, HighlightLanguageResolver.FromTemplateFileName(fileName));
    }

    /// <summary>
    /// 内置包全部 Scriban 模板应经共享渲染引擎成功渲染，输出包含类名，验证引擎与真实模板兼容。
    /// </summary>
    [Fact]
    public async Task Render_BuiltinPackageTemplates_RenderSuccessfully()
    {
        string builtinRoot = BuiltinTemplatePackages.GetDefaultRootPath();
        string builtinDir = Path.Combine(builtinRoot, "java-mybatis-plus");
        Assert.True(Directory.Exists(builtinDir), $"内置包未复制到输出目录：{builtinDir}");

        TemplatePackageInfo package = await TemplatePackageLoader.LoadFromDirectoryAsync(builtinDir, isBuiltin: true, CancellationToken.None);
        TemplatePackageContext packageContext = TemplatePackageContext.From(package);

        foreach (TemplateFileInfo file in package.Files)
        {
            string templateText = await File.ReadAllTextAsync(file.TemplatePath);
            PreviewResult result = _engine.Render(templateText, CreateTable(), null, packageContext, CancellationToken.None, file.RelativeTemplatePath);

            Assert.True(result.IsSuccess, $"模板 {file.RelativeTemplatePath} 渲染失败：{result.ErrorMessage}");
            Assert.Contains("SysUser", result.Output);
        }
    }

    /// <summary>
    /// 构造含主键与非主键列的样例表元数据。
    /// </summary>
    /// <returns>填充列集合的样例表。</returns>
    private static TableInfo CreateTable()
    {
        var table = new TableInfo
        {
            RawName = "sys_user",
            SchemaName = "shop",
            Comment = "系统用户表",
            ClassName = TableInfo.ToPascalCase("sys_user"),
            VariableName = TableInfo.ToCamelCase("sys_user")
        };

        table.SetColumns(new[]
        {
            new ColumnInfo { RawName = "id", PropertyName = "id", Comment = "主键", RawDbType = "bigint", IsPrimaryKey = true, AutoIncrement = true },
            new ColumnInfo { RawName = "user_name", PropertyName = "userName", Comment = "用户名", RawDbType = "varchar" },
            new ColumnInfo { RawName = "created_time", PropertyName = "createdTime", Comment = "创建时间", RawDbType = "datetime" }
        });
        return table;
    }

    /// <summary>
    /// 构造内置包语义的 package 侧渲染上下文。
    /// </summary>
    /// <returns>样例渲染上下文。</returns>
    private static TemplatePackageContext CreatePackageContext()
    {
        var package = new TemplatePackageInfo
        {
            Name = "java-mybatis-plus",
            BasePackage = "com.example",
            TypeMap = new Dictionary<string, string>
            {
                ["bigint"] = "Long",
                ["varchar"] = "String",
                ["datetime"] = "LocalDateTime"
            }
        };
        return TemplatePackageContext.From(package);
    }

    /// <summary>
    /// 构造路径占位渲染上下文。
    /// </summary>
    /// <returns>样例路径渲染上下文。</returns>
    private static TemplateRenderContext CreatePathContext()
    {
        return new TemplateRenderContext(CreateTable(), CreatePackageContext());
    }

    /// <summary>
    /// 配置服务测试替身，仅承载内存配置快照供类型映射服务读取。
    /// </summary>
    private sealed class StubConfigService : IConfigService
    {
        /// <summary>
        /// 内存配置快照，默认空配置。
        /// </summary>
        public AppConfig Current { get; set; } = new();

        /// <inheritdoc />
        public string ConfigFilePath => "stub";

        /// <inheritdoc />
        public event EventHandler? ConfigChanged;

        /// <inheritdoc />
        public AppConfig Load() => Current;

        /// <inheritdoc />
        public void Save()
        {
            ConfigChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <inheritdoc />
        public GenerationDefaults GetGenerationDefaults() => new(string.Empty, string.Empty);

        /// <inheritdoc />
        public string? GetLlmApiKey() => null;
    }
}
