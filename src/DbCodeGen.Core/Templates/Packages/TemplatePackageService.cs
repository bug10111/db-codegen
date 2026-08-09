using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DbCodeGen.Core.Config;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板包管理服务实现，单例注入。承载列表加载、单包加载、zip/文件夹导入、复制、导出、删除，
/// 以及新建包、新增模板文件与删除模板文件，
/// 内置包只读边界、zip 防穿越与解压上限、变更操作串行化均在此实现。
/// </summary>
public sealed class TemplatePackageService : ITemplatePackageService, IDisposable
{
    /// <summary>
    /// zip 解压条目数上限（防 zip bomb）。
    /// </summary>
    internal const int MaxZipEntries = 200;

    /// <summary>
    /// zip 单条解压大小上限（10MB，防 zip bomb）。
    /// </summary>
    internal const int MaxSingleEntryBytes = 10 * 1024 * 1024;

    /// <summary>
    /// zip 解压总大小上限（50MB，防 zip bomb）。
    /// </summary>
    internal const long MaxTotalBytes = 50L * 1024 * 1024;

    private readonly IConfigService _configService;
    private readonly ILogger<TemplatePackageService> _logger;
    private readonly string _builtinRootPath;
    private readonly string _importTempRoot;
    private readonly PackageOperationGate _gate = new();

    /// <summary>
    /// 创建模板包管理服务实例。
    /// </summary>
    /// <param name="configService">配置服务，用于读取模板搜索目录。</param>
    /// <param name="logger">模板包服务日志器。</param>
    /// <param name="builtinRootPath">内置包根目录；为空时默认应用基目录\Templates\Builtin。</param>
    /// <param name="importTempRootOverride">zip 导入临时目录根；为空时默认 %TEMP%\DbCodeGen\Imports。</param>
    /// <exception cref="ArgumentNullException">configService 或 logger 为 null 时抛出。</exception>
    public TemplatePackageService(
        IConfigService configService,
        ILogger<TemplatePackageService> logger,
        string? builtinRootPath = null,
        string? importTempRootOverride = null)
    {
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(logger);
        _configService = configService;
        _logger = logger;
        _builtinRootPath = Path.GetFullPath(builtinRootPath ?? BuiltinTemplatePackages.GetDefaultRootPath());
        _importTempRoot = importTempRootOverride ?? Path.Combine(Path.GetTempPath(), "DbCodeGen", "Imports");
    }

    /// <summary>
    /// 释放串行门等非托管资源。
    /// </summary>
    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TemplatePackageInfo>> ListPackagesAsync(CancellationToken cancellationToken)
    {
        var packages = new List<TemplatePackageInfo>();

        // 内置包优先收集，单包异常不中断整体列表
        await CollectPackagesFromDirectoryAsync(_builtinRootPath, isBuiltin: true, packages, cancellationToken).ConfigureAwait(false);

        // 用户包按配置的模板搜索目录收集
        foreach (string searchDirectory in GetUserTemplateDirectories())
        {
            await CollectPackagesFromDirectoryAsync(searchDirectory, isBuiltin: false, packages, cancellationToken).ConfigureAwait(false);
        }

        // 默认排序：内置包优先，组内按包名字符串升序（大小写不敏感），保证 UI 稳定
        IOrderedEnumerable<TemplatePackageInfo> ordered = packages
            .OrderByDescending(package => package.IsBuiltin)
            .ThenBy(package => package.Name, StringComparer.OrdinalIgnoreCase);
        return ordered.ToList();
    }

    /// <inheritdoc />
    public async Task<TemplatePackageInfo> LoadPackageAsync(string packageName, CancellationToken cancellationToken)
    {
        if (!TemplatePackageLoader.IsValidPackageName(packageName))
        {
            throw new TemplatePackageException($"包名不合法：{packageName}");
        }

        // 内置包目录优先查找
        string builtinDirectory = Path.Combine(_builtinRootPath, packageName);
        if (Directory.Exists(builtinDirectory))
        {
            return await TemplatePackageLoader.LoadFromDirectoryAsync(builtinDirectory, isBuiltin: true, cancellationToken).ConfigureAwait(false);
        }

        // 再在用户模板搜索目录中查找
        foreach (string searchDirectory in GetUserTemplateDirectories())
        {
            string userDirectory = Path.Combine(searchDirectory, packageName);
            if (Directory.Exists(userDirectory))
            {
                return await TemplatePackageLoader.LoadFromDirectoryAsync(userDirectory, isBuiltin: false, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TemplatePackageException($"模板包不存在：{packageName}");
    }

    /// <inheritdoc />
    public async Task<TemplatePackageOperationResult> ImportFromZipAsync(string zipPath, bool overwrite, CancellationToken cancellationToken)
    {
        return await _gate.ExecuteExclusiveAsync(
            token => ImportFromZipCoreAsync(zipPath, overwrite, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TemplatePackageOperationResult> ImportFromFolderAsync(string folderPath, bool overwrite, CancellationToken cancellationToken)
    {
        return await _gate.ExecuteExclusiveAsync(
            token => ImportFromFolderCoreAsync(folderPath, overwrite, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TemplatePackageOperationResult> CopyPackageAsync(string sourceName, string newName, bool overwrite, CancellationToken cancellationToken)
    {
        return await _gate.ExecuteExclusiveAsync(
            token => CopyPackageCoreAsync(sourceName, newName, overwrite, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> ExportToZipAsync(string packageName, string targetZipPath, CancellationToken cancellationToken)
    {
        TemplatePackageInfo package;
        try
        {
            package = await LoadPackageAsync(packageName, cancellationToken).ConfigureAwait(false);
        }
        catch (TemplatePackageException exception)
        {
            throw new TemplatePackageException($"导出模板包失败：{exception.Message}", exception);
        }

        if (string.IsNullOrWhiteSpace(targetZipPath))
        {
            throw new TemplatePackageException("目标 zip 路径为空。");
        }

        string fullTargetPath = Path.GetFullPath(targetZipPath);
        string? targetDirectory = Path.GetDirectoryName(fullTargetPath);
        if (!string.IsNullOrEmpty(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        using FileStream zipStream = new(fullTargetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using ZipArchive archive = new(zipStream, ZipArchiveMode.Create, leaveOpen: false);

        // 写入 manifest 原文件（UTF-8），保证导出 zip 可被本服务直接重新导入
        ZipArchiveEntry manifestEntry = archive.CreateEntry(TemplatePackageLoader.ManifestFileName);
        await using (Stream manifestTarget = manifestEntry.Open())
        {
            byte[] manifestBytes = await File.ReadAllBytesAsync(package.ManifestPath, cancellationToken).ConfigureAwait(false);
            await manifestTarget.WriteAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
        }

        // 写入包内全部模板文件，条目路径使用正斜杠并保持包内相对结构
        foreach (TemplateFileInfo file in package.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string entryName = file.RelativeTemplatePath.Replace('\\', '/');
            ZipArchiveEntry entry = archive.CreateEntry(entryName);
            await using Stream entryTarget = entry.Open();
            await using FileStream source = new(file.TemplatePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await source.CopyToAsync(entryTarget, cancellationToken).ConfigureAwait(false);
        }

        return fullTargetPath;
    }

    /// <inheritdoc />
    public async Task DeletePackageAsync(string packageName, CancellationToken cancellationToken)
    {
        await _gate.ExecuteExclusiveAsync(
            token => DeletePackageCoreAsync(packageName, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TemplatePackageOperationResult> CreatePackageAsync(
        string packageName, string description, string? firstTemplatePath, string? firstOutputPath, CancellationToken cancellationToken)
    {
        return await _gate.ExecuteExclusiveAsync(
            token => CreatePackageCoreAsync(packageName, description, firstTemplatePath, firstOutputPath, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TemplatePackageOperationResult> AddTemplateFileAsync(
        string packageName, string templateRelativePath, string outputPath, CancellationToken cancellationToken)
    {
        return await _gate.ExecuteExclusiveAsync(
            token => AddTemplateFileCoreAsync(packageName, templateRelativePath, outputPath, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TemplatePackageOperationResult> DeleteTemplateFileAsync(
        string packageName, string templateRelativePath, CancellationToken cancellationToken)
    {
        return await _gate.ExecuteExclusiveAsync(
            token => DeleteTemplateFileCoreAsync(packageName, templateRelativePath, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 新建包核心流程：校验包名与首文件路径（可空）→ 内置包同名只读拒绝 → 用户包同名冲突拒绝 →
    /// 建目录写清单（首文件为空则写空 files 清单、不建物理文件）→ 重新校验返回。
    /// </summary>
    private async Task<TemplatePackageOperationResult> CreatePackageCoreAsync(
        string packageName, string description, string? firstTemplatePath, string? firstOutputPath, CancellationToken cancellationToken)
    {
        if (!TemplatePackageLoader.IsValidPackageName(packageName))
        {
            return TemplatePackageOperationResult.Invalid($"包名不合法（须为字母/数字/中划线/下划线且不含路径分隔符或 ..）：{packageName}");
        }

        // 首模板文件路径可空：为空创建空包，非空则须通过安全校验，把"未提供首文件"与"路径不合法"区分开
        bool hasFirstFile = !string.IsNullOrWhiteSpace(firstTemplatePath);
        string? normalizedTemplate = null;
        string? normalizedOutput = null;
        if (hasFirstFile)
        {
            normalizedTemplate = NormalizeSafeRelativePath(firstTemplatePath);
            if (normalizedTemplate is null)
            {
                return TemplatePackageOperationResult.Invalid($"首模板文件路径不合法（禁止绝对路径、.. 段与盘符前缀）：{firstTemplatePath}");
            }

            normalizedOutput = NormalizeSafeRelativePath(firstOutputPath);
            if (normalizedOutput is null)
            {
                return TemplatePackageOperationResult.Invalid($"输出路径不合法（禁止绝对路径、.. 段与盘符前缀）：{firstOutputPath}");
            }
        }

        // 与内置包同名：内置包只读，新建直接拒绝，不进入覆盖流程
        if (PackageNameExists(_builtinRootPath, packageName))
        {
            return TemplatePackageOperationResult.BuiltinReadonly($"内置包 {packageName} 只读，不可覆盖或新建同名包。");
        }

        string? userLibraryRoot = GetUserLibraryRoot();
        if (userLibraryRoot is null)
        {
            return TemplatePackageOperationResult.Failure("未配置可用的用户模板搜索目录，无法创建模板包。");
        }

        string targetDirectory = Path.Combine(userLibraryRoot, packageName);
        if (Directory.Exists(targetDirectory))
        {
            return TemplatePackageOperationResult.NameConflict($"同名用户包 {packageName} 已存在，新建不走覆盖，请更换包名。");
        }

        try
        {
            Directory.CreateDirectory(targetDirectory);

            // 写入 template.json 清单：固定 scriban 引擎；提供首文件时声明首模板条目，否则写空 files 清单
            var manifest = new TemplateManifest
            {
                Name = packageName,
                Description = description?.Trim() ?? string.Empty,
                Engine = TemplatePackageLoader.SupportedEngine,
                Files = hasFirstFile
                    ? new List<TemplateFileEntry>
                    {
                        new()
                        {
                            Template = normalizedTemplate!,
                            Output = normalizedOutput!,
                            Enabled = true
                        }
                    }
                    : new List<TemplateFileEntry>()
            };

            string manifestPath = Path.Combine(targetDirectory, TemplatePackageLoader.ManifestFileName);
            string json = JsonSerializer.Serialize(manifest, TemplatePackageLoader.JsonOptions);
            await File.WriteAllTextAsync(manifestPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);

            // 提供首文件时创建首模板空文件并自动建父目录（分组目录），保证清单 files 引用的文件真实存在
            if (hasFirstFile)
            {
                string templateFullPath = TemplatePackageLoader.ResolveWithinRoot(targetDirectory, normalizedTemplate!);
                string? parentDirectory = Path.GetDirectoryName(templateFullPath);
                if (!string.IsNullOrEmpty(parentDirectory))
                {
                    Directory.CreateDirectory(parentDirectory);
                }

                await File.WriteAllTextAsync(templateFullPath, string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
            }

            TemplatePackageInfo created = await TemplatePackageLoader.LoadFromDirectoryAsync(targetDirectory, isBuiltin: false, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("已创建用户模板包：{PackageName}，目录：{Directory}。", packageName, targetDirectory);
            return TemplatePackageOperationResult.Success(created);
        }
        catch (OperationCanceledException)
        {
            // 创建中途被取消时清理已建目录与文件，避免留下半成品模板包
            TryDeleteDirectory(targetDirectory);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TemplatePackageException)
        {
            TryDeleteDirectory(targetDirectory);
            _logger.LogError(exception, "创建模板包失败，目标目录：{TargetDirectory}。", targetDirectory);
            return TemplatePackageOperationResult.Failure($"创建模板包失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 新增文件核心流程：加载包并校验内置只读 → 路径校验 → 目标已存在拒绝 →
    /// 建空文件并追加 manifest 条目重写 → 重新校验返回。
    /// </summary>
    private async Task<TemplatePackageOperationResult> AddTemplateFileCoreAsync(
        string packageName, string templateRelativePath, string outputPath, CancellationToken cancellationToken)
    {
        TemplatePackageInfo package;
        try
        {
            package = await LoadPackageAsync(packageName, cancellationToken).ConfigureAwait(false);
        }
        catch (TemplatePackageException exception)
        {
            return TemplatePackageOperationResult.Failure($"模板包加载失败：{exception.Message}");
        }

        if (package.IsBuiltin)
        {
            return TemplatePackageOperationResult.BuiltinReadonly($"内置包 {packageName} 只读，不可新增模板。");
        }

        string? normalizedTemplate = NormalizeSafeRelativePath(templateRelativePath);
        string? normalizedOutput = NormalizeSafeRelativePath(outputPath);
        if (normalizedTemplate is null)
        {
            return TemplatePackageOperationResult.Invalid($"模板文件路径不合法（禁止绝对路径、.. 段与盘符前缀）：{templateRelativePath}");
        }

        if (normalizedOutput is null)
        {
            return TemplatePackageOperationResult.Invalid($"输出路径不合法（禁止绝对路径、.. 段与盘符前缀）：{outputPath}");
        }

        try
        {
            string templateFullPath = TemplatePackageLoader.ResolveWithinRoot(package.RootPath, normalizedTemplate);
            if (File.Exists(templateFullPath))
            {
                return TemplatePackageOperationResult.Failure($"模板文件已存在：{normalizedTemplate}");
            }

            // 建空模板文件并自动建父目录（分组目录）
            string? parentDirectory = Path.GetDirectoryName(templateFullPath);
            if (!string.IsNullOrEmpty(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            await File.WriteAllTextAsync(templateFullPath, string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);

            try
            {
                // 追加 manifest 条目并重写清单，保证清单与磁盘文件一致
                await AppendManifestFileEntryAsync(package, normalizedTemplate, normalizedOutput, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // 清单追加失败时回滚已建文件，避免孤儿文件残留；异常经外层 catch 统一收敛为失败结果
                TryDeleteFile(templateFullPath);
                throw;
            }

            TemplatePackageInfo updated = await TemplatePackageLoader.LoadFromDirectoryAsync(package.RootPath, isBuiltin: false, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("已新增模板文件，包 {PackageName}，相对路径 {RelativePath}。", packageName, normalizedTemplate);
            return TemplatePackageOperationResult.Success(updated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TemplatePackageException)
        {
            _logger.LogError(exception, "新增模板文件失败，包 {PackageName}，相对路径 {RelativePath}。", packageName, normalizedTemplate);
            return TemplatePackageOperationResult.Failure($"新增模板失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 删除文件核心流程：加载包并校验内置只读 → 文件不存在拒绝 →
    /// 删除文件并移除 manifest 条目重写 → 重新校验返回。允许删除最后一个模板，包可变为空包。
    /// </summary>
    private async Task<TemplatePackageOperationResult> DeleteTemplateFileCoreAsync(
        string packageName, string templateRelativePath, CancellationToken cancellationToken)
    {
        TemplatePackageInfo package;
        try
        {
            package = await LoadPackageAsync(packageName, cancellationToken).ConfigureAwait(false);
        }
        catch (TemplatePackageException exception)
        {
            return TemplatePackageOperationResult.Failure($"模板包加载失败：{exception.Message}");
        }

        if (package.IsBuiltin)
        {
            return TemplatePackageOperationResult.BuiltinReadonly($"内置包 {packageName} 只读，不可删除模板。");
        }

        string? normalizedTemplate = NormalizeSafeRelativePath(templateRelativePath);
        if (normalizedTemplate is null)
        {
            return TemplatePackageOperationResult.Invalid($"模板文件路径不合法（禁止绝对路径、.. 段与盘符前缀）：{templateRelativePath}");
        }

        try
        {
            string templateFullPath = TemplatePackageLoader.ResolveWithinRoot(package.RootPath, normalizedTemplate);
            if (!File.Exists(templateFullPath))
            {
                return TemplatePackageOperationResult.Failure($"模板文件不存在：{normalizedTemplate}");
            }

            // 先移除 manifest 对应条目并写回，成功后再删文件，避免清单写失败留下损坏包；
            // 删除最后一个模板后包变为空包，空 files 清单由 loader 放行可重新校验
            await RemoveManifestFileEntryAsync(package, normalizedTemplate, cancellationToken).ConfigureAwait(false);
            File.Delete(templateFullPath);

            TemplatePackageInfo updated = await TemplatePackageLoader.LoadFromDirectoryAsync(package.RootPath, isBuiltin: false, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("已删除模板文件，包 {PackageName}，相对路径 {RelativePath}。", packageName, normalizedTemplate);
            return TemplatePackageOperationResult.Success(updated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TemplatePackageException)
        {
            _logger.LogError(exception, "删除模板文件失败，包 {PackageName}，相对路径 {RelativePath}。", packageName, normalizedTemplate);
            return TemplatePackageOperationResult.Failure($"删除模板失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 规范化并校验模板/输出相对路径骨架安全，返回规范化路径；不合法返回 null。
    /// 原始输入含绝对路径、盘符前缀或根标记时直接拒绝，避免规范化吞掉前导分隔符造成语义漂移；
    /// 规范化后逐段校验非法文件名字符，避免 Path.GetFullPath 抛出未处理异常并落盘脏数据。
    /// </summary>
    /// <param name="relativePath">待校验的相对路径，可为空（为空视为未提供路径，返回 null）。</param>
    /// <returns>规范化后的安全相对路径；为空或含绝对路径、盘符前缀、.. 段、非法字符时返回 null。</returns>
    private static string? NormalizeSafeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
        {
            return null;
        }

        string normalized = TemplatePackageLoader.NormalizeRelativePath(relativePath);
        if (normalized.Length == 0 || !TemplatePackageLoader.IsSafeRelativeSkeleton(normalized))
        {
            return null;
        }

        // 逐段校验非法文件名字符（含 | * ? < > 等与控制字符），防 Windows 路径解析抛未处理异常
        char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
        foreach (string segment in normalized.Split('/'))
        {
            if (segment.IndexOfAny(invalidFileNameChars) >= 0)
            {
                return null;
            }
        }

        return normalized;
    }

    /// <summary>
    /// 向包清单追加一个模板文件条目并重写 template.json，保持清单与磁盘文件一致。
    /// </summary>
    /// <param name="package">目标模板包运行时信息。</param>
    /// <param name="normalizedTemplate">规范化后的模板相对路径。</param>
    /// <param name="normalizedOutput">规范化后的输出相对路径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    private static async Task AppendManifestFileEntryAsync(
        TemplatePackageInfo package, string normalizedTemplate, string normalizedOutput, CancellationToken cancellationToken)
    {
        TemplateManifest manifest = await ReadManifestForUpdateAsync(package.ManifestPath, cancellationToken).ConfigureAwait(false);
        manifest.Files.Add(new TemplateFileEntry
        {
            Template = normalizedTemplate,
            Output = normalizedOutput,
            Enabled = true
        });

        await WriteManifestAsync(package.ManifestPath, manifest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 从包清单移除指定模板文件条目并重写 template.json，保持清单与磁盘文件一致。
    /// </summary>
    /// <param name="package">目标模板包运行时信息。</param>
    /// <param name="normalizedTemplate">规范化后的待移除模板相对路径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    private static async Task RemoveManifestFileEntryAsync(
        TemplatePackageInfo package, string normalizedTemplate, CancellationToken cancellationToken)
    {
        TemplateManifest manifest = await ReadManifestForUpdateAsync(package.ManifestPath, cancellationToken).ConfigureAwait(false);
        manifest.Files.RemoveAll(entry =>
            string.Equals(TemplatePackageLoader.NormalizeRelativePath(entry.Template), normalizedTemplate, StringComparison.OrdinalIgnoreCase));

        await WriteManifestAsync(package.ManifestPath, manifest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读取包清单用于更新，解析失败时抛模板包异常，不改变磁盘内容。
    /// </summary>
    /// <param name="manifestPath">template.json 清单路径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>可更新的清单模型。</returns>
    private static async Task<TemplateManifest> ReadManifestForUpdateAsync(string manifestPath, CancellationToken cancellationToken)
    {
        try
        {
            string json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<TemplateManifest>(json, TemplatePackageLoader.JsonOptions) ?? new TemplateManifest();
        }
        catch (JsonException exception)
        {
            throw new TemplatePackageException("更新模板包清单失败：清单解析异常。", exception);
        }
    }

    /// <summary>
    /// 以 UTF-8 无 BOM 编码写回模板包清单。
    /// </summary>
    /// <param name="manifestPath">template.json 清单路径。</param>
    /// <param name="manifest">待写回的清单模型。</param>
    /// <param name="cancellationToken">取消标记。</param>
    private static async Task WriteManifestAsync(string manifestPath, TemplateManifest manifest, CancellationToken cancellationToken)
    {
        string updated = JsonSerializer.Serialize(manifest, TemplatePackageLoader.JsonOptions);
        await File.WriteAllTextAsync(manifestPath, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// zip 导入核心流程：校验 zip 路径 → 安全解压到临时目录 → 加载校验 → 安装到用户模板库 → 清理临时目录。
    /// </summary>
    private async Task<TemplatePackageOperationResult> ImportFromZipCoreAsync(string zipPath, bool overwrite, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            return TemplatePackageOperationResult.Invalid("zip 文件不存在。");
        }

        string fullZipPath = Path.GetFullPath(zipPath);
        string tempDirectory = Path.Combine(_importTempRoot, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDirectory);
            await ExtractZipSafelyAsync(fullZipPath, tempDirectory, cancellationToken).ConfigureAwait(false);

            string manifestPath = Path.Combine(tempDirectory, TemplatePackageLoader.ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                return TemplatePackageOperationResult.Invalid("zip 根目录缺少 template.json 清单。");
            }

            TemplatePackageInfo validated;
            try
            {
                validated = await TemplatePackageLoader.LoadFromDirectoryAsync(tempDirectory, isBuiltin: false, cancellationToken).ConfigureAwait(false);
            }
            catch (TemplatePackageException exception)
            {
                return TemplatePackageOperationResult.Invalid($"模板包校验失败：{exception.Message}");
            }

            return await InstallToUserLibraryAsync(validated, overwrite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TemplatePackageException exception)
        {
            return TemplatePackageOperationResult.Invalid($"导入 zip 内容不合法：{exception.Message}");
        }
        catch (InvalidDataException exception)
        {
            _logger.LogError(exception, "zip 文件损坏或格式异常，路径：{ZipPath}。", fullZipPath);
            return TemplatePackageOperationResult.Failure($"zip 文件损坏或格式异常：{exception.Message}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "导入 zip 失败，路径：{ZipPath}。", fullZipPath);
            return TemplatePackageOperationResult.Failure($"导入 zip 失败：{exception.Message}");
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>
    /// 文件夹导入核心流程：校验目录与清单 → 加载校验 → 安装到用户模板库。
    /// </summary>
    private async Task<TemplatePackageOperationResult> ImportFromFolderCoreAsync(string folderPath, bool overwrite, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return TemplatePackageOperationResult.Invalid("模板包目录不存在。");
        }

        string fullFolderPath = Path.GetFullPath(folderPath);
        string manifestPath = Path.Combine(fullFolderPath, TemplatePackageLoader.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return TemplatePackageOperationResult.Invalid("目录缺少 template.json 清单。");
        }

        TemplatePackageInfo validated;
        try
        {
            validated = await TemplatePackageLoader.LoadFromDirectoryAsync(fullFolderPath, isBuiltin: false, cancellationToken).ConfigureAwait(false);
        }
        catch (TemplatePackageException exception)
        {
            return TemplatePackageOperationResult.Invalid($"模板包校验失败：{exception.Message}");
        }

        return await InstallToUserLibraryAsync(validated, overwrite, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 复制核心流程：校验新包名 → 加载源包 → 内置包同名只读拒绝 → 用户包同名冲突判断 → 复制并重写清单 → 重新校验。
    /// </summary>
    private async Task<TemplatePackageOperationResult> CopyPackageCoreAsync(string sourceName, string newName, bool overwrite, CancellationToken cancellationToken)
    {
        if (!TemplatePackageLoader.IsValidPackageName(newName))
        {
            return TemplatePackageOperationResult.Invalid($"新包名不合法（须为字母/数字/中划线/下划线且不含路径分隔符或 ..）：{newName}");
        }

        TemplatePackageInfo source;
        try
        {
            source = await LoadPackageAsync(sourceName, cancellationToken).ConfigureAwait(false);
        }
        catch (TemplatePackageException exception)
        {
            return TemplatePackageOperationResult.Failure($"源模板包不存在或加载失败：{exception.Message}");
        }

        // 新包名与内置包同名：内置包只读，拒绝覆盖，overwrite 不生效
        if (PackageNameExists(_builtinRootPath, newName))
        {
            return TemplatePackageOperationResult.BuiltinReadonly($"内置包 {newName} 只读，不可覆盖。");
        }

        string? userLibraryRoot = GetUserLibraryRoot();
        if (userLibraryRoot is null)
        {
            return TemplatePackageOperationResult.Failure("未配置可用的用户模板搜索目录，无法复制模板包。");
        }

        string targetDirectory = Path.Combine(userLibraryRoot, newName);
        string sourceRootFull = Path.GetFullPath(source.RootPath);
        string targetFull = Path.GetFullPath(targetDirectory);

        // 复制目标与源包为同一目录（用户包复制为自身同名）时直接重新校验返回，避免覆盖流程删除源包
        if (string.Equals(sourceRootFull, targetFull, StringComparison.OrdinalIgnoreCase))
        {
            TemplatePackageInfo alreadyCopied = await TemplatePackageLoader.LoadFromDirectoryAsync(targetDirectory, isBuiltin: false, cancellationToken).ConfigureAwait(false);
            return TemplatePackageOperationResult.Success(alreadyCopied);
        }

        // 复制目标位于源包目录内部时拒绝，避免目录递归复制自身导致无限展开
        if (IsSameOrChild(targetDirectory, source.RootPath))
        {
            return TemplatePackageOperationResult.Invalid("复制目标不能位于源模板包目录内部。");
        }

        if (Directory.Exists(targetDirectory) && !overwrite)
        {
            return TemplatePackageOperationResult.NameConflict($"同名用户包 {newName} 已存在，需确认覆盖。");
        }

        if (Directory.Exists(targetDirectory))
        {
            TryDeleteDirectory(targetDirectory);
        }

        try
        {
            Directory.CreateDirectory(targetDirectory);
            await CopyDirectoryAsync(source.RootPath, targetDirectory, cancellationToken).ConfigureAwait(false);

            // 重写清单包名为新包名，保持"包名=目录名"不变量，再整体重新校验
            await RewriteManifestNameAsync(targetDirectory, newName, cancellationToken).ConfigureAwait(false);
            TemplatePackageInfo copied = await TemplatePackageLoader.LoadFromDirectoryAsync(targetDirectory, isBuiltin: false, cancellationToken).ConfigureAwait(false);
            return TemplatePackageOperationResult.Success(copied);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TemplatePackageException)
        {
            TryDeleteDirectory(targetDirectory);
            _logger.LogError(exception, "复制模板包失败，目标目录：{TargetDirectory}。", targetDirectory);
            return TemplatePackageOperationResult.Failure($"复制模板包失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 删除核心流程：内置包只读拒绝，用户包物理删除，包不存在抛异常。
    /// </summary>
    private async Task DeletePackageCoreAsync(string packageName, CancellationToken cancellationToken)
    {
        // 删除流程为同步目录操作，保持异步签名供调用方统一等待，已完成任务等待后立即继续
        await Task.CompletedTask;

        if (PackageNameExists(_builtinRootPath, packageName))
        {
            throw new TemplatePackageException($"内置包 {packageName} 只读，禁止删除。");
        }

        foreach (string searchDirectory in GetUserTemplateDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string userDirectory = Path.Combine(searchDirectory, packageName);
            if (Directory.Exists(userDirectory))
            {
                TryDeleteDirectory(userDirectory);
                _logger.LogInformation("已删除用户模板包：{PackageName}，目录：{Directory}。", packageName, userDirectory);
                return;
            }
        }

        throw new TemplatePackageException($"模板包不存在：{packageName}");
    }

    /// <summary>
    /// 将校验通过的内容安装到用户模板库：内置包同名只读拒绝、用户包同名需覆盖确认，复制后重新单包校验。
    /// </summary>
    /// <param name="validated">已通过完整校验的模板包（源自临时目录或源文件夹）。</param>
    /// <param name="overwrite">是否允许覆盖同名用户包。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>安装操作结果。</returns>
    private async Task<TemplatePackageOperationResult> InstallToUserLibraryAsync(TemplatePackageInfo validated, bool overwrite, CancellationToken cancellationToken)
    {
        string packageName = validated.Name;

        // 目标为内置包同名：内置包只读，拒绝覆盖，overwrite 不生效
        if (PackageNameExists(_builtinRootPath, packageName))
        {
            return TemplatePackageOperationResult.BuiltinReadonly($"内置包 {packageName} 只读，不可覆盖或删除。");
        }

        string? userLibraryRoot = GetUserLibraryRoot();
        if (userLibraryRoot is null)
        {
            return TemplatePackageOperationResult.Failure("未配置可用的用户模板搜索目录，无法安装模板包。");
        }

        string targetDirectory = Path.Combine(userLibraryRoot, packageName);

        // 源与目标为同一目录（导入的文件夹已在用户库）时，直接重新校验返回，避免删源
        if (string.Equals(Path.GetFullPath(validated.RootPath), Path.GetFullPath(targetDirectory), StringComparison.OrdinalIgnoreCase))
        {
            TemplatePackageInfo alreadyInstalled = await TemplatePackageLoader.LoadFromDirectoryAsync(targetDirectory, isBuiltin: false, cancellationToken).ConfigureAwait(false);
            return TemplatePackageOperationResult.Success(alreadyInstalled);
        }

        // 安装目标位于源目录内部时拒绝，避免目录递归复制自身导致无限展开
        if (IsSameOrChild(targetDirectory, validated.RootPath))
        {
            return TemplatePackageOperationResult.Invalid("安装目标不能位于源模板包目录内部。");
        }

        if (Directory.Exists(targetDirectory) && !overwrite)
        {
            return TemplatePackageOperationResult.NameConflict($"同名用户包 {packageName} 已存在，需确认覆盖。");
        }

        if (Directory.Exists(targetDirectory))
        {
            TryDeleteDirectory(targetDirectory);
        }

        try
        {
            Directory.CreateDirectory(targetDirectory);
            await CopyDirectoryAsync(validated.RootPath, targetDirectory, cancellationToken).ConfigureAwait(false);

            // 复制后重新单包校验，确保落库内容与校验结果一致
            TemplatePackageInfo installed = await TemplatePackageLoader.LoadFromDirectoryAsync(targetDirectory, isBuiltin: false, cancellationToken).ConfigureAwait(false);
            return TemplatePackageOperationResult.Success(installed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TemplatePackageException)
        {
            TryDeleteDirectory(targetDirectory);
            _logger.LogError(exception, "安装模板包失败，目标目录：{TargetDirectory}。", targetDirectory);
            return TemplatePackageOperationResult.Failure($"安装模板包失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 安全解压 zip 到目标目录：逐条目校验防目录穿越（zip slip）、条目数与解压大小上限（zip bomb）。
    /// </summary>
    /// <param name="zipPath">zip 文件路径。</param>
    /// <param name="tempRoot">解压目标目录。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <exception cref="TemplatePackageException">条目路径穿越或解压超限时抛出。</exception>
    /// <exception cref="InvalidDataException">zip 结构损坏时抛出。</exception>
    private static async Task ExtractZipSafelyAsync(string zipPath, string tempRoot, CancellationToken cancellationToken)
    {
        using FileStream zipStream = new(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using ZipArchive archive = new(zipStream, ZipArchiveMode.Read, leaveOpen: false);

        long totalUncompressedBytes = 0;
        int entryCount = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 目录条目不参与计数与大小统计
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            entryCount++;
            if (entryCount > MaxZipEntries)
            {
                throw new TemplatePackageException($"zip 条目数超过上限 {MaxZipEntries}，已拒绝解压。");
            }

            // 单条解压大小与总大小上限，防 zip bomb 耗尽磁盘；未声明大小的条目同样拒绝，避免上限被绕过
            if (entry.Length < 0)
            {
                throw new TemplatePackageException($"zip 条目未声明解压大小：{entry.FullName}，已拒绝。");
            }

            if (entry.Length > MaxSingleEntryBytes)
            {
                throw new TemplatePackageException($"zip 单条解压大小超过上限 10MB：{entry.FullName}，已拒绝。");
            }

            totalUncompressedBytes += entry.Length;
            if (totalUncompressedBytes > MaxTotalBytes)
            {
                throw new TemplatePackageException("zip 解压总大小超过上限 50MB，已拒绝。");
            }

            // 条目名防目录穿越并解析到临时目录内绝对路径
            string targetPath = TemplatePackageLoader.ResolveWithinRoot(tempRoot, entry.FullName);
            string? parentDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            using Stream source = entry.Open();
            using FileStream destination = new(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 递归复制目录内容到目标目录，IO 走异步流。
    /// </summary>
    private static async Task CopyDirectoryAsync(string sourceDirectory, string targetDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (string file in Directory.EnumerateFiles(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(file);
            string targetFile = Path.Combine(targetDirectory, fileName);
            await using FileStream sourceStream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using FileStream targetStream = new(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await sourceStream.CopyToAsync(targetStream, cancellationToken).ConfigureAwait(false);
        }

        foreach (string subDirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directoryName = Path.GetFileName(subDirectory);
            await CopyDirectoryAsync(subDirectory, Path.Combine(targetDirectory, directoryName), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 重写目标包目录 template.json 的包名为新名，保持包名与目录名一致。
    /// </summary>
    private static async Task RewriteManifestNameAsync(string packageDirectory, string newName, CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(packageDirectory, TemplatePackageLoader.ManifestFileName);
        TemplateManifest manifest;
        try
        {
            string json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            manifest = JsonSerializer.Deserialize<TemplateManifest>(json, TemplatePackageLoader.JsonOptions) ?? new TemplateManifest();
        }
        catch (JsonException exception)
        {
            throw new TemplatePackageException("重写模板包清单失败：清单解析异常。", exception);
        }

        manifest.Name = newName;
        string updated = JsonSerializer.Serialize(manifest, TemplatePackageLoader.JsonOptions);
        await File.WriteAllTextAsync(manifestPath, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 从配置读取可用的用户模板搜索目录列表，过滤空值与不存在的目录。
    /// </summary>
    /// <returns>用户模板搜索目录绝对路径列表。</returns>
    private IReadOnlyList<string> GetUserTemplateDirectories()
    {
        AppConfig config = _configService.Load();
        var directories = new List<string>();
        if (config.TemplateSearchDirectories is null)
        {
            return directories;
        }

        foreach (string directory in config.TemplateSearchDirectories)
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                directories.Add(Path.GetFullPath(directory));
            }
        }

        return directories;
    }

    /// <summary>
    /// 从配置挑选并准备用户模板库根目录：优先使用第一个存在的搜索目录，否则创建第一个可写目录。
    /// </summary>
    /// <returns>用户模板库根目录绝对路径；不可用时返回 null。</returns>
    private string? GetUserLibraryRoot()
    {
        AppConfig config = _configService.Load();
        if (config.TemplateSearchDirectories is null || config.TemplateSearchDirectories.Count == 0)
        {
            return null;
        }

        string? firstUsable = null;
        foreach (string directory in config.TemplateSearchDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            string full = Path.GetFullPath(directory);
            if (IsSameOrChild(full, _builtinRootPath))
            {
                continue;
            }

            firstUsable ??= full;
            if (Directory.Exists(full))
            {
                return full;
            }
        }

        // 没有现成目录时尝试创建第一个可用搜索目录，避免将用户包落入内置包目录
        if (firstUsable is null)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(firstUsable);
            return Path.GetFullPath(firstUsable);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "创建用户模板库目录失败：{Directory}。", firstUsable);
            return null;
        }
    }

    /// <summary>
    /// 从指定根目录收集模板包，单包加载异常记录日志并跳过。
    /// </summary>
    private async Task CollectPackagesFromDirectoryAsync(string directory, bool isBuiltin, List<TemplatePackageInfo> result, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string packageDirectory in Directory.EnumerateDirectories(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 用户包收集时跳过内置包根目录及其中内容，避免与内置包重复
            if (!isBuiltin && IsSameOrChild(packageDirectory, _builtinRootPath))
            {
                continue;
            }

            try
            {
                TemplatePackageInfo package = await TemplatePackageLoader.LoadFromDirectoryAsync(packageDirectory, isBuiltin, cancellationToken).ConfigureAwait(false);
                result.Add(package);
            }
            catch (Exception exception) when (exception is TemplatePackageException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "模板包加载失败，目录：{PackageDirectory}。", packageDirectory);
            }
        }
    }

    /// <summary>
    /// 判断路径是否与另一路径相同或位于其内（大小写不敏感）。
    /// </summary>
    private static bool IsSameOrChild(string path, string parent)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断根目录下是否存在指定包名的包目录。
    /// </summary>
    private static bool PackageNameExists(string rootDirectory, string packageName)
    {
        string packageDirectory = Path.Combine(rootDirectory, packageName);
        return Directory.Exists(packageDirectory);
    }

    /// <summary>
    /// 尽力删除单个文件，失败仅记录日志不阻断主流程。
    /// </summary>
    /// <param name="filePath">待删除文件路径。</param>
    private void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "删除文件失败：{FilePath}。", filePath);
        }
    }

    /// <summary>
    /// 尽力删除目录（递归），失败仅记录日志不阻断主流程。
    /// </summary>
    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "删除目录失败：{Directory}。", directory);
        }
    }
}
