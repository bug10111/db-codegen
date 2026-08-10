using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DbCodeGen.Core.Config;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板包管理服务实现，单例注入。承载列表加载、单包加载、zip/文件夹导入、复制、重命名、导出、删除，
/// 以及新建包、新增模板文件、批量追加模板文件、删除模板文件与模板文件重命名，
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

        // 包展示顺序记忆为空视为默认排序，保持"内置优先+包名升序"基线不变
        List<string> rememberedOrder = _configService.Load().TemplatePackageOrder;
        if (rememberedOrder.Count == 0)
        {
            return SortByDefaultOrder(packages);
        }

        // 记忆非空时按记忆覆盖默认排序：记忆内仍存在的包按记忆顺序前置，新包按默认规则追加末尾
        return SortByRememberedOrder(packages, rememberedOrder);
    }

    /// <summary>
    /// 按默认排序规则对包列表排序：内置包优先，组内按包名字符串升序（大小写不敏感），保证 UI 稳定。
    /// 包展示顺序记忆为空或不存在时使用，也是记忆模式下新包追加末尾的排序基线。
    /// </summary>
    /// <param name="packages">待排序的模板包集合。</param>
    /// <returns>按默认规则排序后的包列表。</returns>
    private static List<TemplatePackageInfo> SortByDefaultOrder(IEnumerable<TemplatePackageInfo> packages)
    {
        // 默认排序：内置包优先，组内按包名字符串升序（大小写不敏感），保证 UI 稳定
        IOrderedEnumerable<TemplatePackageInfo> ordered = packages
            .OrderByDescending(package => package.IsBuiltin)
            .ThenBy(package => package.Name, StringComparer.OrdinalIgnoreCase);
        return ordered.ToList();
    }

    /// <summary>
    /// 按包展示顺序记忆覆盖默认排序：记忆内仍存在的包按记忆顺序前置，
    /// 不在记忆内的新包按默认规则追加末尾，记忆中的失效包名（已删除的包）被过滤不进入列表。
    /// </summary>
    /// <param name="packages">已收集的全部模板包集合。</param>
    /// <param name="rememberedOrder">包展示顺序记忆，按展示顺序排列的包名清单。</param>
    /// <returns>按记忆覆盖排序后的包列表。</returns>
    private static List<TemplatePackageInfo> SortByRememberedOrder(IEnumerable<TemplatePackageInfo> packages, IReadOnlyList<string> rememberedOrder)
    {
        // 建立包名到包的映射，记忆匹配与去重统一按大小写不敏感比较，与包名升序基线一致
        var packagesByName = new Dictionary<string, TemplatePackageInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (TemplatePackageInfo package in packages)
        {
            packagesByName[package.Name] = package;
        }

        // 按记忆顺序收集仍存在的包：重复记忆只消费一次，失效包名（已删除）被过滤
        var remembered = new List<TemplatePackageInfo>();
        var processedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rememberedName in rememberedOrder)
        {
            if (!processedNames.Add(rememberedName))
            {
                continue;
            }

            if (packagesByName.TryGetValue(rememberedName, out TemplatePackageInfo? package))
            {
                remembered.Add(package);
            }
        }

        // 不在记忆内的新包按默认规则追加末尾，保证新建/导入的包始终可见且顺序稳定
        List<TemplatePackageInfo> newPackages = SortByDefaultOrder(
            packages.Where(package => !processedNames.Contains(package.Name)));
        remembered.AddRange(newPackages);
        return remembered;
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

    /// <inheritdoc />
    public async Task<TemplatePackageOperationResult> AppendTemplateFilesAsync(
        string packageName, IReadOnlyList<TemplateFileWriteEntry> files, CancellationToken cancellationToken)
    {
        return await _gate.ExecuteExclusiveAsync(
            token => AppendTemplateFilesCoreAsync(packageName, files, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TemplatePackageOperationResult> RenamePackageAsync(
        string packageName, string newName, CancellationToken cancellationToken)
    {
        return await _gate.ExecuteExclusiveAsync(
            token => RenamePackageCoreAsync(packageName, newName, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TemplatePackageOperationResult> RenameTemplateFileAsync(
        string packageName, string templateRelativePath, string newRelativePath, CancellationToken cancellationToken)
    {
        return await _gate.ExecuteExclusiveAsync(
            token => RenameTemplateFileCoreAsync(packageName, templateRelativePath, newRelativePath, token),
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

            normalizedOutput = NormalizeSafeOutputPath(firstOutputPath);
            if (normalizedOutput is null)
            {
                return TemplatePackageOperationResult.Invalid($"输出路径不合法（禁止绝对路径与盘符前缀，.. 仅限工作区根内）：{firstOutputPath}");
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
        string? normalizedOutput = NormalizeSafeOutputPath(outputPath);
        if (normalizedTemplate is null)
        {
            return TemplatePackageOperationResult.Invalid($"模板文件路径不合法（禁止绝对路径、.. 段与盘符前缀）：{templateRelativePath}");
        }

        if (normalizedOutput is null)
        {
            return TemplatePackageOperationResult.Invalid($"输出路径不合法（禁止绝对路径与盘符前缀，.. 仅限工作区根内）：{outputPath}");
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
    /// 重命名包核心流程：校验新包名 → 加载源包 → 内置包只读拒绝 → 新名与内置包/用户包冲突判断 →
    /// 物理改目录名 → 重写清单包名 → 重新校验返回；失败时已改目录尽力回滚。
    /// </summary>
    private async Task<TemplatePackageOperationResult> RenamePackageCoreAsync(
        string packageName, string newName, CancellationToken cancellationToken)
    {
        if (!TemplatePackageLoader.IsValidPackageName(newName))
        {
            return TemplatePackageOperationResult.Invalid($"新包名不合法（须为字母/数字/中划线/下划线且不含路径分隔符或 ..）：{newName}");
        }

        TemplatePackageInfo source;
        try
        {
            source = await LoadPackageAsync(packageName, cancellationToken).ConfigureAwait(false);
        }
        catch (TemplatePackageException exception)
        {
            return TemplatePackageOperationResult.Failure($"模板包不存在或加载失败：{exception.Message}");
        }

        // 内置包只读：不允许重命名
        if (source.IsBuiltin)
        {
            return TemplatePackageOperationResult.BuiltinReadonly($"内置包 {packageName} 只读，不可重命名。");
        }

        // 新名与内置包同名：内置包只读，拒绝重命名到该名称
        if (PackageNameExists(_builtinRootPath, newName))
        {
            return TemplatePackageOperationResult.BuiltinReadonly($"内置包 {newName} 只读，不可重命名到该名称。");
        }

        // 新旧同名视为成功原样返回，避免无意义重命名与覆盖自身目录
        if (string.Equals(source.Name, newName, StringComparison.OrdinalIgnoreCase))
        {
            TemplatePackageInfo unchanged = await TemplatePackageLoader.LoadFromDirectoryAsync(
                source.RootPath, isBuiltin: false, cancellationToken).ConfigureAwait(false);
            return TemplatePackageOperationResult.Success(unchanged);
        }

        // 新名在任一用户模板搜索目录已被占用时返回冲突，防跨目录同名包
        foreach (string searchDirectory in GetUserTemplateDirectories())
        {
            if (Directory.Exists(Path.Combine(searchDirectory, newName)))
            {
                return TemplatePackageOperationResult.NameConflict($"同名用户包 {newName} 已存在，请更换新包名。");
            }
        }

        // 在源包所在父目录内改名，目标目录即新包名目录
        string sourceDirectory = Path.GetFullPath(source.RootPath);
        string? parentDirectory = Path.GetDirectoryName(sourceDirectory);
        if (string.IsNullOrEmpty(parentDirectory))
        {
            return TemplatePackageOperationResult.Failure($"模板包目录无法定位：{packageName}");
        }

        string targetDirectory = Path.Combine(parentDirectory, newName);
        bool directoryMoved = false;
        try
        {
            Directory.Move(sourceDirectory, targetDirectory);
            directoryMoved = true;

            // 重写清单包名为新名，保持"包名=目录名"不变量，再整体重新校验
            await RewriteManifestNameAsync(targetDirectory, newName, cancellationToken).ConfigureAwait(false);
            TemplatePackageInfo renamed = await TemplatePackageLoader.LoadFromDirectoryAsync(
                targetDirectory, isBuiltin: false, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("已重命名模板包：{OldName} → {NewName}，目录 {Directory}。", packageName, newName, targetDirectory);
            return TemplatePackageOperationResult.Success(renamed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TemplatePackageException)
        {
            // 目录已改而清单重写失败时尽力回滚目录名，避免留下新目录名配旧清单的半成品包
            if (directoryMoved && Directory.Exists(targetDirectory) && !Directory.Exists(sourceDirectory))
            {
                TryMoveDirectoryBack(targetDirectory, sourceDirectory, packageName, newName);
            }

            _logger.LogError(exception, "重命名模板包失败，源包 {OldName}，新名 {NewName}。", packageName, newName);
            return TemplatePackageOperationResult.Failure($"重命名模板包失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 重命名文件核心流程：加载包并校验内置只读 → 新旧相对路径安全校验 → 旧文件存在、新文件不存在 →
    /// 物理改名文件 → 更新清单对应条目并重写 → 重新校验返回；失败时已改名文件尽力回滚。
    /// </summary>
    private async Task<TemplatePackageOperationResult> RenameTemplateFileCoreAsync(
        string packageName, string templateRelativePath, string newRelativePath, CancellationToken cancellationToken)
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
            return TemplatePackageOperationResult.BuiltinReadonly($"内置包 {packageName} 只读，不可重命名模板。");
        }

        string? normalizedOld = NormalizeSafeRelativePath(templateRelativePath);
        string? normalizedNew = NormalizeSafeRelativePath(newRelativePath);
        if (normalizedOld is null)
        {
            return TemplatePackageOperationResult.Invalid($"模板文件路径不合法（禁止绝对路径、.. 段与盘符前缀）：{templateRelativePath}");
        }

        if (normalizedNew is null)
        {
            return TemplatePackageOperationResult.Invalid($"新模板文件路径不合法（禁止绝对路径、.. 段与盘符前缀）：{newRelativePath}");
        }

        // 新旧路径相同视为成功原样返回，避免无意义重命名与覆盖自身
        if (string.Equals(normalizedOld, normalizedNew, StringComparison.OrdinalIgnoreCase))
        {
            return TemplatePackageOperationResult.Success(package);
        }

        try
        {
            string oldFullPath = TemplatePackageLoader.ResolveWithinRoot(package.RootPath, normalizedOld);
            string newFullPath = TemplatePackageLoader.ResolveWithinRoot(package.RootPath, normalizedNew);
            if (!File.Exists(oldFullPath))
            {
                return TemplatePackageOperationResult.Failure($"模板文件不存在：{normalizedOld}");
            }

            if (File.Exists(newFullPath))
            {
                return TemplatePackageOperationResult.Failure($"目标模板文件已存在：{normalizedNew}");
            }

            // 先物理改名文件并自动建目标父目录（分组目录），成功后再更新清单条目
            string? parentDirectory = Path.GetDirectoryName(newFullPath);
            if (!string.IsNullOrEmpty(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            File.Move(oldFullPath, newFullPath);

            try
            {
                await RenameManifestFileEntryAsync(package, normalizedOld, normalizedNew, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // 清单更新失败时回滚已改名文件，避免磁盘新名配旧清单的孤儿状态
                if (File.Exists(newFullPath) && !File.Exists(oldFullPath))
                {
                    TryMoveFileBack(newFullPath, oldFullPath, normalizedOld, normalizedNew);
                }

                throw;
            }

            TemplatePackageInfo updated = await TemplatePackageLoader.LoadFromDirectoryAsync(
                package.RootPath, isBuiltin: false, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "已重命名模板文件，包 {PackageName}，旧路径 {OldPath}，新路径 {NewPath}。", packageName, normalizedOld, normalizedNew);
            return TemplatePackageOperationResult.Success(updated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TemplatePackageException)
        {
            _logger.LogError(
                exception, "重命名模板文件失败，包 {PackageName}，旧路径 {OldPath}，新路径 {NewPath}。",
                packageName, normalizedOld, normalizedNew);
            return TemplatePackageOperationResult.Failure($"重命名模板失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 尽力将重命名中的包目录改回源目录名，回滚失败仅记录日志不阻断主流程。
    /// </summary>
    /// <param name="targetDirectory">已改名的目标目录。</param>
    /// <param name="sourceDirectory">源目录名。</param>
    /// <param name="oldName">源包名。</param>
    /// <param name="newName">新包名。</param>
    private void TryMoveDirectoryBack(string targetDirectory, string sourceDirectory, string oldName, string newName)
    {
        try
        {
            Directory.Move(targetDirectory, sourceDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception, "重命名模板包回滚目录失败，源包 {OldName}，新名 {NewName}。", oldName, newName);
        }
    }

    /// <summary>
    /// 尽力将重命名中的模板文件改回源路径，回滚失败仅记录日志不阻断主流程。
    /// </summary>
    /// <param name="newFullPath">已改名的新文件路径。</param>
    /// <param name="oldFullPath">源文件路径。</param>
    /// <param name="oldPath">源相对路径。</param>
    /// <param name="newPath">新相对路径。</param>
    private void TryMoveFileBack(string newFullPath, string oldFullPath, string oldPath, string newPath)
    {
        try
        {
            File.Move(newFullPath, oldFullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception, "重命名模板文件回滚失败，旧路径 {OldPath}，新路径 {NewPath}。", oldPath, newPath);
        }
    }

    /// <summary>
    /// 将包清单中指定模板条目更新为新相对路径并重写 template.json，保持清单与磁盘文件一致。
    /// </summary>
    /// <param name="package">目标模板包运行时信息。</param>
    /// <param name="oldPath">重命名前的模板相对路径。</param>
    /// <param name="newPath">重命名后的模板相对路径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    private static async Task RenameManifestFileEntryAsync(
        TemplatePackageInfo package, string oldPath, string newPath, CancellationToken cancellationToken)
    {
        TemplateManifest manifest = await ReadManifestForUpdateAsync(package.ManifestPath, cancellationToken).ConfigureAwait(false);
        foreach (TemplateFileEntry entry in manifest.Files)
        {
            if (string.Equals(TemplatePackageLoader.NormalizeRelativePath(entry.Template), oldPath, StringComparison.OrdinalIgnoreCase))
            {
                entry.Template = newPath;
            }
        }

        await WriteManifestAsync(package.ManifestPath, manifest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 批量追加核心流程：加载包并校验内置只读 → 逐条目路径安全与已存在预检（全部通过才落盘）→
    /// 写全部文件 → 批量追加 manifest 条目一次写回 → 重载返回；任一步失败回滚已写文件，不留半成品。
    /// </summary>
    private async Task<TemplatePackageOperationResult> AppendTemplateFilesCoreAsync(
        string packageName, IReadOnlyList<TemplateFileWriteEntry> files, CancellationToken cancellationToken)
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
            return TemplatePackageOperationResult.BuiltinReadonly($"内置包 {packageName} 只读，不可追加模板。");
        }

        if (files is null || files.Count == 0)
        {
            return TemplatePackageOperationResult.Invalid("待追加的模板文件列表为空。");
        }

        var normalizedEntries = new List<TemplateFileWriteEntry>(files.Count);
        var writtenFiles = new List<string>();

        // 逐条目规范化并校验模板与输出路径安全，任一非法整体拒绝，不进入写盘
        foreach (TemplateFileWriteEntry entry in files)
        {
            if (entry is null)
            {
                return TemplatePackageOperationResult.Invalid("待追加的模板文件条目存在空条目。");
            }

            string? normalizedTemplate = NormalizeSafeRelativePath(entry.RelativePath);
            string? normalizedOutput = NormalizeSafeOutputPath(entry.OutputPath);
            if (normalizedTemplate is null)
            {
                return TemplatePackageOperationResult.Invalid($"模板文件路径不合法（禁止绝对路径、.. 段与盘符前缀）：{entry.RelativePath}");
            }

            if (normalizedOutput is null)
            {
                return TemplatePackageOperationResult.Invalid($"输出路径不合法（禁止绝对路径与盘符前缀，.. 仅限工作区根内）：{entry.OutputPath}");
            }

            normalizedEntries.Add(new TemplateFileWriteEntry(normalizedTemplate, normalizedOutput, entry.Content ?? string.Empty, entry.Enabled));
        }

        try
        {
            // 预检全部目标模板文件不存在且批内无重名，任一已存在或重名则整体拒绝，保证全部通过才进入写盘
            var seenTemplatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TemplateFileWriteEntry entry in normalizedEntries)
            {
                if (!seenTemplatePaths.Add(entry.RelativePath))
                {
                    return TemplatePackageOperationResult.Invalid($"待追加条目模板路径重复：{entry.RelativePath}");
                }

                string templateFullPath = TemplatePackageLoader.ResolveWithinRoot(package.RootPath, entry.RelativePath);
                if (File.Exists(templateFullPath))
                {
                    return TemplatePackageOperationResult.Failure($"模板文件已存在：{entry.RelativePath}");
                }
            }

            // 全部预检通过后写文件：自动建父目录（分组目录），UTF-8 无 BOM
            foreach (TemplateFileWriteEntry entry in normalizedEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string templateFullPath = TemplatePackageLoader.ResolveWithinRoot(package.RootPath, entry.RelativePath);
                string? parentDirectory = Path.GetDirectoryName(templateFullPath);
                if (!string.IsNullOrEmpty(parentDirectory))
                {
                    Directory.CreateDirectory(parentDirectory);
                }

                await File.WriteAllTextAsync(templateFullPath, entry.Content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
                writtenFiles.Add(templateFullPath);
            }

            // 批量追加 manifest 条目并一次写回，保持清单与磁盘文件一致
            await AppendManifestEntriesAsync(package, normalizedEntries, cancellationToken).ConfigureAwait(false);

            // 重载更新后包并返回成功结果
            TemplatePackageInfo updated = await TemplatePackageLoader.LoadFromDirectoryAsync(package.RootPath, isBuiltin: false, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("已向模板包 {PackageName} 批量追加模板文件 {FileCount} 个。", packageName, normalizedEntries.Count);
            return TemplatePackageOperationResult.Success(updated);
        }
        catch (OperationCanceledException)
        {
            // 中途取消时回滚已写文件，避免半成品残留
            RollbackWrittenFiles(writtenFiles);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TemplatePackageException)
        {
            // 写盘或清单写回中途失败时回滚已写文件，不留半成品
            RollbackWrittenFiles(writtenFiles);
            _logger.LogError(exception, "批量追加模板文件失败，包 {PackageName}。", packageName);
            return TemplatePackageOperationResult.Failure($"批量追加模板失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 向包清单批量追加模板文件条目并一次重写 template.json，保持清单与磁盘文件一致。
    /// </summary>
    /// <param name="package">目标模板包运行时信息。</param>
    /// <param name="entries">规范化后的待追加写入条目集合。</param>
    /// <param name="cancellationToken">取消标记。</param>
    private static async Task AppendManifestEntriesAsync(
        TemplatePackageInfo package,
        IReadOnlyList<TemplateFileWriteEntry> entries,
        CancellationToken cancellationToken)
    {
        TemplateManifest manifest = await ReadManifestForUpdateAsync(package.ManifestPath, cancellationToken).ConfigureAwait(false);
        foreach (TemplateFileWriteEntry entry in entries)
        {
            manifest.Files.Add(new TemplateFileEntry
            {
                Template = entry.RelativePath,
                Output = entry.OutputPath,
                Enabled = entry.Enabled
            });
        }

        await WriteManifestAsync(package.ManifestPath, manifest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 尽力删除已写模板文件，回滚失败的批量追加操作，失败仅记录日志不阻断主流程。
    /// </summary>
    /// <param name="writtenFiles">已写文件绝对路径集合。</param>
    private void RollbackWrittenFiles(IReadOnlyList<string> writtenFiles)
    {
        foreach (string filePath in writtenFiles)
        {
            TryDeleteFile(filePath);
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
    /// 规范化并校验输出相对路径骨架安全（允许 .. 段），返回规范化路径；不合法返回 null。
    /// 原始输入含绝对路径、盘符前缀或根标记时直接拒绝，避免规范化吞掉前导分隔符造成语义漂移；
    /// 规范化后逐段校验非法文件名字符，避免 Path.GetFullPath 抛出未处理异常并落盘脏数据。
    /// .. 段允许存在，最终由生成侧解析时限定在工作区根内，可越出代码根落到资源目录。
    /// </summary>
    /// <param name="relativePath">待校验的输出相对路径，可为空（为空视为未提供路径，返回 null）。</param>
    /// <returns>规范化后的安全输出相对路径；为空或含绝对路径、盘符前缀、非法字符时返回 null。</returns>
    private static string? NormalizeSafeOutputPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
        {
            return null;
        }

        string normalized = TemplatePackageLoader.NormalizeRelativePath(relativePath);
        if (normalized.Length == 0 || !TemplatePackageLoader.IsSafeOutputPathSkeleton(normalized))
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
