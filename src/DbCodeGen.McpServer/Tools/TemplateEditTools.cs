using System.ComponentModel;
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
    [McpServerTool(Name = "save_template_content")]
    [Description("保存用户模板包内既有模板文件的正文（整篇覆写，Scriban 源文本）。内置包只读拒绝；文件须已存在，新建文件用 create_template_file。")]
    public async Task<object> SaveTemplateContentAsync(
        string package,
        string path,
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
    /// 向用户模板包新增一个空模板文件（建空文件并追加清单条目，enabled=true）；内置包只读拒绝。
    /// </summary>
    /// <param name="package">目标用户模板包包名。</param>
    /// <param name="path">新增模板文件相对包根路径，可含分组目录（如 controller/user.java.scriban）。</param>
    /// <param name="outputPath">新增模板的输出相对路径模板，支持 {{变量}} 占位（如 src/{{table.variableName}}/User.java）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>新增成功摘要或结构化错误。</returns>
    [McpServerTool(Name = "create_template_file")]
    [Description("向用户模板包新增一个空模板文件（enabled=true，正文随后用 save_template_content 写入）。path 可含分组目录；outputPath 为输出相对路径模板，支持 {{变量}} 占位。")]
    public async Task<object> CreateTemplateFileAsync(
        string package,
        string path,
        string outputPath,
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
                package.Trim(), NormalizePath(path), outputPath.Trim(), cancellationToken);
            ToolErrorResult? error = FromOperationStatus(result, "create_template_file");
            if (error is not null)
            {
                return error;
            }

            _logger.LogInformation("create_template_file 完成，包 {PackageName}，路径 {TemplatePath}。", package, path);
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
    [McpServerTool(Name = "delete_template_file")]
    [Description("从用户模板包删除一个模板文件（物理删除并同步移除清单条目，不可恢复）。内置包只读拒绝；允许删除最后一个模板（包变空包）。")]
    public async Task<object> DeleteTemplateFileAsync(
        string package,
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
    [McpServerTool(Name = "rename_template_file")]
    [Description("重命名/移动用户模板包内的一个模板文件（物理改名并同步更新清单条目，勾选记忆不迁移）。内置包只读拒绝；newPath 可含新分组目录。")]
    public async Task<object> RenameTemplateFileAsync(
        string package,
        string path,
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
    /// 内置只读与同名冲突归模板包域错误，校验失败（路径穿越、文件不存在等）归参数错误，其余归内部错误。
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
/// <param name="Action">编辑动作：saved / created / deleted / renamed。</param>
/// <param name="Path">目标模板相对路径（重命名为旧路径；统一正斜杠形态回显，与清单条目路径形态一致）。</param>
/// <param name="NewPath">重命名后的新路径（正斜杠形态）；非重命名动作为 null。</param>
/// <param name="FileCount">操作完成后包内模板文件数。</param>
internal sealed record TemplateEditResult(
    string Package,
    string Action,
    string Path,
    string? NewPath,
    int FileCount)
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
}
