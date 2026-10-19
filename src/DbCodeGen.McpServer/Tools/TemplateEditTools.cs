using System.ComponentModel;
using System.Text.Json.Serialization;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DbCodeGen.McpServer.Tools;

/// <summary>
/// 模板编辑工具组：用户模板包内模板文件正文保存、新建、删除与重命名。
/// 全部转发 Core 模板包服务，内置包只读拒绝、路径防目录穿越与跨进程互斥均由 Core 服务层强制；
/// 桌面端不自动感知 MCP 侧的模板文件变更，需要时在桌面端重新加载模板包。
/// </summary>
[McpServerToolType]
public sealed class TemplateEditTools
{
    private readonly ITemplatePackageService _packageService;
    private readonly ILogger<TemplateEditTools> _logger;

    /// <summary>
    /// 以模板包服务与日志器构造模板编辑工具组。
    /// </summary>
    /// <param name="packageService">模板包服务，承载模板文件的保存/新建/删除/重命名与全部安全校验。</param>
    /// <param name="logger">工具组日志器。</param>
    public TemplateEditTools(ITemplatePackageService packageService, ILogger<TemplateEditTools> logger)
    {
        _packageService = packageService;
        _logger = logger;
    }

    /// <summary>
    /// 保存用户模板包内既有模板文件的正文（整篇覆写）；内置包只读拒绝，新建文件走 create_template_file。
    /// </summary>
    /// <param name="package">目标用户模板包包名。</param>
    /// <param name="path">模板文件相对包根路径（与 list_templates 返回的 path 一致）。</param>
    /// <param name="content">模板正文全文（Scriban 源文本，UTF-8 无 BOM 落盘）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>保存成功摘要或结构化错误。</returns>
    [McpServerTool(Name = "save_template_content", Title = "保存模板内容", Destructive = false, Idempotent = true)]
    [Description("用途：保存用户模板包内既有模板文件的正文（整篇覆写，Scriban 源文本）。\n"
        + "何时用：整篇重写模板内容；只改几行请用 save_template_edit 免整篇重传；新建文件先用 create_template_file。\n"
        + "参数：package(string,必填)=目标用户模板包包名；path(string,必填)=模板相对包根路径（须已存在于清单）；"
        + "content(string,必填)=模板正文全文（UTF-8 无 BOM 落盘；行尾保真：宿主文件非空时自动对齐宿主行尾形态）。\n"
        + "返回：package、action=saved、path（正斜杠相对路径）、fileCount（操作后包内模板文件数）；newPath/contentHash 等字段省略。\n"
        + "常见错误：TemplatePackageError（内置包只读；包不存在；同名冲突）；InvalidArgument（路径穿越等校验失败；文件不存在）；InternalError。")]
    public async Task<object> SaveTemplateContentAsync(
        [Description("目标用户模板包包名（必填）。")]
        string package,
        [Description("模板文件相对包根路径（必填）：须已存在于包清单，取 list_templates 返回的 path。")]
        string path,
        [Description("模板正文全文（必填）：Scriban 源文本，UTF-8 无 BOM 落盘；行尾自动对齐宿主文件形态。")]
        string content,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidatePackageAndPath(package, path);
            ArgumentNullException.ThrowIfNull(content);

            TemplatePackageOperationResult result = await _packageService.SaveTemplateContentAsync(
                package.Trim(), NormalizePath(path), content, cancellationToken);
            ToolErrorResult? error = FromOperationStatus(result, "save_template_content");
            if (error is not null)
            {
                return error;
            }

            _logger.LogInformation(
                "save_template_content 完成，包 {PackageName}，路径 {TemplatePath}，字符数 {CharCount}。",
                package, path, content.Length);
            return TemplateEditResult.Saved(result.Package!, NormalizePath(path));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "save_template_content 执行失败，包名 {PackageName}，路径 {TemplatePath}。", package, path);
            return ToolError.From(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 对用户模板包内既有模板文件执行子串级单处精确替换（小块替换原语：改几行免整篇覆写重传）。
    /// </summary>
    /// <param name="package">目标用户模板包包名。</param>
    /// <param name="path">模板文件相对包根路径。</param>
    /// <param name="oldText">待替换原文子串，须在正文中恰好出现一次；0 次命中拒绝（未找到）、多次命中拒绝（歧义保护并报命中次数）。行尾形态差异（CRLF/LF）不阻断匹配。</param>
    /// <param name="newText">替换后子串（空串=删除该子串），落地遵循宿主文件行尾形态。</param>
    /// <param name="expectedContentHash">可选条件写乐观锁：期望的当前内容摘要（SHA256 前 16 位 hex 小写，取自 read_template 回执 contentHash）；不匹配拒绝并回显当前摘要。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>编辑成功摘要（含新 contentHash / charCount / 替换点上下文快照）或结构化错误。</returns>
    [McpServerTool(Name = "save_template_edit", Title = "局部编辑模板", Destructive = false, Idempotent = true)]
    [Description("用途：小块替换原语——对模板文件正文做子串级单处精确替换，改几行免整篇覆盖重传。\n"
        + "何时用：只改模板局部；配合 read_template 的 contentHash 做并发安全的条件写。\n"
        + "参数：package(string,必填)=目标用户模板包包名；path(string,必填)=模板相对包根路径；"
        + "oldText(string,必填非空)=待替换原文子串，须在正文中恰好出现一次（0 次报未找到、多次歧义保护并报命中次数；CRLF/LF 差异不阻断匹配）；"
        + "newText(string,必填,允许空串=删除)=替换后子串，行尾落地遵循宿主文件；"
        + "expectedContentHash(string,可选,默认 null)=条件写期望摘要（取自 read_template 的 contentHash），不匹配拒绝并回显当前摘要。\n"
        + "返回：package、action=edited、path、fileCount、contentHash（编辑后新摘要，供后续条件写复用）、charCount、contextBefore/contextAfter（替换点前后上下文快照）。\n"
        + "常见错误：TemplatePackageError（内置包只读；包不存在）；NotFound（oldText 未找到）；InvalidArgument（oldText 多次命中、路径校验失败）；ContentConflict（摘要不匹配）；InternalError。")]
    public async Task<object> SaveTemplateEditAsync(
        [Description("目标用户模板包包名（必填）。")]
        string package,
        [Description("模板文件相对包根路径（必填）。")]
        string path,
        [Description("待替换原文子串（必填非空串）：须在正文中恰好出现一次，多命中将歧义保护拒绝；CRLF/LF 行尾差异不阻断匹配。")]
        string oldText,
        [Description("替换后子串（必填，允许空串）：空串表示删除该子串；行尾形态自动对齐宿主文件。")]
        string newText,
        [Description("条件写期望的当前内容摘要（可选，默认 null；SHA256 前 16 位 hex 小写，取自 read_template 回执 contentHash）；缺省不校验。")]
        string? expectedContentHash = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidatePackageAndPath(package, path);
            ArgumentNullException.ThrowIfNull(oldText);
            ArgumentNullException.ThrowIfNull(newText);

            TemplatePackageOperationResult result = await _packageService.EditTemplateContentAsync(
                package.Trim(), NormalizePath(path), oldText, newText, expectedContentHash, cancellationToken);
            ToolErrorResult? error = FromOperationStatus(result, "save_template_edit");
            if (error is not null)
            {
                return error;
            }

            _logger.LogInformation(
                "save_template_edit 完成，包 {PackageName}，路径 {TemplatePath}，字符数 {CharCount}。",
                package, path, result.CharCount);
            return TemplateEditResult.Edited(
                result.Package!, NormalizePath(path), result.ContentHash!, result.CharCount!.Value, result.ContextBefore!, result.ContextAfter!);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "save_template_edit 执行失败，包名 {PackageName}，路径 {TemplatePath}。", package, path);
            return ToolError.From(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 向用户模板包新增一个空模板文件（建空文件并追加清单条目，enabled=true）；内置包只读拒绝。
    /// </summary>
    /// <param name="package">目标用户模板包包名。</param>
    /// <param name="path">新增模板文件相对包根路径，可含分组目录（如 controller/user.java.scriban）。</param>
    /// <param name="outputPath">新增模板的输出相对路径模板，支持 {{变量}} 占位与管道表达式（如 src/{{table.variableName}}/User.java）。</param>
    /// <param name="scope">可选模板作用域声明：table（缺省，按表集逐表渲染）/ module（按模块清单逐个渲染）/ project（整次生成渲染一次）；
    /// module/project 模板删除重建时必须显式携带本参数恢复作用域声明。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>新增成功摘要或结构化错误。</returns>
    [McpServerTool(Name = "create_template_file", Title = "新建模板文件", Destructive = false)]
    [Description("用途：向用户模板包新增一个空模板文件（enabled=true，正文随后用 save_template_content 写入）。\n"
        + "何时用：给用户包增加新模板；内置包只读需先用 copy_template_package 复制为可写用户包。\n"
        + "参数：package(string,必填)=目标用户模板包包名；path(string,必填)=新增模板相对路径，可含分组目录（如 controller/user.java.scriban）；"
        + "outputPath(string,必填)=输出相对路径模板，支持 {{变量}} 占位与管道表达式（渲染期合法化，字面量非法字符在生成侧产物路径校验拦截）；"
        + "scope(string,可选,默认 null=table)=模板作用域 table/module/project，非法取值结构化拒绝；module/project 模板删除重建时必须显式携带本参数恢复声明。\n"
        + "返回：package、action=created、path、fileCount。\n"
        + "常见错误：TemplatePackageError（内置包只读；同名文件已存在；包不存在）；InvalidArgument（outputPath 为空；scope 非法；路径穿越/绝对路径/盘符前缀校验失败）；InternalError。")]
    public async Task<object> CreateTemplateFileAsync(
        [Description("目标用户模板包包名（必填）。")]
        string package,
        [Description("新增模板文件相对包根路径（必填），可含分组目录（如 controller/user.java.scriban）。")]
        string path,
        [Description("输出相对路径模板（必填）：支持 {{变量}} 占位与管道表达式（如 src/{{table.variableName}}/User.java）。")]
        string outputPath,
        [Description("模板作用域声明：table（缺省，按表集逐表渲染）/ module（按模块清单逐个渲染）/ project（整次生成渲染一次）；缺省或空白不写清单字段（按缺省 table），非法取值结构化拒绝。")]
        string? scope = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidatePackageAndPath(package, path);
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("输出路径模板（outputPath）不能为空。");
            }

            TemplatePackageOperationResult result = await _packageService.AddTemplateFileAsync(
                package.Trim(), NormalizePath(path), outputPath.Trim(), cancellationToken, scope);
            ToolErrorResult? error = FromOperationStatus(result, "create_template_file");
            if (error is not null)
            {
                return error;
            }

            _logger.LogInformation("create_template_file 完成，包 {PackageName}，路径 {TemplatePath}，作用域 {Scope}。", package, path, scope ?? "（缺省 table）");
            return TemplateEditResult.Created(result.Package!, NormalizePath(path));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "create_template_file 执行失败，包名 {PackageName}，路径 {TemplatePath}。", package, path);
            return ToolError.From(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 从用户模板包删除一个模板文件（删文件并同步移除清单条目）；内置包只读拒绝，允许删除最后一个模板。
    /// </summary>
    /// <param name="package">目标用户模板包包名。</param>
    /// <param name="path">待删除模板文件相对包根路径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>删除成功摘要或结构化错误。</returns>
    [McpServerTool(Name = "delete_template_file", Title = "删除模板文件", Destructive = true)]
    [Description("用途：从用户模板包删除一个模板文件（物理删除并同步移除清单条目，不可恢复）。\n"
        + "何时用：确认模板不再需要时；调用前先 read_template 或确认包内容。\n"
        + "参数：package(string,必填)=目标用户模板包包名；path(string,必填)=待删除模板相对包根路径。\n"
        + "返回：package、action=deleted、path、fileCount（允许删除最后一个模板，删除后包变空包）。\n"
        + "常见错误：TemplatePackageError（内置包只读；包不存在）；InvalidArgument（路径校验失败；文件不存在）；InternalError。")]
    public async Task<object> DeleteTemplateFileAsync(
        [Description("目标用户模板包包名（必填）。")]
        string package,
        [Description("待删除模板文件相对包根路径（必填）。")]
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidatePackageAndPath(package, path);

            TemplatePackageOperationResult result = await _packageService.DeleteTemplateFileAsync(
                package.Trim(), NormalizePath(path), cancellationToken);
            ToolErrorResult? error = FromOperationStatus(result, "delete_template_file");
            if (error is not null)
            {
                return error;
            }

            _logger.LogInformation("delete_template_file 完成，包 {PackageName}，路径 {TemplatePath}。", package, path);
            return TemplateEditResult.Deleted(result.Package!, NormalizePath(path));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "delete_template_file 执行失败，包名 {PackageName}，路径 {TemplatePath}。", package, path);
            return ToolError.From(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 重命名用户模板包内的一个模板文件（物理改名并同步更新清单条目）；内置包只读拒绝。
    /// </summary>
    /// <param name="package">目标用户模板包包名。</param>
    /// <param name="path">当前模板文件相对包根路径。</param>
    /// <param name="newPath">新模板文件相对包根路径，可移动分组目录，须防目录穿越安全。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>重命名成功摘要或结构化错误。</returns>
    [McpServerTool(Name = "rename_template_file", Title = "重命名模板文件", Destructive = false)]
    [Description("用途：重命名/移动用户模板包内的一个模板文件（物理改名并同步更新清单条目）。\n"
        + "何时用：调整模板分组或文件名；newPath 可含新分组目录（移动分组）。\n"
        + "参数：package(string,必填)=目标用户模板包包名；path(string,必填)=当前模板相对路径；newPath(string,必填)=新模板相对路径，可含新分组目录。\n"
        + "返回：package、action=renamed、path（旧路径）、newPath（新路径，均正斜杠形态）、fileCount。\n"
        + "常见错误：TemplatePackageError（内置包只读；newPath 与既有文件同名冲突；包不存在）；InvalidArgument（newPath 为空；路径校验失败；源文件不存在）；InternalError。"
        + "注意：勾选记忆不迁移。")]
    public async Task<object> RenameTemplateFileAsync(
        [Description("目标用户模板包包名（必填）。")]
        string package,
        [Description("当前模板文件相对包根路径（必填）。")]
        string path,
        [Description("新模板文件相对包根路径（必填），可含新分组目录；须防目录穿越安全。")]
        string newPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidatePackageAndPath(package, path);
            if (string.IsNullOrWhiteSpace(newPath))
            {
                throw new ArgumentException("新模板路径（newPath）不能为空。");
            }

            TemplatePackageOperationResult result = await _packageService.RenameTemplateFileAsync(
                package.Trim(), NormalizePath(path), NormalizePath(newPath), cancellationToken);
            ToolErrorResult? error = FromOperationStatus(result, "rename_template_file");
            if (error is not null)
            {
                return error;
            }

            _logger.LogInformation(
                "rename_template_file 完成，包 {PackageName}，路径 {TemplatePath} → {NewTemplatePath}。",
                package, path, newPath);
            return TemplateEditResult.Renamed(result.Package!, NormalizePath(path), NormalizePath(newPath));
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception, "rename_template_file 执行失败，包名 {PackageName}，路径 {TemplatePath} → {NewTemplatePath}。",
                package, path, newPath);
            return ToolError.From(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 模板编辑类工具的公共入参校验：包名与模板路径必填非空白。
    /// </summary>
    /// <param name="package">模板包包名。</param>
    /// <param name="path">模板文件相对路径。</param>
    /// <exception cref="ArgumentException">包名或路径为空白时抛出。</exception>
    private static void ValidatePackageAndPath(string package, string path)
    {
        if (string.IsNullOrWhiteSpace(package))
        {
            throw new ArgumentException("模板包名（package）不能为空。");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("模板路径（path）不能为空。");
        }
    }

    /// <summary>
    /// 统一路径形态：去首尾空白并把反斜杠规范为正斜杠，与清单条目路径形态对齐；穿越与非法字符校验由 Core 层强制。
    /// </summary>
    /// <param name="path">调用方传入的模板路径。</param>
    /// <returns>规范化后的路径。</returns>
    private static string NormalizePath(string path)
    {
        return path.Trim().Replace('\\', '/');
    }

    /// <summary>
    /// 将模板包操作结果状态映射为结构化错误应答：成功返回 null；
    /// 内置只读与同名冲突归模板包域错误，校验失败（路径穿越、文件不存在等）归参数错误，
    /// 未找到归 NotFound，条件写摘要不一致归内容冲突，其余归内部错误。
    /// </summary>
    /// <param name="result">Core 模板包操作结果。</param>
    /// <param name="toolName">工具名，供日志定位。</param>
    /// <returns>失败时的结构化错误；成功时为 null。</returns>
    private ToolErrorResult? FromOperationStatus(TemplatePackageOperationResult result, string toolName)
    {
        if (result.Status == TemplatePackageOperationStatus.Succeeded)
        {
            return null;
        }

        string errorType = result.Status switch
        {
            TemplatePackageOperationStatus.BuiltinConflict => "TemplatePackageError",
            TemplatePackageOperationStatus.NameConflict => "TemplatePackageError",
            TemplatePackageOperationStatus.Invalid => "InvalidArgument",
            TemplatePackageOperationStatus.NotFound => "NotFound",
            TemplatePackageOperationStatus.ContentConflict => "ContentConflict",
            _ => "InternalError"
        };
        _logger.LogWarning("{ToolName} 被拒绝，状态 {Status}，原因：{Message}。", toolName, result.Status, result.Message);
        return new ToolErrorResult(ToolErrorResult.ErrorMarker, errorType, result.Message);
    }
}

/// <summary>
/// 模板编辑类工具的成功应答 DTO。
/// </summary>
/// <param name="Package">包名。</param>
/// <param name="Action">编辑动作：saved / created / deleted / renamed / edited。</param>
/// <param name="Path">目标模板相对路径（重命名为旧路径；统一正斜杠形态回显，与清单条目路径形态一致）。</param>
/// <param name="NewPath">重命名后的新路径（正斜杠形态）；非重命名动作为 null。</param>
/// <param name="FileCount">操作完成后包内模板文件数。</param>
/// <param name="ContentHash">局部编辑后的正文内容摘要（SHA256 前 16 位 hex 小写，供后续条件写复用）；非编辑动作为 null 且不序列化。</param>
/// <param name="CharCount">局部编辑后的正文字符数；非编辑动作为 null 且不序列化。</param>
/// <param name="ContextBefore">替换点之前上下文快照（约 60 字符）；非编辑动作为 null 且不序列化。</param>
/// <param name="ContextAfter">替换点之后上下文快照（约 60 字符）；非编辑动作为 null 且不序列化。</param>
internal sealed record TemplateEditResult(
    string Package,
    string Action,
    string Path,
    string? NewPath,
    int FileCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CharCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContextBefore = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContextAfter = null)
{
    /// <summary>
    /// 构造保存成功应答。
    /// </summary>
    /// <param name="package">操作后的包运行时信息。</param>
    /// <param name="path">已保存的模板相对路径。</param>
    /// <returns>保存成功应答。</returns>
    public static TemplateEditResult Saved(TemplatePackageInfo package, string path)
    {
        return new TemplateEditResult(package.Name, "saved", path, null, package.Files.Count);
    }

    /// <summary>
    /// 构造新增成功应答。
    /// </summary>
    /// <param name="package">操作后的包运行时信息。</param>
    /// <param name="path">新增的模板相对路径。</param>
    /// <returns>新增成功应答。</returns>
    public static TemplateEditResult Created(TemplatePackageInfo package, string path)
    {
        return new TemplateEditResult(package.Name, "created", path, null, package.Files.Count);
    }

    /// <summary>
    /// 构造删除成功应答。
    /// </summary>
    /// <param name="package">操作后的包运行时信息。</param>
    /// <param name="path">已删除的模板相对路径。</param>
    /// <returns>删除成功应答。</returns>
    public static TemplateEditResult Deleted(TemplatePackageInfo package, string path)
    {
        return new TemplateEditResult(package.Name, "deleted", path, null, package.Files.Count);
    }

    /// <summary>
    /// 构造重命名成功应答。
    /// </summary>
    /// <param name="package">操作后的包运行时信息。</param>
    /// <param name="oldPath">重命名前的模板相对路径。</param>
    /// <param name="newPath">重命名后的模板相对路径。</param>
    /// <returns>重命名成功应答。</returns>
    public static TemplateEditResult Renamed(TemplatePackageInfo package, string oldPath, string newPath)
    {
        return new TemplateEditResult(package.Name, "renamed", oldPath, newPath, package.Files.Count);
    }

    /// <summary>
    /// 构造局部编辑成功应答，携带替换后证据供调用方条件写与就地核对。
    /// </summary>
    /// <param name="package">操作后的包运行时信息。</param>
    /// <param name="path">已编辑的模板相对路径。</param>
    /// <param name="contentHash">编辑后的正文内容摘要（SHA256 前 16 位 hex 小写）。</param>
    /// <param name="charCount">编辑后的正文字符数。</param>
    /// <param name="contextBefore">替换点之前上下文快照（约 60 字符）。</param>
    /// <param name="contextAfter">替换点之后上下文快照（约 60 字符）。</param>
    /// <returns>局部编辑成功应答。</returns>
    public static TemplateEditResult Edited(
        TemplatePackageInfo package, string path, string contentHash, int charCount, string contextBefore, string contextAfter)
    {
        return new TemplateEditResult(
            package.Name, "edited", path, null, package.Files.Count, contentHash, charCount, contextBefore, contextAfter);
    }
}
