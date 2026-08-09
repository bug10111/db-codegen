using System.Diagnostics;
using DbCodeGen.Core.Model;
using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;
using Scriban.Syntax;

namespace DbCodeGen.Core.Templates;

/// <summary>
/// 共享模板渲染引擎，封装 Scriban，为模板编辑预览与批量代码生成提供统一的内容渲染与路径占位渲染能力。
/// 内容渲染 Render 与路径占位渲染 RenderPathTemplate 同属一个服务，保证"所见即所得"。
/// </summary>
public sealed class TemplateEngine
{
    private readonly ITypeMappingService? _typeMappingService;

    /// <summary>
    /// 创建共享模板渲染引擎；可传入全局类型映射服务供 tool.type/typeImport/imports 使用，为空时按包 typeMap 旧行为渲染。
    /// </summary>
    /// <param name="typeMappingService">全局类型映射解析服务，可为空。</param>
    public TemplateEngine(ITypeMappingService? typeMappingService = null)
    {
        _typeMappingService = typeMappingService;
    }

    /// <summary>
    /// 渲染模板内容，注入 table / column? / package / tool 上下文；失败时返回结构化错误与行列定位。
    /// 实时预览与批量生成共用本方法渲染模板正文。
    /// </summary>
    /// <param name="templateText">模板文本。</param>
    /// <param name="table">当前表元数据。</param>
    /// <param name="column">可选的当前列上下文，模板内 column 变量取自此参数。</param>
    /// <param name="packageContext">package 侧渲染上下文。</param>
    /// <param name="cancellationToken">取消标记，渲染被取消时抛 OperationCanceledException。</param>
    /// <param name="templateName">模板名，用于错误定位展示；为空时使用包名。</param>
    /// <returns>渲染结果，成功时含真实代码，失败时含错误描述与行列。</returns>
    /// <exception cref="OperationCanceledException">渲染被取消时抛出。</exception>
    public PreviewResult Render(
        string templateText,
        TableInfo table,
        ColumnInfo? column,
        TemplatePackageContext packageContext,
        CancellationToken cancellationToken,
        string? templateName = null)
    {
        ArgumentNullException.ThrowIfNull(templateText);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(packageContext);
        cancellationToken.ThrowIfCancellationRequested();

        string sourceName = string.IsNullOrWhiteSpace(templateName) ? packageContext.Name : templateName;
        Stopwatch stopwatch = Stopwatch.StartNew();

        Template template = Template.Parse(templateText, sourceName);
        if (template.HasErrors)
        {
            // 模板存在语法错误时直接结构化返回，不再进入渲染
            (string errorMessage, int errorLine, int errorColumn) = FirstParseError(template);
            stopwatch.Stop();
            return PreviewResult.Error(FormatErrorMessage(sourceName, errorMessage, errorLine, errorColumn), errorLine, errorColumn, stopwatch.ElapsedMilliseconds);
        }

        // 组装渲染上下文：table / column? / package / tool
        ScriptObject root = BuildRootContext(table, column, packageContext);

        try
        {
            var context = new TemplateContext();
            context.CancellationToken = cancellationToken;
            context.PushGlobal(root);
            string output = template.Render(context);
            stopwatch.Stop();
            return PreviewResult.Success(output, stopwatch.ElapsedMilliseconds);
        }
        catch (ScriptAbortException exception)
        {
            stopwatch.Stop();
            // Scriban 以 ScriptAbortException 表达取消，转换为 OperationCanceledException 供调用方区分
            throw new OperationCanceledException("模板渲染已取消。", exception, cancellationToken);
        }
        catch (ScriptRuntimeException exception)
        {
            stopwatch.Stop();
            int errorLine = exception.Span.Start.Line + 1;
            int errorColumn = exception.Span.Start.Column + 1;
            return PreviewResult.Error(FormatErrorMessage(sourceName, exception.OriginalMessage, errorLine, errorColumn), errorLine, errorColumn, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // 其它途径的取消信号原样上抛，不落入通用异常分支
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return PreviewResult.Error($"{sourceName} 渲染异常：{exception.Message}", null, null, stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// 渲染输出路径模板中的占位变量，支持 table.*（如 {{table.variableName}} / {{table.className}}）、
    /// package.*（如 {{package.dir}}）与 tool.* 函数（如 {{tool.firstLowerCase(table.className)}}），
    /// 供批量生成解析 manifest files[].output；渲染失败时抛结构化异常携带行列。
    /// </summary>
    /// <param name="pathTemplate">输出相对路径模板。</param>
    /// <param name="context">路径渲染上下文，提供表与 package 占位值。</param>
    /// <returns>渲染后的相对输出路径。</returns>
    /// <exception cref="TemplateRenderException">路径模板解析或渲染失败时抛出，携带行列定位。</exception>
    public string RenderPathTemplate(string pathTemplate, TemplateRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(pathTemplate);
        ArgumentNullException.ThrowIfNull(context);

        const string sourceName = "输出路径模板";
        Template template = Template.Parse(pathTemplate, sourceName);
        if (template.HasErrors)
        {
            // 路径模板语法错误按渲染异常抛出，供批量生成整单定位
            (string message, int line, int column) = FirstParseError(template);
            throw new TemplateRenderException(FormatErrorMessage(sourceName, message, line, column), line, column);
        }

        ScriptObject root = BuildPathContext(context);
        try
        {
            var renderContext = new TemplateContext();
            renderContext.PushGlobal(root);
            return template.Render(renderContext);
        }
        catch (ScriptRuntimeException exception)
        {
            int line = exception.Span.Start.Line + 1;
            int column = exception.Span.Start.Column + 1;
            throw new TemplateRenderException($"{sourceName}渲染失败：{exception.OriginalMessage}", exception, line, column);
        }
    }

    /// <summary>
    /// 从解析消息中提取第一条错误消息及其 1 基行列，供语法错误结构化返回。
    /// </summary>
    /// <param name="template">已解析模板。</param>
    /// <returns>错误消息文本与行列；无错误消息时回退通用文案。</returns>
    private static (string Message, int Line, int Column) FirstParseError(Template template)
    {
        foreach (LogMessage message in template.Messages)
        {
            if (message.Type != ParserMessageType.Error)
            {
                continue;
            }

            // Scriban 的 Span 行列从 0 开始，转为从 1 开始供编辑器定位
            return (message.Message, message.Span.Start.Line + 1, message.Span.Start.Column + 1);
        }

        return ("模板语法错误。", 1, 1);
    }

    /// <summary>
    /// 组装内容渲染根上下文，注入 table / column? / package / tool 四个变量节点。
    /// </summary>
    private ScriptObject BuildRootContext(TableInfo table, ColumnInfo? column, TemplatePackageContext packageContext)
    {
        var root = new ScriptObject();
        root.SetValue("table", BuildTableObject(table), true);
        root.SetValue("package", BuildPackageObject(packageContext), true);
        root.SetValue("tool", ToolFunctions.Build(packageContext, _typeMappingService, table.DatabaseType), true);

        // 当前列上下文仅在调用方显式提供时注入，模板内按需使用
        if (column is not null)
        {
            root.SetValue("column", BuildColumnObject(column), true);
        }

        return root;
    }

    /// <summary>
    /// 将表元数据转换为脚本对象，列集合以 ScriptArray 承载供模板 foreach 遍历。
    /// </summary>
    private static ScriptObject BuildTableObject(TableInfo table)
    {
        var tableObject = new ScriptObject();
        tableObject.SetValue("rawName", table.RawName, true);
        if (table.SchemaName is not null)
        {
            tableObject.SetValue("schemaName", table.SchemaName, true);
        }

        tableObject.SetValue("className", table.ClassName, true);
        tableObject.SetValue("variableName", table.VariableName, true);
        if (table.Comment is not null)
        {
            tableObject.SetValue("comment", table.Comment, true);
        }

        tableObject.SetValue("columns", BuildColumnArray(table.Columns), true);
        tableObject.SetValue("primaryKeys", BuildColumnArray(table.PrimaryKeys), true);
        tableObject.SetValue("fullColumn", BuildColumnArray(table.FullColumn), true);
        tableObject.SetValue("otherColumn", BuildColumnArray(table.OtherColumn), true);
        return tableObject;
    }

    /// <summary>
    /// 将列集合转换为脚本数组，逐列调用列对象转换。
    /// </summary>
    private static ScriptArray BuildColumnArray(IEnumerable<ColumnInfo> columns)
    {
        var array = new ScriptArray();
        foreach (ColumnInfo column in columns)
        {
            array.Add(BuildColumnObject(column));
        }

        return array;
    }

    /// <summary>
    /// 将列元数据转换为脚本对象，可空字段仅在非空时注入。
    /// </summary>
    private static ScriptObject BuildColumnObject(ColumnInfo column)
    {
        var columnObject = new ScriptObject();
        columnObject.SetValue("propertyName", column.PropertyName, true);
        columnObject.SetValue("rawName", column.RawName, true);
        if (column.Comment is not null)
        {
            columnObject.SetValue("comment", column.Comment, true);
        }

        columnObject.SetValue("rawDbType", column.RawDbType, true);
        columnObject.SetValue("isPrimaryKey", column.IsPrimaryKey, true);
        columnObject.SetValue("autoIncrement", column.AutoIncrement, true);
        columnObject.SetValue("isNullable", column.IsNullable, true);
        if (column.DefaultValue is not null)
        {
            columnObject.SetValue("defaultValue", column.DefaultValue, true);
        }

        if (column.Length is not null)
        {
            columnObject.SetValue("length", column.Length.Value, true);
        }

        if (column.Precision is not null)
        {
            columnObject.SetValue("precision", column.Precision.Value, true);
        }

        if (column.Scale is not null)
        {
            columnObject.SetValue("scale", column.Scale.Value, true);
        }

        return columnObject;
    }

    /// <summary>
    /// 将 package 侧上下文转换为脚本对象，注入 name / dir / basePackage。
    /// </summary>
    private static ScriptObject BuildPackageObject(TemplatePackageContext packageContext)
    {
        var packageObject = new ScriptObject();
        packageObject.SetValue("name", packageContext.Name, true);
        packageObject.SetValue("dir", packageContext.Dir, true);
        if (packageContext.BasePackage is not null)
        {
            packageObject.SetValue("basePackage", packageContext.BasePackage, true);
        }

        return packageObject;
    }

    /// <summary>
    /// 组装路径渲染上下文，注入 table / package / tool 三个变量节点，与内容渲染对齐，
    /// 使路径占位同样可使用 tool 函数（如 {{tool.firstLowerCase(table.className)}}）。
    /// </summary>
    private ScriptObject BuildPathContext(TemplateRenderContext context)
    {
        var root = new ScriptObject();

        var tableObject = new ScriptObject();
        tableObject.SetValue("variableName", context.Table.VariableName, true);
        tableObject.SetValue("className", context.Table.ClassName, true);
        tableObject.SetValue("rawName", context.Table.RawName, true);
        root.SetValue("table", tableObject, true);

        var packageObject = new ScriptObject();
        packageObject.SetValue("dir", context.Package.Dir, true);
        packageObject.SetValue("name", context.Package.Name, true);
        if (context.Package.BasePackage is not null)
        {
            packageObject.SetValue("basePackage", context.Package.BasePackage, true);
        }

        root.SetValue("package", packageObject, true);

        // 路径占位复用 tool 函数集，供 firstLowerCase/hump2Underline 等命名转换；映射服务为空时按包 typeMap 旧行为
        root.SetValue("tool", ToolFunctions.Build(context.Package, _typeMappingService, context.Table.DatabaseType), true);
        return root;
    }

    /// <summary>
    /// 组装结构化错误文案，携带模板名与行列信息，供预览区展示与批量生成定位。
    /// </summary>
    private static string FormatErrorMessage(string sourceName, string message, int line, int column)
    {
        return $"{sourceName} 第 {line} 行 第 {column} 列：{message}";
    }
}
