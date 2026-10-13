using System.ComponentModel;
using System.Text.Json;
using DbCodeGen.Core.Generation;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DbCodeGen.McpServer.Tools;

/// <summary>
/// 代码生成写盘工具组：按显式入参执行批量生成并逐文件写盘。
/// 零记忆态副作用（D8）：工作区根/代码目录/表/模板选择/覆盖策略全部显式传入，
/// 生成过程不读取也不回写 config.json 的任何最近路径与勾选记忆。
/// </summary>
[McpServerToolType]
public sealed class GenerationTools
{
    private readonly ITemplatePackageService _packageService;
    private readonly ICodeGenerator _codeGenerator;
    private readonly ILogger<GenerationTools> _logger;

    /// <summary>
    /// 以模板包服务、批量生成服务与日志器构造代码生成工具组。
    /// </summary>
    /// <param name="packageService">模板包服务，承载包加载与校验。</param>
    /// <param name="codeGenerator">批量生成服务，承载 dry-run 重算与逐文件写盘。</param>
    /// <param name="logger">工具组日志器。</param>
    public GenerationTools(
        ITemplatePackageService packageService,
        ICodeGenerator codeGenerator,
        ILogger<GenerationTools> logger)
    {
        _packageService = packageService;
        _codeGenerator = codeGenerator;
        _logger = logger;
    }

    /// <summary>
    /// 执行批量生成并写盘（内部强制先重算 dry-run，写盘无确认弹窗）。
    /// 全部参数显式传入：不读配置记忆态、生成后不回写 config.json。
    /// tables 按生效模板集合分型：仅当所选模板（files 过滤后）含 table 作用域条目时必填非空。
    /// </summary>
    /// <param name="package">模板包包名。</param>
    /// <param name="workspaceRoot">工作区根目录绝对路径（输出文件的根前缀）。</param>
    /// <param name="codeDirectory">代码目录（项目内完整相对路径含包名，如 src/main/java/com/example/common）；经源根标记拆分为相对输出根与基础包名，包间输出路径配方以包清单声明的输出路径形态为准。</param>
    /// <param name="tables">合成表集合；所选模板含 table 作用域条目时必填非空（至少一张表），纯 module/project 作用域集合可省略；需要完整列时建议先用 get_table_detail 取回后传入，离线合成场景直接内联表结构即可，无需连接数据库。</param>
    /// <param name="basePackageOverride">本次生成的基础包名覆盖值；为空时使用代码目录派生值。</param>
    /// <param name="files">模板过滤清单：传则仅生成清单内模板（包内不存在的路径直接拒绝），省略时默认选择包内全部启用条目。</param>
    /// <param name="duplicateStrategy">同名目标文件处理策略：overwrite（覆盖内容不同的同名文件）/ skip（同名一律跳过）。</param>
    /// <param name="variables">可选的调用方变量对象，逐键注入渲染上下文 vars.*（如 {"appName":"demo"}）；值支持任意 JSON 类型（字符串/数组/布尔/对象），集合消费键必须传真实数组；键与保留命名空间冲突时结构化拒绝。</param>
    /// <param name="modules">可选的模块清单（如 [{"name":"account"}]），包内 module 作用域模板按此逐个渲染；元素 name 必填，其余字段原样注入 module.*；勾选 module 作用域模板但省略时结构化报错。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>写盘结果统计与生成日志。</returns>
    [McpServerTool(Name = "generate", Title = "批量生成", Destructive = true)]
    [Description("执行批量代码生成并写盘（先重算 dry-run 再写盘，无确认弹窗）。建议先 preview_generation 确认文件计划；tables 按所选模板分型——所选模板（files 过滤后）含 table 作用域条目时必填非空（至少一张表），纯 module/project 作用域集合可省略（无表的手写域片段渲染用 render_template）。files 为模板过滤清单：传则仅生成清单内模板，省略时选包内全部启用条目。全参数显式，不读不写配置记忆。")]
    public async Task<object> GenerateAsync(
        string package,
        string workspaceRoot,
        [Description("代码目录（项目内完整相对路径含包名，如 src/main/java/com/example/common）：按源根标记（java/kotlin/groovy/scala/src）拆分为相对输出根与基础包名，纯包名形态输出根固定 src/main/java；包间输出路径配方差异以包清单 files[].output 形态为准（经 list_templates 查看）。")]
        string codeDirectory,
        [Description("合成表集合；所选模板（files 过滤后）含 table 作用域条目时必填非空（至少一张表），纯 module/project 作用域集合可省略；离线合成场景直接内联表结构（列元数据自带），无需连接数据库。")]
        IReadOnlyList<SyntheticTableInput>? tables = null,
        string? basePackageOverride = null,
        [Description("模板过滤清单：传则仅生成清单内模板（包内不存在的路径直接拒绝），省略时默认选择包内全部启用条目。")]
        IReadOnlyList<string>? files = null,
        [Description("同名目标文件处理策略：overwrite（覆盖内容不同的同名文件）/ skip（同名一律跳过）；对含手写代码的输出目录用 skip 防覆盖手写文件。")]
        DuplicateFileStrategy duplicateStrategy = DuplicateFileStrategy.Overwrite,
        [Description("调用方变量对象，逐键注入渲染上下文 vars.*（如 {\"appName\":\"demo\"}）；值支持任意 JSON 类型（字符串/数值/布尔/数组/对象），集合消费键必须传真实数组（传字符串将被结构化拒绝），布尔键传布尔值；键与保留命名空间冲突时结构化拒绝。")]
        IReadOnlyDictionary<string, JsonElement>? variables = null,
        IReadOnlyList<Dictionary<string, object?>>? modules = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            GenerationRequest request = await GenerationRequestFactory.CreateAsync(
                _packageService, package, tables, files, workspaceRoot, codeDirectory,
                basePackageOverride, duplicateStrategy.ToString().ToLowerInvariant(), persistLastUsedPaths: false,
                variables, modules, cancellationToken);

            // 零记忆态副作用：请求关闭生成后路径记忆回写，config.json 的最近工作区根/代码目录保持不变
            GenerationResult result = await _codeGenerator.GenerateAsync(request, progress: null, cancellationToken);

            List<GenerationLogItem> logs = new(result.Logs.Count);
            foreach (var logEntry in result.Logs)
            {
                logs.Add(new GenerationLogItem(logEntry.Timestamp, logEntry.Level.ToString().ToLowerInvariant(), logEntry.Message));
            }

            _logger.LogInformation(
                "generate 完成，包 {PackageName}，新增 {Generated} / 覆盖 {Overwritten} / 跳过 {Skipped} / 失败 {Failed}，取消 {IsCancelled}。",
                package, result.Generated, result.Overwritten, result.Skipped, result.Failed, result.IsCancelled);
            return new GenerateResult(
                result.Generated,
                result.Overwritten,
                result.Skipped,
                result.Failed,
                result.IsCancelled,
                logs);
        }
        catch (GenerationException exception)
        {
            // 渲染失败与输出路径越界属生成域结构化业务错误（携带模板名行列或被拒路径），映射模板包域错误便于调用方定位模板问题
            _logger.LogError(exception, "generate 生成域失败，包名 {PackageName}。", package);
            return new ToolErrorResult(ToolErrorResult.ErrorMarker, "TemplatePackageError", exception.Message);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "generate 执行失败，包名 {PackageName}。", package);
            return ToolError.From(exception, cancellationToken);
        }
    }
}

/// <summary>
/// generate 生成日志条目 DTO。
/// </summary>
/// <param name="Timestamp">日志时间。</param>
/// <param name="Level">日志级别：info / warning / error。</param>
/// <param name="Message">日志消息（目标文件相对路径与结果描述，不含敏感信息）。</param>
internal sealed record GenerationLogItem(DateTime Timestamp, string Level, string Message);

/// <summary>
/// generate 应答 DTO。
/// </summary>
/// <param name="Generated">新增成功写盘文件数。</param>
/// <param name="Overwritten">覆盖成功文件数。</param>
/// <param name="Skipped">跳过未写文件数。</param>
/// <param name="Failed">写盘失败文件数。</param>
/// <param name="IsCancelled">是否被取消，取消时部分文件可能已写盘。</param>
/// <param name="Logs">生成日志列表。</param>
internal sealed record GenerateResult(
    int Generated,
    int Overwritten,
    int Skipped,
    int Failed,
    bool IsCancelled,
    IReadOnlyList<GenerationLogItem> Logs);
