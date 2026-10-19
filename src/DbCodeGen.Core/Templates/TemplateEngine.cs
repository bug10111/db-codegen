using System.Diagnostics;
using DbCodeGen.Core.Generation;
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
    /// <summary>
    /// 包内共享函数文件约定名（相对包根）。存在时渲染主模板前先以同一渲染上下文执行其中的
    /// func 定义，供包内全部模板复用；文件不存在时渲染行为与无共享函数机制完全一致。
    /// </summary>
    public const string SharedFunctionsFileName = "_functions.scriban";

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
    /// 实时预览与批量生成共用本方法渲染模板正文。传入 scopeContext 时附加注入 vars / module / tables 作用域变量。
    /// </summary>
    /// <param name="templateText">模板文本。</param>
    /// <param name="table">当前表元数据。</param>
    /// <param name="column">可选的当前列上下文，模板内 column 变量取自此参数。</param>
    /// <param name="packageContext">package 侧渲染上下文。</param>
    /// <param name="cancellationToken">取消标记，渲染被取消时抛 OperationCanceledException。</param>
    /// <param name="templateName">模板名，用于错误定位展示；为空时使用包名。</param>
    /// <param name="scopeContext">可选的作用域渲染输入，注入 vars / module / tables；为空时与既有行为一致。</param>
    /// <returns>渲染结果，成功时含真实代码，失败时含错误描述与行列。</returns>
    /// <exception cref="OperationCanceledException">渲染被取消时抛出。</exception>
    public PreviewResult Render(
        string templateText,
        TableInfo table,
        ColumnInfo? column,
        TemplatePackageContext packageContext,
        CancellationToken cancellationToken,
        string? templateName = null,
        ScopeRenderInput? scopeContext = null)
    {
        ArgumentNullException.ThrowIfNull(templateText);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(packageContext);
        cancellationToken.ThrowIfCancellationRequested();

        string sourceName = string.IsNullOrWhiteSpace(templateName) ? packageContext.Name : templateName;

        Template template = Template.Parse(templateText, sourceName);
        if (template.HasErrors)
        {
            // 模板存在语法错误时直接结构化返回，不再进入渲染
            (string errorMessage, int errorLine, int errorColumn) = FirstParseError(template);
            return PreviewResult.Error(FormatErrorMessage(sourceName, errorMessage, errorLine, errorColumn), errorLine, errorColumn, 0);
        }

        // 组装渲染上下文：table / column? / package / tool（+ 可选作用域变量 vars / module / tables）
        ScriptObject root = BuildRootContext(table, column, packageContext, scopeContext);

        return RenderCore(template, root, packageContext, cancellationToken, sourceName);
    }

    /// <summary>
    /// 按作用域渲染模板正文（module / project 作用域片段与生成共用）：不注入 table / column 单表上下文，
    /// 注入 package / tool 与作用域变量 vars? / module? / tables? / modules?（modules 由调用方按输入决定携带，
    /// 批量生成管线中仅 project 作用域输入携带）；
    /// 渲染管线（共享函数文件、空行规整、结构化错误）与表作用域 Render 完全一致。
    /// </summary>
    /// <param name="templateText">模板文本。</param>
    /// <param name="packageContext">package 侧渲染上下文。</param>
    /// <param name="scopeContext">作用域渲染输入，承载 vars / module / tables / modules。</param>
    /// <param name="cancellationToken">取消标记，渲染被取消时抛 OperationCanceledException。</param>
    /// <param name="templateName">模板名，用于错误定位展示；为空时使用包名。</param>
    /// <returns>渲染结果，成功时含真实代码，失败时含错误描述与行列。</returns>
    /// <exception cref="OperationCanceledException">渲染被取消时抛出。</exception>
    public PreviewResult RenderScoped(
        string templateText,
        TemplatePackageContext packageContext,
        ScopeRenderInput scopeContext,
        CancellationToken cancellationToken,
        string? templateName = null)
    {
        ArgumentNullException.ThrowIfNull(templateText);
        ArgumentNullException.ThrowIfNull(packageContext);
        ArgumentNullException.ThrowIfNull(scopeContext);
        cancellationToken.ThrowIfCancellationRequested();

        string sourceName = string.IsNullOrWhiteSpace(templateName) ? packageContext.Name : templateName;

        Template template = Template.Parse(templateText, sourceName);
        if (template.HasErrors)
        {
            // 模板存在语法错误时直接结构化返回，不再进入渲染
            (string errorMessage, int errorLine, int errorColumn) = FirstParseError(template);
            return PreviewResult.Error(FormatErrorMessage(sourceName, errorMessage, errorLine, errorColumn), errorLine, errorColumn, 0);
        }

        // 组装作用域渲染上下文：package / tool + vars? / module? / tables?（无单表 table / column）
        ScriptObject root = BuildScopedRootContext(packageContext, scopeContext);

        return RenderCore(template, root, packageContext, cancellationToken, sourceName);
    }

    /// <summary>
    /// 渲染执行共用管线：以既定根上下文执行包内共享函数文件、渲染主模板并规整空行；
    /// 取消、运行期错误与语法错误的结构化映射对表作用域与作用域渲染保持一致。
    /// </summary>
    /// <param name="template">已解析模板。</param>
    /// <param name="root">已组装的渲染根上下文。</param>
    /// <param name="packageContext">package 侧渲染上下文，供共享函数文件定位。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <param name="sourceName">模板名，用于错误定位展示。</param>
    /// <returns>渲染结果，成功时含真实代码，失败时含错误描述与行列。</returns>
    private PreviewResult RenderCore(
        Template template,
        ScriptObject root,
        TemplatePackageContext packageContext,
        CancellationToken cancellationToken,
        string sourceName)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        // 渲染前集合消费守卫：调用方 variables 标量值被 for 迭代时 Scriban 会逐字符产出垃圾内容，
        // 此处按模板 AST 定位后结构化拒绝，行列指向模板内集合消费表达式
        (string GuardMessage, int GuardLine, int GuardColumn)? guardError =
            CollectionIterationGuard.FindScalarIteration(template, root);
        if (guardError is not null)
        {
            stopwatch.Stop();
            return PreviewResult.Error(
                FormatErrorMessage(sourceName, guardError.Value.GuardMessage, guardError.Value.GuardLine, guardError.Value.GuardColumn),
                guardError.Value.GuardLine,
                guardError.Value.GuardColumn,
                stopwatch.ElapsedMilliseconds);
        }

        // 渲染前空迭代分析：for 迭代源为空集合 / null 的零迭代收集为非阻断告警（守卫文案设计附带降噪指引），
        // 随成功结果透出供三入口（render/preview/generate）回执可见
        IReadOnlyList<EmptyIterationWarning> emptyIterations = EmptyIterationGuard.FindEmptyIterations(template, root);

        // 解析包内共享函数文件：约定文件不存在时为 null，渲染管线零改动，保证无共享文件包行为不变
        string? sharedFunctionsPath = ResolveSharedFunctionsPath(packageContext);
        Template? sharedFunctionsTemplate = null;
        if (sharedFunctionsPath is not null)
        {
            (sharedFunctionsTemplate, (string Message, int Line, int Column)? parseError) = ParseSharedFunctions(sharedFunctionsPath);
            if (parseError is not null)
            {
                // 共享函数文件语法错误按其自身文件名结构化返回，行列定位在共享文件内部
                stopwatch.Stop();
                return PreviewResult.Error(
                    FormatErrorMessage(SharedFunctionsFileName, parseError.Value.Message, parseError.Value.Line, parseError.Value.Column),
                    parseError.Value.Line, parseError.Value.Column, stopwatch.ElapsedMilliseconds);
            }
        }

        try
        {
            var context = new TemplateContext();
            context.CancellationToken = cancellationToken;
            context.PushGlobal(root);

            // 主模板渲染前先执行共享函数文件：func 定义注册进同一渲染上下文的全局作用域供主模板调用；
            // 共享文件仅为定义副作用执行，其输出文本丢弃，运行期错误按共享文件名结构化定位
            if (sharedFunctionsTemplate is not null)
            {
                (string? sharedError, int? sharedErrorLine, int? sharedErrorColumn) =
                    RenderSharedFunctions(sharedFunctionsTemplate, context, sharedFunctionsPath!);
                if (sharedError is not null)
                {
                    stopwatch.Stop();
                    return PreviewResult.Error(sharedError, sharedErrorLine, sharedErrorColumn, stopwatch.ElapsedMilliseconds);
                }
            }

            string output = NormalizeBlankLines(template.Render(context));
            stopwatch.Stop();
            var warnings = new List<string>(emptyIterations.Count);
            foreach (EmptyIterationWarning warning in emptyIterations)
            {
                warnings.Add(warning.Message);
            }

            return PreviewResult.Success(output, stopwatch.ElapsedMilliseconds, warnings);
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
    /// 按作用域渲染输出路径模板占位（module / project 作用域）：注入 package / tool 与 vars? / module?，
    /// 不注入单表 table；module 作用域路径可引用 {{module.name}}，project 作用域可引用 {{vars.*}}。
    /// </summary>
    /// <param name="pathTemplate">输出相对路径模板。</param>
    /// <param name="packageContext">package 侧渲染上下文。</param>
    /// <param name="scopeContext">作用域渲染输入。</param>
    /// <returns>渲染后的相对输出路径。</returns>
    /// <exception cref="TemplateRenderException">路径模板解析或渲染失败时抛出，携带行列定位。</exception>
    public string RenderScopedPathTemplate(
        string pathTemplate,
        TemplatePackageContext packageContext,
        ScopeRenderInput scopeContext)
    {
        ArgumentNullException.ThrowIfNull(pathTemplate);
        ArgumentNullException.ThrowIfNull(packageContext);
        ArgumentNullException.ThrowIfNull(scopeContext);

        const string sourceName = "输出路径模板";
        Template template = Template.Parse(pathTemplate, sourceName);
        if (template.HasErrors)
        {
            // 路径模板语法错误按渲染异常抛出，供批量生成整单定位
            (string message, int line, int column) = FirstParseError(template);
            throw new TemplateRenderException(FormatErrorMessage(sourceName, message, line, column), line, column);
        }

        var root = new ScriptObject();
        root.SetValue("package", BuildPackageObject(packageContext), true);
        root.SetValue("tool", ToolFunctions.Build(packageContext, _typeMappingService, ResolveScopeDatabaseType(scopeContext)), true);

        // module 作用域路径可引用 module.*（如 {{module.name}}）；vars 供工程级路径参数引用
        if (scopeContext.Module is not null)
        {
            root.SetValue("module", BuildModuleObject(scopeContext.Module), true);
        }

        if (scopeContext.Variables is not null)
        {
            root.SetValue("vars", BuildVarsObject(scopeContext.Variables), true);
        }

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
    /// 解析包内共享函数文件路径：包根已知且约定文件存在时返回其绝对路径，否则返回 null
    /// （无包根的测试直构上下文或无共享文件的包均跳过注入，渲染行为不变）。
    /// </summary>
    /// <param name="packageContext">package 侧渲染上下文，提供包根目录。</param>
    /// <returns>共享函数文件绝对路径；无需注入时为 null。</returns>
    private static string? ResolveSharedFunctionsPath(TemplatePackageContext packageContext)
    {
        string? rootPath = packageContext.RootPath;
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        string candidate = Path.Combine(rootPath, SharedFunctionsFileName);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// 读取并解析包内共享函数文件；语法错误按共享文件自身行列结构化返回，不进入渲染。
    /// </summary>
    /// <param name="sharedFunctionsPath">共享函数文件绝对路径。</param>
    /// <returns>解析成功的模板；失败时模板为 null 并携带错误消息与行列。</returns>
    private static (Template? Template, (string Message, int Line, int Column)? Error) ParseSharedFunctions(string sharedFunctionsPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(sharedFunctionsPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 共享函数文件读取失败（被外部占用/权限变动）按结构化错误返回，不中断渲染管线崩溃
            return (null, ($"共享函数文件读取失败：{exception.Message}", 1, 1));
        }

        Template template = Template.Parse(text, SharedFunctionsFileName);
        if (template.HasErrors)
        {
            (string message, int line, int column) = FirstParseError(template);
            return (null, (message, line, column));
        }

        return (template, null);
    }

    /// <summary>
    /// 以主模板同一渲染上下文执行共享函数文件：func 定义注册进全局作用域，输出文本丢弃。
    /// 运行期错误按共享文件名与共享文件内行列返回；取消与中止异常穿透由调用方统一处理。
    /// </summary>
    /// <param name="sharedFunctionsTemplate">已解析的共享函数模板。</param>
    /// <param name="context">主模板渲染上下文，func 定义注册于此。</param>
    /// <param name="sharedFunctionsPath">共享函数文件路径，供错误消息归因。</param>
    /// <returns>错误三元组；成功时 ErrorMessage 为 null。</returns>
    private static (string? ErrorMessage, int? Line, int? Column) RenderSharedFunctions(
        Template sharedFunctionsTemplate, TemplateContext context, string sharedFunctionsPath)
    {
        try
        {
            // 输出丢弃：共享文件预期只承载 func 定义，不产出正文文本
            _ = sharedFunctionsTemplate.Render(context);
            return (null, null, null);
        }
        catch (ScriptRuntimeException exception)
        {
            int line = exception.Span.Start.Line + 1;
            int column = exception.Span.Start.Column + 1;
            return (FormatErrorMessage(SharedFunctionsFileName, exception.OriginalMessage, line, column), line, column);
        }
    }

    /// <summary>
    /// 组装内容渲染根上下文，注入 table / column? / package / tool 四个变量节点；
    /// 传入 scopeContext 时附加注入 vars / module / tables 作用域变量。
    /// </summary>
    private ScriptObject BuildRootContext(TableInfo table, ColumnInfo? column, TemplatePackageContext packageContext, ScopeRenderInput? scopeContext = null)
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

        ApplyScopeContext(root, scopeContext);
        return root;
    }

    /// <summary>
    /// 组装作用域（module / project）内容渲染根上下文：注入 package / tool 与 vars? / module? / tables? / modules?
    /// （modules 由调用方按输入决定携带，批量生成管线中仅 project 作用域输入携带），不注入单表 table / column；
    /// tool 的数据库类型取全表汇总中首个非空值（同次生成通常同库）。
    /// </summary>
    /// <param name="packageContext">package 侧渲染上下文。</param>
    /// <param name="scopeContext">作用域渲染输入。</param>
    /// <returns>作用域渲染根上下文。</returns>
    private ScriptObject BuildScopedRootContext(TemplatePackageContext packageContext, ScopeRenderInput scopeContext)
    {
        DataSourceType? databaseType = ResolveScopeDatabaseType(scopeContext);
        var root = new ScriptObject();
        root.SetValue("package", BuildPackageObject(packageContext), true);
        root.SetValue("tool", ToolFunctions.Build(packageContext, _typeMappingService, databaseType), true);
        ApplyScopeContext(root, scopeContext);
        return root;
    }

    /// <summary>
    /// 向渲染根上下文注入作用域变量：vars.*（键值经 Scriban 值转换）、module.*（name + 附加属性）、
    /// tables（全表汇总，元素字段与 table.* 同构）、modules（全模块清单，project 作用域聚合循环消费，
    /// 元素字段与 module.* 同构）；各集合仅在调用方提供时注入。
    /// </summary>
    /// <param name="root">渲染根上下文。</param>
    /// <param name="scopeContext">作用域渲染输入，可为空（空时不注入任何作用域变量，行为与既有一致）。</param>
    private static void ApplyScopeContext(
        ScriptObject root,
        ScopeRenderInput? scopeContext)
    {
        if (scopeContext is null)
        {
            return;
        }

        // 调用方变量集合注入 vars 子对象：键已在 ScopeRenderInput.Create 校验过保留命名空间冲突
        if (scopeContext.Variables is not null)
        {
            root.SetValue("vars", BuildVarsObject(scopeContext.Variables), true);
        }

        // 当前模块注入 module 子对象：name 必含，其余附加属性原样注入
        if (scopeContext.Module is not null)
        {
            root.SetValue("module", BuildModuleObject(scopeContext.Module), true);
        }

        // 全表汇总注入 tables 集合：元素字段与 table.* 同构，供 project/module 模板循环登记胶水代码
        if (scopeContext.Tables is not null)
        {
            var tablesArray = new ScriptArray();
            foreach (TableInfo table in scopeContext.Tables)
            {
                tablesArray.Add(BuildTableObject(table)!);
            }

            root.SetValue("tables", tablesArray, true);
        }

        // 全模块清单注入 modules 集合：元素字段与 module.* 同构，供 project 模板循环模块聚合段；
        // 与用户显式传入的 vars.modules 分属根变量与 vars 子键两个命名空间，同名互不遮蔽
        if (scopeContext.Modules is not null)
        {
            var modulesArray = new ScriptArray();
            foreach (ModuleContext module in scopeContext.Modules)
            {
                modulesArray.Add(BuildModuleObject(module)!);
            }

            root.SetValue("modules", modulesArray, true);
        }
    }

    /// <summary>
    /// 将调用方变量集合构建为 vars 子对象，键值经 Scriban 值转换。
    /// </summary>
    /// <param name="variables">调用方注入变量集合（键已校验保留命名空间冲突）。</param>
    /// <returns>vars 脚本对象。</returns>
    private static ScriptObject BuildVarsObject(IReadOnlyDictionary<string, object?> variables)
    {
        var varsObject = new ScriptObject();
        foreach (KeyValuePair<string, object?> pair in variables)
        {
            varsObject.SetValue(pair.Key, ScribanValueFactory.ToScribanValue(pair.Value), true);
        }

        return varsObject;
    }

    /// <summary>
    /// 将模块上下文构建为 module 子对象：name 必含，其余附加属性原样注入。
    /// </summary>
    /// <param name="module">当前模块上下文。</param>
    /// <returns>module 脚本对象。</returns>
    private static ScriptObject BuildModuleObject(ModuleContext module)
    {
        var moduleObject = new ScriptObject();
        moduleObject.SetValue("name", module.Name, true);
        foreach (KeyValuePair<string, object?> pair in module.Properties)
        {
            moduleObject.SetValue(pair.Key, ScribanValueFactory.ToScribanValue(pair.Value), true);
        }

        return moduleObject;
    }

    /// <summary>
    /// 解析作用域渲染的 tool 数据库类型：取全表汇总中首个非空值，全空时返回 null（仅命中通用映射条目）。
    /// </summary>
    /// <param name="scopeContext">作用域渲染输入。</param>
    /// <returns>首个非空数据库类型；无可用值时为 null。</returns>
    private static DataSourceType? ResolveScopeDatabaseType(ScopeRenderInput scopeContext)
    {
        if (scopeContext.Tables is null)
        {
            return null;
        }

        foreach (TableInfo table in scopeContext.Tables)
        {
            if (table.DatabaseType is not null)
            {
                return table.DatabaseType;
            }
        }

        return null;
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

        // 实体名按命名策略派生；未派生时回退类名，保证既有上下文行为不变
        string entityName = string.IsNullOrEmpty(table.EntityName) ? table.ClassName : table.EntityName;
        tableObject.SetValue("entityName", entityName, true);
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
    /// 将 package 侧上下文转换为脚本对象，注入 name / dir / basePackage / segments / permissionModule / naming。
    /// segments 为归一后的包段清单（全小写、kebab 归一、连续重复段去重），permissionModule 为请求级派生值，
    /// naming 为生效命名策略（stripTablePrefix/tablePrefixes/singularize/entitySuffix/packageSegmentCase/layerPrefix）。
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

        var segmentsArray = new ScriptArray();
        foreach (string segment in packageContext.Segments)
        {
            segmentsArray.Add(segment);
        }

        packageObject.SetValue("segments", segmentsArray, true);
        packageObject.SetValue("permissionModule", packageContext.PermissionModule, true);

        // 生效命名策略整体透出，供模板按 package.naming.* 组装路径段（如 layerPrefix）与实体后缀
        var namingObject = new ScriptObject();
        namingObject.SetValue("stripTablePrefix", packageContext.Naming.StripTablePrefix, true);
        var prefixesArray = new ScriptArray();
        foreach (string prefix in packageContext.Naming.TablePrefixes)
        {
            prefixesArray.Add(prefix);
        }

        namingObject.SetValue("tablePrefixes", prefixesArray, true);
        namingObject.SetValue("singularize", packageContext.Naming.Singularize, true);
        namingObject.SetValue("entitySuffix", packageContext.Naming.EntitySuffix, true);
        namingObject.SetValue("packageSegmentCase", packageContext.Naming.PackageSegmentCase, true);
        namingObject.SetValue("layerPrefix", packageContext.Naming.LayerPrefix, true);
        packageObject.SetValue("naming", namingObject, true);

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
        string entityName = string.IsNullOrEmpty(context.Table.EntityName) ? context.Table.ClassName : context.Table.EntityName;
        tableObject.SetValue("entityName", entityName, true);
        tableObject.SetValue("variableName", context.Table.VariableName, true);
        tableObject.SetValue("className", context.Table.ClassName, true);
        tableObject.SetValue("rawName", context.Table.RawName, true);
        root.SetValue("table", tableObject, true);

        // 路径占位与内容渲染共用同一 package 对象（含 dir / segments / permissionModule / naming）
        root.SetValue("package", BuildPackageObject(context.Package), true);

        // 路径占位复用 tool 函数集，供 firstLowerCase/hump2Underline/packagePath/permissionKey 等命名转换
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

    /// <summary>
    /// 规整渲染结果的空行排版：连续多个空行压缩为单个空行、开头空行丢弃、仅含空白字符的行归一为空串、行尾风格保持模板原样；
    /// 并删除"注解行（以右括号结尾）之后"与"方法链延续行（以点开头）之前"的空行，保证注解紧邻字段、链式调用紧邻上一语句。
    /// 用于消除"控制标签独占一行"在渲染后残留的多余空行，避免生成代码被空行撑开。
    /// </summary>
    /// <param name="text">渲染后的原始文本。</param>
    /// <returns>规整后的文本，空输入原样返回。</returns>
    private static string NormalizeBlankLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // 按模板原始行尾风格（CRLF 或 LF）切分，避免规整后行尾风格漂移
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        // 第一遍：连续空行压缩为单个、开头空行丢弃、仅含空白字符的行统一为空串
        var collected = new List<string>(lines.Length);
        bool previousBlank = false;
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (collected.Count > 0 && !previousBlank)
                {
                    collected.Add(string.Empty);
                }

                previousBlank = true;
                continue;
            }

            collected.Add(line);
            previousBlank = false;
        }

        // 第二遍：删除不应保留的空行——前一非空行以右括号结尾（注解应紧邻其字段），
        // 或后一非空行以点开头（方法链延续行应紧邻上一语句）
        var normalized = new List<string>(collected.Count);
        for (int index = 0; index < collected.Count; index++)
        {
            if (collected[index].Length == 0)
            {
                string? previous = FindPreviousNonBlank(collected, index);
                string? next = FindNextNonBlank(collected, index);
                bool shouldDrop = (previous is not null && previous.TrimEnd().EndsWith(")", StringComparison.Ordinal))
                    || (next is not null && next.TrimStart().StartsWith(".", StringComparison.Ordinal));
                if (shouldDrop)
                {
                    continue;
                }
            }

            normalized.Add(collected[index]);
        }

        return string.Join(newline, normalized);
    }

    /// <summary>
    /// 向前查找指定空行之前的最近一个非空行，无则返回空引用。
    /// </summary>
    /// <param name="lines">已规整的行集合。</param>
    /// <param name="fromIndex">起始索引（不含）。</param>
    /// <returns>最近的非空行文本，不存在返回 null。</returns>
    private static string? FindPreviousNonBlank(List<string> lines, int fromIndex)
    {
        for (int index = fromIndex - 1; index >= 0; index--)
        {
            if (lines[index].Length > 0)
            {
                return lines[index];
            }
        }

        return null;
    }

    /// <summary>
    /// 向后查找指定空行之后的最近一个非空行，无则返回空引用。
    /// </summary>
    /// <param name="lines">已规整的行集合。</param>
    /// <param name="fromIndex">起始索引（不含）。</param>
    /// <returns>最近的非空行文本，不存在返回 null。</returns>
    private static string? FindNextNonBlank(List<string> lines, int fromIndex)
    {
        for (int index = fromIndex + 1; index < lines.Count; index++)
        {
            if (lines[index].Length > 0)
            {
                return lines[index];
            }
        }

        return null;
    }
}
