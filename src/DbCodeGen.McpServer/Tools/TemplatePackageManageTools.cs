using System.ComponentModel;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DbCodeGen.McpServer.Tools;

/// <summary>
/// 模板包管理工具组：新建、删除、重命名、元数据更新与复制模板包。
/// 全部转发 Core 模板包服务，内置包只读拒绝、跨进程互斥与防目录穿越均由 Core 服务层强制；
/// 桌面端不自动感知 MCP 侧的模板包变更，需要时在桌面端重新加载模板包列表。
/// </summary>
[McpServerToolType]
public sealed class TemplatePackageManageTools
{
    private readonly ITemplatePackageService _packageService;
    private readonly ILogger<TemplatePackageManageTools> _logger;

    /// <summary>
    /// 以模板包服务与日志器构造模板包管理工具组。
    /// </summary>
    /// <param name="packageService">模板包服务，承载包的增删改与全部安全校验。</param>
    /// <param name="logger">工具组日志器。</param>
    public TemplatePackageManageTools(ITemplatePackageService packageService, ILogger<TemplatePackageManageTools> logger)
    {
        _packageService = packageService;
        _logger = logger;
    }

    /// <summary>
    /// 新建用户模板包：写 template.json 清单（含包说明、基础包名与包级类型映射），可选顺带创建首个空模板文件；内置包只读，新建同名包拒绝。
    /// </summary>
    /// <param name="package">新模板包包名，须符合目录名规则。</param>
    /// <param name="description">包说明，可省略。</param>
    /// <param name="basePackage">基础包名，可省略；传空串清空。</param>
    /// <param name="typeMap">包级类型映射表（数据库原始类型 → 目标语言类型，如 {"bigint":"long"}），可省略；
    /// 空白键值项被跳过，键统一小写化落盘。该映射在生成时优先于全局映射表命中（解析链：包 typeMap &gt; 库专属 &gt; 通用 &gt; 兜底）。</param>
    /// <param name="firstTemplatePath">首模板文件相对包根路径，可含分组目录；与 firstOutputPath 须同时提供。</param>
    /// <param name="firstOutputPath">首模板文件输出相对路径模板，支持 {{变量}} 占位。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>创建成功摘要或结构化错误。</returns>
    [McpServerTool(Name = "create_template_package", Title = "新建模板包", Destructive = false)]
    [Description("新建用户模板包（写 template.json 清单，固定 scriban 引擎，可设包说明 description、基础包名 basePackage 与包级类型映射 typeMap（数据库原始类型→目标语言类型，生成时优先于全局映射表命中），basePackage 传空串表示清空）。可选传 firstTemplatePath+firstOutputPath 顺带创建首个空模板文件（两者须同时提供），正文随后用 save_template_content 写入；不传则创建空包。与内置包/既有用户包同名拒绝。")]
    public async Task<object> CreateTemplatePackageAsync(
        string package,
        string? description = null,
        string? basePackage = null,
        Dictionary<string, string>? typeMap = null,
        string? firstTemplatePath = null,
        string? firstOutputPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidatePackageName(package);

            TemplatePackageOperationResult result = await _packageService.CreatePackageAsync(
                package.Trim(), description ?? string.Empty, firstTemplatePath, firstOutputPath, cancellationToken, basePackage, typeMap);
            ToolErrorResult? error = FromOperationStatus(result, "create_template_package");
            if (error is not null)
            {
                return error;
            }

            _logger.LogInformation("create_template_package 完成，包 {PackageName}。", package);
            return PackageManageResult.Created(result.Package!);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "create_template_package 执行失败，包名 {PackageName}。", package);
            return ToolError.From(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 删除用户模板包：物理删除整个包目录，不可恢复；confirm 防误删闸必须显式传 true。
    /// </summary>
    /// <param name="package">待删除用户模板包包名。</param>
    /// <param name="confirm">删除确认开关，必须显式传 true 才执行。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>删除成功摘要或结构化错误。</returns>
    [McpServerTool(Name = "delete_template_package", Title = "删除模板包", Destructive = true)]
    [Description("删除用户模板包（物理删除整个包目录，不可恢复）。防误删保护：confirm 必须显式传 true 才执行；内置包只读拒绝删除。")]
    public async Task<object> DeleteTemplatePackageAsync(
        string package,
        bool confirm,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidatePackageName(package);

            // 防误删闸：确认开关未显式置位即拒绝，不进入 Core 删除流程
            if (!confirm)
            {
                throw new ArgumentException("删除确认未通过：confirm 必须显式传 true 才允许删除模板包（防误删保护）。");
            }

            await _packageService.DeletePackageAsync(package.Trim(), cancellationToken);

            _logger.LogInformation("delete_template_package 完成，包 {PackageName}。", package);
            return PackageManageResult.Deleted(package.Trim());
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "delete_template_package 执行失败，包名 {PackageName}。", package);
            return ToolError.From(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 重命名用户模板包：物理改目录名并同步重写清单包名；勾选/展示顺序记忆不迁移，需调用方自行重建。
    /// </summary>
    /// <param name="package">当前用户模板包包名。</param>
    /// <param name="newName">新模板包包名，须符合目录名规则。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>重命名成功摘要或结构化错误。</returns>
    [McpServerTool(Name = "rename_template_package", Title = "重命名模板包", Destructive = false)]
    [Description("重命名用户模板包（物理改目录名并同步重写清单包名）。勾选记忆不迁移：包展示顺序记忆与模板勾选记忆不随改名迁移，与 rename_template_file 文件级先例一致；桌面端旧名记忆条目需自行重建。内置包只读拒绝；新名与既有包同名拒绝。")]
    public async Task<object> RenameTemplatePackageAsync(
        string package,
        string newName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidatePackageName(package);
            if (string.IsNullOrWhiteSpace(newName))
            {
                throw new ArgumentException("新包名（newName）不能为空。");
            }

            TemplatePackageOperationResult result = await _packageService.RenamePackageAsync(
                package.Trim(), newName.Trim(), cancellationToken);
            ToolErrorResult? error = FromOperationStatus(result, "rename_template_package");
            if (error is not null)
            {
                return error;
            }

            _logger.LogInformation("rename_template_package 完成，包 {PackageName} → {NewName}。", package, newName);
            return PackageManageResult.Renamed(package.Trim(), result.Package!);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "rename_template_package 执行失败，包名 {PackageName}，新名 {NewName}。", package, newName);
            return ToolError.From(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 更新用户模板包元数据（包说明、基础包名与包级类型映射）：只传需要修改的字段，未传字段保持不变；内置包只读拒绝。
    /// </summary>
    /// <param name="package">目标用户模板包包名。</param>
    /// <param name="description">新包说明；省略表示不改动，传空串清空。</param>
    /// <param name="basePackage">新基础包名；省略表示不改动，传空串清空。</param>
    /// <param name="typeMap">新包级类型映射表（数据库原始类型 → 目标语言类型）；省略表示不改动，
    /// 传空对象 {} 清空映射表。该映射在生成时优先于全局映射表命中（解析链：包 typeMap &gt; 库专属 &gt; 通用 &gt; 兜底）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>更新成功摘要或结构化错误。</returns>
    [McpServerTool(Name = "update_template_package_metadata", Title = "更新模板包元数据", Destructive = false, Idempotent = true)]
    [Description("更新用户模板包元数据（包说明 description、基础包名 basePackage 与包级类型映射 typeMap）：只传需要修改的字段，未传字段保持不变；basePackage 传空串表示清空；typeMap 传空对象 {} 表示清空映射表，非空对象整体覆盖（键为数据库原始类型、值为目标语言类型，生成时优先于全局映射表命中）。内置包只读拒绝。")]
    public async Task<object> UpdateTemplatePackageMetadataAsync(
        string package,
        string? description = null,
        string? basePackage = null,
        Dictionary<string, string>? typeMap = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidatePackageName(package);

            TemplatePackageOperationResult result = await _packageService.UpdatePackageMetadataAsync(
                package.Trim(), description, basePackage, cancellationToken, typeMap);
            ToolErrorResult? error = FromOperationStatus(result, "update_template_package_metadata");
            if (error is not null)
            {
                return error;
            }

            _logger.LogInformation(
                "update_template_package_metadata 完成，包 {PackageName}，说明={Description}，基础包={BasePackage}，类型映射={TypeMapCount}。",
                package, description, basePackage, typeMap?.Count);
            return PackageManageResult.MetadataUpdated(result.Package!);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "update_template_package_metadata 执行失败，包名 {PackageName}。", package);
            return ToolError.From(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 复制模板包为新用户包：内置包可一键复制为可写用户包后自定义修改；内置包只读，复制目标同名拒绝。
    /// </summary>
    /// <param name="sourcePackage">源模板包包名（内置包或用户包均可）。</param>
    /// <param name="newName">新用户包包名，须符合目录名规则。</param>
    /// <param name="overwrite">是否允许覆盖同名用户包。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>复制成功摘要或结构化错误。</returns>
    [McpServerTool(Name = "copy_template_package", Title = "复制模板包", Destructive = true)]
    [Description("复制模板包为新用户包：内置包可一键复制为可写用户包后自定义修改。新包名与内置包同名拒绝；与用户包同名需 overwrite=true 覆盖确认。")]
    public async Task<object> CopyTemplatePackageAsync(
        string sourcePackage,
        string newName,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidatePackageName(sourcePackage);
            if (string.IsNullOrWhiteSpace(newName))
            {
                throw new ArgumentException("新包名（newName）不能为空。");
            }

            TemplatePackageOperationResult result = await _packageService.CopyPackageAsync(
                sourcePackage.Trim(), newName.Trim(), overwrite, cancellationToken);
            ToolErrorResult? error = FromOperationStatus(result, "copy_template_package");
            if (error is not null)
            {
                return error;
            }

            _logger.LogInformation(
                "copy_template_package 完成，源包 {SourcePackage} → 新包 {NewName}，覆盖={Overwrite}。",
                sourcePackage, newName, overwrite);
            return PackageManageResult.Copied(sourcePackage.Trim(), result.Package!);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception, "copy_template_package 执行失败，源包 {SourcePackage}，新名 {NewName}。", sourcePackage, newName);
            return ToolError.From(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 包管理类工具的公共入参校验：包名必填非空白。
    /// </summary>
    /// <param name="package">模板包包名。</param>
    /// <exception cref="ArgumentException">包名为空白时抛出。</exception>
    private static void ValidatePackageName(string package)
    {
        if (string.IsNullOrWhiteSpace(package))
        {
            throw new ArgumentException("模板包名（package）不能为空。");
        }
    }

    /// <summary>
    /// 将模板包操作结果状态映射为结构化错误应答：成功返回 null；
    /// 内置只读与同名冲突归模板包域错误，校验失败（包名不合法等）归参数错误，其余归内部错误。
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
/// 模板包管理类工具的成功应答 DTO。
/// </summary>
/// <param name="Package">操作目标包名（重命名为旧包名，复制为源包名）。</param>
/// <param name="Action">管理动作：created / deleted / renamed / metadata-updated / copied。</param>
/// <param name="NewName">操作后的新包名（重命名/复制）；其余动作为 null。</param>
/// <param name="Description">操作后包说明；删除动作无包信息为 null。</param>
/// <param name="BasePackage">操作后基础包名；删除动作无包信息为 null。</param>
/// <param name="FileCount">操作完成后包内模板文件数；删除动作无包信息为 null。</param>
internal sealed record PackageManageResult(
    string Package,
    string Action,
    string? NewName,
    string? Description,
    string? BasePackage,
    int? FileCount)
{
    /// <summary>
    /// 构造创建成功应答。
    /// </summary>
    /// <param name="package">新建的包运行时信息。</param>
    /// <returns>创建成功应答。</returns>
    public static PackageManageResult Created(TemplatePackageInfo package)
    {
        return new PackageManageResult(package.Name, "created", null, package.Description, package.BasePackage, package.Files.Count);
    }

    /// <summary>
    /// 构造删除成功应答。
    /// </summary>
    /// <param name="packageName">已删除的包名。</param>
    /// <returns>删除成功应答。</returns>
    public static PackageManageResult Deleted(string packageName)
    {
        return new PackageManageResult(packageName, "deleted", null, null, null, null);
    }

    /// <summary>
    /// 构造重命名成功应答。
    /// </summary>
    /// <param name="oldName">重命名前的包名。</param>
    /// <param name="package">重命名后的包运行时信息。</param>
    /// <returns>重命名成功应答。</returns>
    public static PackageManageResult Renamed(string oldName, TemplatePackageInfo package)
    {
        return new PackageManageResult(oldName, "renamed", package.Name, package.Description, package.BasePackage, package.Files.Count);
    }

    /// <summary>
    /// 构造元数据更新成功应答。
    /// </summary>
    /// <param name="package">更新后的包运行时信息。</param>
    /// <returns>元数据更新成功应答。</returns>
    public static PackageManageResult MetadataUpdated(TemplatePackageInfo package)
    {
        return new PackageManageResult(package.Name, "metadata-updated", null, package.Description, package.BasePackage, package.Files.Count);
    }

    /// <summary>
    /// 构造复制成功应答。
    /// </summary>
    /// <param name="sourceName">源包名。</param>
    /// <param name="package">复制出的新包运行时信息。</param>
    /// <returns>复制成功应答。</returns>
    public static PackageManageResult Copied(string sourceName, TemplatePackageInfo package)
    {
        return new PackageManageResult(sourceName, "copied", package.Name, package.Description, package.BasePackage, package.Files.Count);
    }
}
