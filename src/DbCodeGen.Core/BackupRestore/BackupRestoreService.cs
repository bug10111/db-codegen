using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.Core.BackupRestore;

/// <summary>
/// 备份/恢复服务实现，单例注入。承载 .dbcg 备份打包与恢复还原：
/// 配置脱敏（PasswordCipher/ApiKeyEncrypted 一律不进入备份）、备份文件校验
/// （版本/格式/防目录穿越/zip bomb 上限）与用户包冲突确认均在此实现。
/// 备份与恢复操作经串行门互斥，日志不输出任何敏感字段。
/// </summary>
public sealed class BackupRestoreService : IBackupRestoreService, IDisposable
{
    /// <summary>
    /// 备份包清单文件名，位于 .dbcg 根目录。
    /// </summary>
    private const string ManifestEntryName = "manifest.json";

    /// <summary>
    /// 用户模板包在备份包内的目录前缀。
    /// </summary>
    private const string TemplatesPrefix = "templates/";

    /// <summary>
    /// 当前支持的备份文件格式版本。
    /// </summary>
    private const int SupportedManifestVersion = 1;

    private readonly IConfigService _configService;
    private readonly ITemplatePackageService _templatePackageService;
    private readonly ILogger<BackupRestoreService> _logger;
    private readonly string _defaultTemplateDirectory;
    private readonly string _restoreTempRoot;
    private readonly PackageOperationGate _gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// 创建备份/恢复服务实例。
    /// </summary>
    /// <param name="configService">配置服务，用于读取配置快照与恢复时写回非密文字段。</param>
    /// <param name="templatePackageService">模板包服务，用于备份时列出用户模板包。</param>
    /// <param name="logger">备份/恢复服务日志器，日志不得输出任何敏感字段。</param>
    /// <param name="restoreTempRootOverride">恢复临时目录根；为空时默认 %TEMP%\DbCodeGen\Restores。</param>
    /// <param name="defaultTemplateDirectoryOverride">默认模板库目录；为空时默认 %AppData%\DbCodeGen\Templates。</param>
    /// <exception cref="ArgumentNullException">configService、templatePackageService 或 logger 为 null 时抛出。</exception>
    public BackupRestoreService(
        IConfigService configService,
        ITemplatePackageService templatePackageService,
        ILogger<BackupRestoreService> logger,
        string? restoreTempRootOverride = null,
        string? defaultTemplateDirectoryOverride = null)
    {
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(templatePackageService);
        ArgumentNullException.ThrowIfNull(logger);
        _configService = configService;
        _templatePackageService = templatePackageService;
        _logger = logger;
        _restoreTempRoot = restoreTempRootOverride ?? Path.Combine(Path.GetTempPath(), "DbCodeGen", "Restores");
        _defaultTemplateDirectory = Path.GetFullPath(defaultTemplateDirectoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DbCodeGen",
            "Templates"));
    }

    /// <summary>
    /// 释放串行门持有的信号量资源。
    /// </summary>
    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <inheritdoc />
    public Task<BackupResult> CreateBackupAsync(string targetFilePath, CancellationToken cancellationToken)
    {
        return _gate.ExecuteExclusiveAsync(
            token => CreateBackupCoreAsync(targetFilePath, token),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<RestoreResult> RestoreBackupAsync(string backupFilePath, bool overwriteUserPackages, CancellationToken cancellationToken)
    {
        return _gate.ExecuteExclusiveAsync(
            token => RestoreBackupCoreAsync(backupFilePath, overwriteUserPackages, token),
            cancellationToken);
    }

    /// <summary>
    /// 备份核心流程：列出用户模板包 → 组装脱敏清单 → 打包 .dbcg 到临时文件后原子移动到目标路径。
    /// </summary>
    private async Task<BackupResult> CreateBackupCoreAsync(string targetFilePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetFilePath))
        {
            throw new BackupValidationException("备份文件目标路径不能为空。");
        }

        string fullTargetPath = Path.GetFullPath(targetFilePath);
        string? targetDirectory = Path.GetDirectoryName(fullTargetPath);
        if (!string.IsNullOrEmpty(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        IReadOnlyList<TemplatePackageInfo> packages = await _templatePackageService.ListPackagesAsync(cancellationToken).ConfigureAwait(false);

        // 只打包用户包，按包名去重避免同一包出现在多个搜索目录时产生重复条目
        IEnumerable<TemplatePackageInfo> nonBuiltinPackages = packages.Where(package => !package.IsBuiltin);
        IEnumerable<TemplatePackageInfo> distinctPackages = nonBuiltinPackages
            .GroupBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
        List<TemplatePackageInfo> userPackages = distinctPackages.ToList();

        BackupManifest manifest = BuildManifest(userPackages);
        string tempZipPath = fullTargetPath + ".tmp";
        try
        {
            await WriteBackupArchiveAsync(tempZipPath, manifest, userPackages, cancellationToken).ConfigureAwait(false);
            File.Move(tempZipPath, fullTargetPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "创建备份文件失败，目标路径：{TargetPath}。", fullTargetPath);
            throw new BackupValidationException($"创建备份文件失败：{exception.Message}", exception);
        }
        finally
        {
            // 无论成功失败或取消，均清理可能残留的临时文件
            TryDeleteFile(tempZipPath);
        }

        _logger.LogInformation("备份创建完成，用户模板包数量：{Count}，文件：{TargetPath}。", userPackages.Count, fullTargetPath);
        return new BackupResult
        {
            BackupFilePath = fullTargetPath,
            UserPackageCount = userPackages.Count,
            PackageNames = userPackages.Select(package => package.Name).ToList()
        };
    }

    /// <summary>
    /// 组装备份清单：固定当前备份格式版本、记录创建时间与应用版本，配置快照经脱敏转换。
    /// </summary>
    private BackupManifest BuildManifest(IReadOnlyList<TemplatePackageInfo> userPackages)
    {
        AppConfig config = _configService.Current;
        return new BackupManifest
        {
            Version = SupportedManifestVersion,
            CreatedAt = DateTime.Now,
            AppVersion = GetAppVersion(),
            PackageNames = userPackages.Select(package => package.Name).ToList(),
            Config = BuildConfigSnapshot(config)
        };
    }

    /// <summary>
    /// 读取当前应用版本号，优先取入口程序集版本，取不到时回退到本程序集版本。
    /// </summary>
    /// <returns>应用版本字符串。</returns>
    private static string GetAppVersion()
    {
        Version? version = Assembly.GetEntryAssembly()?.GetName().Version ?? Assembly.GetExecutingAssembly().GetName().Version;
        return version?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// 构造脱敏配置快照：只复制非密字段，密码与 apiKey 密文一律不进入快照，仅记录是否存在。
    /// </summary>
    private static BackupManifestConfig BuildConfigSnapshot(AppConfig config)
    {
        var dataSources = new List<BackupManifestConfig.DataSourceSnapshot>();
        foreach (DataSourceConfig dataSource in config.DataSources ?? new List<DataSourceConfig>())
        {
            // 跳过空条目与无名连接，保证快照列表干净可还原
            if (dataSource is null || string.IsNullOrWhiteSpace(dataSource.Name))
            {
                continue;
            }

            dataSources.Add(new BackupManifestConfig.DataSourceSnapshot
            {
                Name = dataSource.Name,
                Type = dataSource.Type,
                Host = dataSource.Host,
                Port = dataSource.Port,
                Database = dataSource.Database,
                UserId = dataSource.UserId,
                PasswordConfigured = !string.IsNullOrEmpty(dataSource.PasswordCipher),
                CreatedAt = dataSource.CreatedAt,
                UpdatedAt = dataSource.UpdatedAt
            });
        }

        return new BackupManifestConfig
        {
            Version = config.Version,
            WorkspaceRoot = config.WorkspaceRoot ?? string.Empty,
            LastRelativeOutputRoot = config.LastRelativeOutputRoot ?? string.Empty,
            LlmBaseUrl = config.Llm?.BaseUrl ?? string.Empty,
            LlmModel = config.Llm?.Model ?? string.Empty,
            LlmApiKeyConfigured = !string.IsNullOrEmpty(config.Llm?.ApiKeyEncrypted),
            TemplateSearchDirectories = (config.TemplateSearchDirectories ?? new List<string>())
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .ToList(),
            DataSources = dataSources,
            TypeMappings = BuildTypeMappingSnapshot(config.TypeMappings)
        };
    }

    /// <summary>
    /// 深拷贝全局类型映射表为快照，跳过非法条目，避免快照与配置实例共享集合引用。
    /// </summary>
    /// <param name="typeMappings">配置中的类型映射条目集合，可为空。</param>
    /// <returns>类型映射快照列表。</returns>
    private static List<TypeMappingEntry> BuildTypeMappingSnapshot(IEnumerable<TypeMappingEntry>? typeMappings)
    {
        var result = new List<TypeMappingEntry>();
        foreach (TypeMappingEntry entry in typeMappings ?? new List<TypeMappingEntry>())
        {
            // 跳过空条目与缺键缺值的非法条目，保证快照列表干净可还原
            if (entry is null || string.IsNullOrWhiteSpace(entry.DbType) || string.IsNullOrWhiteSpace(entry.TargetType))
            {
                continue;
            }

            result.Add(new TypeMappingEntry
            {
                DbType = entry.DbType,
                TargetType = entry.TargetType,
                Import = entry.Import,
                Remark = entry.Remark
            });
        }

        return result;
    }

    /// <summary>
    /// 将清单与全部用户模板包内容写入 .dbcg 压缩包：根目录 manifest.json + templates/&lt;包名&gt;/… 全量文件。
    /// </summary>
    private async Task WriteBackupArchiveAsync(
        string zipPath,
        BackupManifest manifest,
        IReadOnlyList<TemplatePackageInfo> packages,
        CancellationToken cancellationToken)
    {
        using FileStream zipStream = new(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using ZipArchive archive = new(zipStream, ZipArchiveMode.Create, leaveOpen: false);

        ZipArchiveEntry manifestEntry = archive.CreateEntry(ManifestEntryName);
        await using (Stream manifestTarget = manifestEntry.Open())
        {
            string manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
            await manifestTarget.WriteAsync(Encoding.UTF8.GetBytes(manifestJson), cancellationToken).ConfigureAwait(false);
        }

        // 逐用户包遍历包根下全部文件，以 templates/<包名>/<相对路径> 结构写入，保持可整体还原
        foreach (TemplatePackageInfo package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string packageRoot = Path.GetFullPath(package.RootPath);
            if (!Directory.Exists(packageRoot))
            {
                _logger.LogWarning("备份时跳过已不存在的模板包目录：{PackageName}。", package.Name);
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(packageRoot, file).Replace('\\', '/');
                string entryName = $"{TemplatesPrefix}{package.Name}/{relative}";
                ZipArchiveEntry entry = archive.CreateEntry(entryName);
                await using Stream entryTarget = entry.Open();
                await using FileStream source = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await source.CopyToAsync(entryTarget, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 恢复核心流程：校验并解压备份 → 检查同名冲突 → 还原用户包到默认模板库 → 还原配置非密文字段。
    /// </summary>
    private async Task<RestoreResult> RestoreBackupCoreAsync(string backupFilePath, bool overwriteUserPackages, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(backupFilePath) || !File.Exists(backupFilePath))
        {
            throw new BackupValidationException("备份文件不存在。");
        }

        string fullBackupPath = Path.GetFullPath(backupFilePath);
        string tempDirectory = Path.Combine(_restoreTempRoot, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDirectory);

            // 校验备份格式并安全解压到临时目录，返回校验通过的清单
            BackupManifest manifest = await ExtractAndValidateAsync(fullBackupPath, tempDirectory, cancellationToken).ConfigureAwait(false);

            // 还原前检查用户包同名冲突，未允许覆盖时返回需确认结果，不执行任何写盘
            IReadOnlyList<string> conflicts = FindExistingPackages(manifest.PackageNames);
            if (conflicts.Count > 0 && !overwriteUserPackages)
            {
                return RestoreResult.ConfirmationRequired(conflicts);
            }

            Directory.CreateDirectory(_defaultTemplateDirectory);

            // 逐包还原到默认模板库目录，源内容已在前置校验通过
            var restoredPackages = new List<string>(manifest.PackageNames.Count);
            foreach (string packageName in manifest.PackageNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string packageDirectory = Path.Combine(tempDirectory, "templates", packageName);
                await InstallPackageAsync(packageDirectory, _defaultTemplateDirectory, packageName, cancellationToken).ConfigureAwait(false);
                restoredPackages.Add(packageName);
            }

            // 还原配置非密文字段，清空密码与 apiKey 密文后经配置服务保存
            ApplyConfigSnapshot(manifest.Config);

            // 收集备份时已配置密码、恢复后需重输密码的数据源连接名
            var passwordRequiredDataSources = new List<string>();
            foreach (BackupManifestConfig.DataSourceSnapshot dataSource in manifest.Config.DataSources)
            {
                if (dataSource.PasswordConfigured)
                {
                    passwordRequiredDataSources.Add(dataSource.Name);
                }
            }

            _logger.LogInformation("备份恢复完成，还原用户模板包：{Count} 个。", restoredPackages.Count);
            return RestoreResult.Succeeded(restoredPackages, passwordRequiredDataSources, manifest.Config.LlmApiKeyConfigured);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BackupValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or TemplatePackageException or ConfigSaveException)
        {
            _logger.LogError(exception, "恢复备份失败，文件：{BackupPath}。", fullBackupPath);
            throw new BackupValidationException($"恢复备份失败：{exception.Message}", exception);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>
    /// 校验 .dbcg 备份包并安全解压：逐条目校验目录穿越与 zip bomb 上限（复用模板包导入校验思路），
    /// 解析并校验 manifest.json，随后逐模板包完整校验。
    /// </summary>
    private async Task<BackupManifest> ExtractAndValidateAsync(string zipPath, string tempDirectory, CancellationToken cancellationToken)
    {
        using FileStream zipStream = new(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using ZipArchive archive = new(zipStream, ZipArchiveMode.Read, leaveOpen: false);

        long totalUncompressedBytes = 0;
        int entryCount = 0;
        ZipArchiveEntry? manifestEntry = null;

        // 逐条目校验并解压：目录条目不参与统计，条目数/单条大小/总大小均设上限防 zip bomb
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            entryCount++;
            if (entryCount > TemplatePackageService.MaxZipEntries)
            {
                throw new BackupValidationException($"备份文件条目数超过上限 {TemplatePackageService.MaxZipEntries}，已拒绝恢复。");
            }

            // 未声明解压大小或单条超限均拒绝，避免绕过 zip bomb 上限
            if (entry.Length < 0)
            {
                throw new BackupValidationException($"备份条目未声明解压大小：{entry.FullName}，已拒绝。");
            }

            if (entry.Length > TemplatePackageService.MaxSingleEntryBytes)
            {
                throw new BackupValidationException($"备份单条解压大小超过上限：{entry.FullName}，已拒绝。");
            }

            totalUncompressedBytes += entry.Length;
            if (totalUncompressedBytes > TemplatePackageService.MaxTotalBytes)
            {
                throw new BackupValidationException("备份文件解压总大小超过上限，已拒绝恢复。");
            }

            // 顶层 manifest.json 单独记录并跳过解压
            if (string.Equals(entry.FullName, ManifestEntryName, StringComparison.Ordinal))
            {
                manifestEntry = entry;
                continue;
            }

            // 其余条目必须以 templates/ 开头，路径经防目录穿越校验解析到临时目录内
            if (!entry.FullName.StartsWith(TemplatesPrefix, StringComparison.Ordinal))
            {
                throw new BackupValidationException($"备份文件含非预期顶层条目：{entry.FullName}，已拒绝。");
            }

            string targetPath = TemplatePackageLoader.ResolveWithinRoot(tempDirectory, entry.FullName);
            string? parentDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            using Stream source = entry.Open();
            using FileStream destination = new(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        if (manifestEntry is null)
        {
            throw new BackupValidationException("备份文件缺少 manifest.json 清单。");
        }

        BackupManifest manifest = await ReadManifestAsync(manifestEntry, cancellationToken).ConfigureAwait(false);
        ValidateManifest(manifest);
        await ValidatePackagesAsync(manifest, tempDirectory, cancellationToken).ConfigureAwait(false);
        return manifest;
    }

    /// <summary>
    /// 从 zip 中读取并反序列化 manifest.json 清单。
    /// </summary>
    /// <param name="manifestEntry">备份包内 manifest.json 条目。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>解析出的备份清单。</returns>
    /// <exception cref="BackupValidationException">清单解析失败或内容为空时抛出。</exception>
    private static async Task<BackupManifest> ReadManifestAsync(ZipArchiveEntry manifestEntry, CancellationToken cancellationToken)
    {
        BackupManifest? manifest;
        try
        {
            await using Stream stream = manifestEntry.Open();
            cancellationToken.ThrowIfCancellationRequested();
            using StreamReader reader = new(stream, Encoding.UTF8);
            string json = await reader.ReadToEndAsync().ConfigureAwait(false);
            manifest = JsonSerializer.Deserialize<BackupManifest>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new BackupValidationException("备份清单 manifest.json 解析失败。", exception);
        }
        catch (InvalidDataException exception)
        {
            throw new BackupValidationException("备份清单 manifest.json 读取失败。", exception);
        }

        if (manifest is null)
        {
            throw new BackupValidationException("备份清单 manifest.json 内容为空。");
        }

        return manifest;
    }

    /// <summary>
    /// 校验清单版本兼容性，并兜底补齐可能缺失的集合，校验包名合法性。
    /// </summary>
    private static void ValidateManifest(BackupManifest manifest)
    {
        if (manifest.Version != SupportedManifestVersion)
        {
            throw new BackupValidationException($"不支持的备份文件版本：{manifest.Version}，当前支持版本 {SupportedManifestVersion}。");
        }

        manifest.PackageNames ??= new List<string>();
        manifest.Config ??= new BackupManifestConfig();
        manifest.Config.DataSources ??= new List<BackupManifestConfig.DataSourceSnapshot>();
        manifest.Config.TemplateSearchDirectories ??= new List<string>();
        manifest.Config.TypeMappings ??= new List<TypeMappingEntry>();

        foreach (string packageName in manifest.PackageNames)
        {
            if (string.IsNullOrWhiteSpace(packageName) || !TemplatePackageLoader.IsValidPackageName(packageName))
            {
                throw new BackupValidationException($"备份清单含非法包名：{packageName}。");
            }
        }
    }

    /// <summary>
    /// 校验备份文件内解压出的模板包：清单声明的包必须存在且经模板包加载器完整校验，
    /// 存在清单未声明的孤儿包目录时拒绝，防止忽略多余内容。
    /// </summary>
    private async Task ValidatePackagesAsync(BackupManifest manifest, string tempDirectory, CancellationToken cancellationToken)
    {
        string templatesRoot = Path.Combine(tempDirectory, "templates");
        var manifestNames = new HashSet<string>(manifest.PackageNames, StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(templatesRoot))
        {
            foreach (string directory in Directory.EnumerateDirectories(templatesRoot))
            {
                string directoryName = Path.GetFileName(directory);
                if (!manifestNames.Contains(directoryName))
                {
                    throw new BackupValidationException($"备份文件含未在清单中声明的模板包目录：{directoryName}。");
                }
            }
        }

        // 逐包经模板包加载器完整校验，确保落库内容与校验结果一致
        foreach (string packageName in manifest.PackageNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string packageDirectory = Path.Combine(templatesRoot, packageName);
            if (!Directory.Exists(packageDirectory))
            {
                throw new BackupValidationException($"备份清单声明了模板包 {packageName}，但备份文件中缺少对应目录。");
            }

            try
            {
                TemplatePackageInfo validated = await TemplatePackageLoader.LoadFromDirectoryAsync(packageDirectory, isBuiltin: false, cancellationToken).ConfigureAwait(false);

                // 清单声明的包名必须与包目录名一致，保持"包名=目录名"不变量
                if (!string.Equals(validated.Name, packageName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new BackupValidationException($"模板包 {packageName} 清单声明的包名与目录不一致：{validated.Name}。");
                }
            }
            catch (TemplatePackageException exception)
            {
                throw new BackupValidationException($"模板包 {packageName} 校验失败：{exception.Message}", exception);
            }
        }
    }

    /// <summary>
    /// 检查默认模板库目录下是否存在同名用户模板包，返回冲突包名清单。
    /// </summary>
    private IReadOnlyList<string> FindExistingPackages(IReadOnlyList<string> packageNames)
    {
        var conflicts = new List<string>();
        foreach (string packageName in packageNames)
        {
            string targetDirectory = Path.Combine(_defaultTemplateDirectory, packageName);
            if (Directory.Exists(targetDirectory))
            {
                conflicts.Add(packageName);
            }
        }

        return conflicts;
    }

    /// <summary>
    /// 将单个已校验的模板包目录安装到目标模板库根目录，目标已存在时先删除再复制保证覆盖语义。
    /// </summary>
    private async Task InstallPackageAsync(string packageDirectory, string targetRoot, string packageName, CancellationToken cancellationToken)
    {
        string targetDirectory = Path.Combine(targetRoot, packageName);

        if (Directory.Exists(targetDirectory))
        {
            TryDeleteDirectory(targetDirectory);
        }

        await CopyDirectoryAsync(packageDirectory, targetDirectory, cancellationToken).ConfigureAwait(false);
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
    /// 将脱敏配置快照应用到当前配置并保存：还原非密文字段，密码与 apiKey 密文一律清空。
    /// </summary>
    private void ApplyConfigSnapshot(BackupManifestConfig snapshot)
    {
        AppConfig current = _configService.Current;
        current.Version = snapshot.Version > 0 ? snapshot.Version : 1;
        current.WorkspaceRoot = snapshot.WorkspaceRoot ?? string.Empty;
        current.LastRelativeOutputRoot = snapshot.LastRelativeOutputRoot ?? string.Empty;

        current.Llm ??= new LlmConfig();

        // LLM 端点与模型按备份还原，apiKey 密文一律清空并引导用户重新配置
        current.Llm.BaseUrl = string.IsNullOrWhiteSpace(snapshot.LlmBaseUrl) ? LlmConfig.DefaultBaseUrl : snapshot.LlmBaseUrl;
        current.Llm.Model = string.IsNullOrWhiteSpace(snapshot.LlmModel) ? LlmConfig.DefaultModel : snapshot.LlmModel;
        current.Llm.ApiKeyEncrypted = string.Empty;

        // 模板搜索目录按备份还原，并确保默认模板库目录在其中，保证还原的用户包可被发现
        current.TemplateSearchDirectories = MergeDefaultTemplateDirectory(snapshot.TemplateSearchDirectories).ToList();

        // 数据源按备份还原，密码密文一律置空
        var dataSources = new List<DataSourceConfig>();
        foreach (BackupManifestConfig.DataSourceSnapshot dataSource in snapshot.DataSources ?? new List<BackupManifestConfig.DataSourceSnapshot>())
        {
            if (string.IsNullOrWhiteSpace(dataSource.Name))
            {
                continue;
            }

            dataSources.Add(new DataSourceConfig
            {
                Name = dataSource.Name,
                Type = dataSource.Type,
                Host = dataSource.Host,
                Port = dataSource.Port,
                Database = dataSource.Database,
                UserId = dataSource.UserId,
                PasswordCipher = string.Empty,
                CreatedAt = dataSource.CreatedAt,
                UpdatedAt = dataSource.UpdatedAt
            });
        }

        current.DataSources = dataSources;

        // 类型映射按备份还原，仅保留合法的缺键缺值条目，保证还原后映射表可正常匹配
        current.TypeMappings = RestoreTypeMappings(snapshot.TypeMappings);
        _configService.Save();
    }

    /// <summary>
    /// 还原类型映射表：深拷贝快照中的合法条目并去首尾空白，跳过空条目与缺键缺值条目。
    /// </summary>
    /// <param name="snapshotTypeMappings">备份快照中的类型映射集合，可为空。</param>
    /// <returns>还原后的类型映射条目列表。</returns>
    private static List<TypeMappingEntry> RestoreTypeMappings(IEnumerable<TypeMappingEntry>? snapshotTypeMappings)
    {
        var result = new List<TypeMappingEntry>();
        foreach (TypeMappingEntry entry in snapshotTypeMappings ?? new List<TypeMappingEntry>())
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.DbType) || string.IsNullOrWhiteSpace(entry.TargetType))
            {
                continue;
            }

            result.Add(new TypeMappingEntry
            {
                DbType = entry.DbType.Trim(),
                TargetType = entry.TargetType.Trim(),
                Import = string.IsNullOrWhiteSpace(entry.Import) ? null : entry.Import.Trim(),
                Remark = entry.Remark
            });
        }

        return result;
    }

    /// <summary>
    /// 合并模板搜索目录：保留备份快照中的非空目录并去重，末尾确保默认模板库目录存在。
    /// </summary>
    private IReadOnlyList<string> MergeDefaultTemplateDirectory(IEnumerable<string>? directories)
    {
        var result = new List<string>();
        foreach (string directory in directories ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(directory) && !result.Contains(directory, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(directory);
            }
        }

        if (!result.Contains(_defaultTemplateDirectory, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(_defaultTemplateDirectory);
        }

        return result;
    }

    /// <summary>
    /// 尽力递归删除目录，失败仅记录日志不阻断主流程。
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

    /// <summary>
    /// 尽力删除残留临时文件，失败仅记录调试日志不阻断主流程。
    /// </summary>
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
            _logger.LogDebug(exception, "清理临时文件失败：{FilePath}。", filePath);
        }
    }
}
