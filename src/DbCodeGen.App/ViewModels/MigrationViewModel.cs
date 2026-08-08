using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbCodeGen.App.Services;
using DbCodeGen.App.Views;
using DbCodeGen.Core.BackupRestore;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.App.ViewModels;

/// <summary>
/// 迁移窗口视图模型，承载换电脑迁移场景的备份与恢复两页闭环：
/// 备份页选择 .dbcg 保存路径、预览将打包的用户模板包与配置摘要后执行备份；
/// 恢复页选择 .dbcg、校验并预览待还原包与同名冲突、经覆盖确认后执行还原，
/// 完成后提示需重输密码的数据源与需重配的 LLM apiKey，并引导打开数据源管理窗口。
/// 备份文件不含密码与密钥，恢复不写回密码与 apiKey，取消令牌贯穿备份恢复调用链。
/// </summary>
public sealed partial class MigrationViewModel : ObservableObject
{
    /// <summary>
    /// 备份包清单文件名，位于 .dbcg 根目录，与 Core 备份服务写入的清单名一致。
    /// </summary>
    private const string ManifestEntryName = "manifest.json";

    /// <summary>
    /// 当前支持的备份文件格式版本，与 Core BackupRestoreService 支持的版本保持一致。
    /// </summary>
    private const int SupportedBackupVersion = 1;

    private readonly IBackupRestoreService _backupRestoreService;
    private readonly ITemplatePackageService _templatePackageService;
    private readonly IConfigService _configService;
    private readonly IFilePickerService _filePickerService;
    private readonly IDialogService _dialogService;
    private readonly IConfirmDialogService _confirmDialogService;
    private readonly Func<DataSourceManagerWindow> _dataSourceManagerWindowFactory;
    private readonly ILogger<MigrationViewModel> _logger;
    private readonly Dispatcher _dispatcher;

    /// <summary>
    /// 在途备份/恢复操作的取消源，取消或关闭时取消本次未完成的调用。
    /// </summary>
    private CancellationTokenSource? _operationCts;

    /// <summary>
    /// 读取备份清单用的 JSON 选项，与 Core 备份服务序列化口径保持一致。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// 备份文件目标保存路径，供备份页选择后展示与执行备份。
    /// </summary>
    [ObservableProperty]
    private string _backupTargetPath = string.Empty;

    /// <summary>
    /// 备份页将打包的用户模板包清单（只含非内置包），供预览与执行备份。
    /// </summary>
    public ObservableCollection<BackupPackageRowViewModel> BackupPackageRows { get; } = new();

    /// <summary>
    /// 备份页配置摘要文本，展示数据源数量、LLM apiKey 状态、模板搜索目录数量与工作区根。
    /// </summary>
    [ObservableProperty]
    private string _backupSummaryText = string.Empty;

    /// <summary>
    /// 备份页是否已完成包清单与配置摘要预览，驱动预览区展示。
    /// </summary>
    [ObservableProperty]
    private bool _hasBackupPreview;

    /// <summary>
    /// 备份页状态栏文本，展示预览结果、备份进度与结果。
    /// </summary>
    [ObservableProperty]
    private string _backupStatusText = string.Empty;

    /// <summary>
    /// 恢复页选中的 .dbcg 备份文件路径。
    /// </summary>
    [ObservableProperty]
    private string _backupFilePath = string.Empty;

    /// <summary>
    /// 恢复页待还原的用户模板包名清单，来自备份清单。
    /// </summary>
    public ObservableCollection<string> RestorePackageNames { get; } = new();

    /// <summary>
    /// 恢复页检测到的同名用户模板包冲突名清单，还原前需覆盖确认。
    /// </summary>
    public ObservableCollection<string> ConflictPackageNames { get; } = new();

    /// <summary>
    /// 恢复页配置快照摘要文本，展示备份内数据源数量、LLM apiKey 状态等非密字段。
    /// </summary>
    [ObservableProperty]
    private string _restoreConfigSummaryText = string.Empty;

    /// <summary>
    /// 恢复页是否已完成校验与预览，驱动预览区展示。
    /// </summary>
    [ObservableProperty]
    private bool _hasRestorePreview;

    /// <summary>
    /// 恢复页是否检测到同名用户模板包冲突，驱动冲突区红色警示展示。
    /// </summary>
    [ObservableProperty]
    private bool _hasConflicts;

    /// <summary>
    /// 恢复页状态栏文本，展示校验结果、恢复进度与结果。
    /// </summary>
    [ObservableProperty]
    private string _restoreStatusText = string.Empty;

    /// <summary>
    /// 是否处于备份/恢复操作中，操作中禁用操作按钮与输入区并展示取消操作按钮。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshBackupPreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateBackupCommand))]
    [NotifyCanExecuteChangedFor(nameof(InspectBackupCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreBackupCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    /// <summary>
    /// 是否空闲可操作，供页面输入区整体禁用开关，操作进行中置假。
    /// </summary>
    public bool IsIdle => !IsBusy;

    /// <summary>
    /// 构造迁移窗口视图模型。
    /// </summary>
    /// <param name="backupRestoreService">备份/恢复服务，承载 .dbcg 打包与还原。</param>
    /// <param name="templatePackageService">模板包服务，备份预览时列出用户模板包。</param>
    /// <param name="configService">配置服务，读取备份页配置摘要与恢复后的当前配置。</param>
    /// <param name="filePickerService">文件选择服务，选择 .dbcg 备份目标与备份文件。</param>
    /// <param name="dialogService">消息提示服务，用于结果提示与错误反馈。</param>
    /// <param name="confirmDialogService">二次确认服务，用于覆盖冲突确认与恢复后引导确认。</param>
    /// <param name="dataSourceManagerWindowFactory">数据源管理窗口工厂，供恢复后引导重输密码打开。</param>
    /// <param name="logger">视图模型日志器，日志不输出任何密码或密钥。</param>
    /// <exception cref="ArgumentNullException">任一依赖参数为 null 时抛出。</exception>
    public MigrationViewModel(
        IBackupRestoreService backupRestoreService,
        ITemplatePackageService templatePackageService,
        IConfigService configService,
        IFilePickerService filePickerService,
        IDialogService dialogService,
        IConfirmDialogService confirmDialogService,
        Func<DataSourceManagerWindow> dataSourceManagerWindowFactory,
        ILogger<MigrationViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(backupRestoreService);
        ArgumentNullException.ThrowIfNull(templatePackageService);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(filePickerService);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(confirmDialogService);
        ArgumentNullException.ThrowIfNull(dataSourceManagerWindowFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _backupRestoreService = backupRestoreService;
        _templatePackageService = templatePackageService;
        _configService = configService;
        _filePickerService = filePickerService;
        _dialogService = dialogService;
        _confirmDialogService = confirmDialogService;
        _dataSourceManagerWindowFactory = dataSourceManagerWindowFactory;
        _logger = logger;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    /// <summary>
    /// 窗口呈现后初始化：自动刷新备份页预览，让用户打开即见可打包的用户模板包与配置摘要。
    /// </summary>
    public Task InitializeAsync()
    {
        return RefreshBackupPreviewAsync();
    }

    /// <summary>
    /// 取消在途备份/恢复操作，供窗口停止按钮或关闭时调用，核心调用链经取消令牌感知取消。
    /// </summary>
    public void CancelPendingOperation()
    {
        _operationCts?.Cancel();
    }

    /// <summary>
    /// 备份文件路径变化时清空恢复预览区，避免继续展示上一份文件的校验结果。
    /// </summary>
    partial void OnBackupFilePathChanged(string value)
    {
        HasRestorePreview = false;
        HasConflicts = false;
        RestorePackageNames.Clear();
        ConflictPackageNames.Clear();
        RestoreConfigSummaryText = string.Empty;
        RestoreStatusText = string.Empty;
    }

    /// <summary>
    /// 选择备份文件保存路径，供备份页浏览按钮触发。
    /// </summary>
    [RelayCommand]
    private async Task PickBackupTargetAsync()
    {
        string? path = await _filePickerService.PickSaveBackupAsync("DbCodeGen-backup.dbcg");
        if (path is null)
        {
            return;
        }

        BackupTargetPath = path;
    }

    /// <summary>
    /// 刷新备份页预览：列出用户模板包并汇总配置摘要，供用户确认备份内容。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private async Task RefreshBackupPreviewAsync()
    {
        CancellationTokenSource cts = BeginOperation();
        try
        {
            BackupStatusText = "正在扫描用户模板包…";
            IReadOnlyList<TemplatePackageInfo> packages = await _templatePackageService.ListPackagesAsync(cts.Token);

            // 只统计用户包，按包名去重，与 Core 备份打包口径保持一致
            IEnumerable<TemplatePackageInfo> nonBuiltinPackages = packages.Where(package => !package.IsBuiltin);
            IEnumerable<IGrouping<string, TemplatePackageInfo>> groups = nonBuiltinPackages
                .GroupBy(package => package.Name, StringComparer.OrdinalIgnoreCase);
            IEnumerable<TemplatePackageInfo> distinctPackages = groups.Select(group => group.First());
            List<TemplatePackageInfo> userPackages = distinctPackages.ToList();

            AppConfig config = _configService.Current;
            RunOnUiThread(() =>
            {
                BackupPackageRows.Clear();
                foreach (TemplatePackageInfo package in userPackages)
                {
                    BackupPackageRows.Add(new BackupPackageRowViewModel(package.Name, package.Description));
                }

                HasBackupPreview = true;
                BuildBackupSummaryText(config);
                BackupStatusText = userPackages.Count > 0
                    ? $"检测到 {userPackages.Count} 个用户模板包可打包。"
                    : "当前没有可打包的用户模板包。";
            });
        }
        catch (OperationCanceledException)
        {
            RunOnUiThread(() => BackupStatusText = "预览已取消。");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "刷新备份预览失败。");
            RunOnUiThread(() => BackupStatusText = "预览失败。");
            _dialogService.ShowError($"刷新备份预览失败：{exception.Message}", "备份");
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// 执行备份：经备份服务将用户模板包与脱敏配置快照打包为 .dbcg 文件。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private async Task CreateBackupAsync()
    {
        if (string.IsNullOrWhiteSpace(BackupTargetPath))
        {
            _dialogService.ShowInfo("请先选择备份文件保存路径。", "备份");
            return;
        }

        if (BackupPackageRows.Count == 0)
        {
            _dialogService.ShowInfo("没有可打包的用户模板包，请先在模板包管理中创建或导入模板包。", "备份");
            return;
        }

        CancellationTokenSource cts = BeginOperation();
        try
        {
            BackupStatusText = "正在创建备份…";
            BackupResult result = await _backupRestoreService.CreateBackupAsync(BackupTargetPath, cts.Token);
            RunOnUiThread(() =>
            {
                BackupStatusText = $"备份完成：{result.BackupFilePath}";
                HasBackupPreview = false;
                BackupPackageRows.Clear();
            });

            _dialogService.ShowInfo(
                $"备份完成。已打包用户模板包 {result.UserPackageCount} 个。\n\n文件：{result.BackupFilePath}\n\n"
                + "备份文件不含任何密码与 apiKey，请妥善保管。",
                "备份完成");
        }
        catch (OperationCanceledException)
        {
            RunOnUiThread(() => BackupStatusText = "备份已取消。");
        }
        catch (BackupValidationException exception)
        {
            _logger.LogError(exception, "创建备份失败。");
            RunOnUiThread(() => BackupStatusText = "备份失败。");
            _dialogService.ShowError($"备份失败：{exception.Message}", "备份失败");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "创建备份发生未预期异常。");
            RunOnUiThread(() => BackupStatusText = "备份失败。");
            _dialogService.ShowError($"备份失败：{exception.Message}", "备份失败");
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// 选择 .dbcg 备份文件，供恢复页浏览按钮触发。
    /// </summary>
    [RelayCommand]
    private async Task PickBackupFileAsync()
    {
        string? path = await _filePickerService.PickOpenBackupAsync();
        if (path is null)
        {
            return;
        }

        BackupFilePath = path;
    }

    /// <summary>
    /// 校验并预览恢复内容：读取备份清单、检测同名冲突并汇总配置快照摘要。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private async Task InspectBackupAsync()
    {
        if (string.IsNullOrWhiteSpace(BackupFilePath) || !File.Exists(BackupFilePath))
        {
            _dialogService.ShowInfo("请先选择备份文件。", "恢复");
            return;
        }

        CancellationTokenSource cts = BeginOperation();
        try
        {
            RestoreStatusText = "正在校验备份文件…";
            BackupManifest manifest = await ReadBackupManifestAsync(BackupFilePath, cts.Token);
            IReadOnlyList<string> conflicts = FindPackageConflicts(manifest.PackageNames);

            RunOnUiThread(() =>
            {
                RestorePackageNames.Clear();
                foreach (string packageName in manifest.PackageNames)
                {
                    RestorePackageNames.Add(packageName);
                }

                ConflictPackageNames.Clear();
                foreach (string conflictName in conflicts)
                {
                    ConflictPackageNames.Add(conflictName);
                }

                HasConflicts = conflicts.Count > 0;
                HasRestorePreview = true;
                BuildRestoreSummaryText(manifest);
                RestoreStatusText = conflicts.Count > 0
                    ? $"校验通过：备份含 {manifest.PackageNames.Count} 个用户模板包，检测到 {conflicts.Count} 个同名冲突，恢复前需确认覆盖。"
                    : $"校验通过：备份含 {manifest.PackageNames.Count} 个用户模板包，无同名冲突。";
            });
        }
        catch (OperationCanceledException)
        {
            RunOnUiThread(() => RestoreStatusText = "校验已取消。");
        }
        catch (BackupValidationException exception)
        {
            _logger.LogWarning("恢复备份校验失败：{Message}。", exception.Message);
            RunOnUiThread(() =>
            {
                HasRestorePreview = false;
                HasConflicts = false;
                RestoreStatusText = "校验失败。";
            });

            _dialogService.ShowError($"备份文件校验失败：{exception.Message}", "校验失败");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "校验备份文件发生未预期异常。");
            RunOnUiThread(() => RestoreStatusText = "校验失败。");
            _dialogService.ShowError($"备份文件校验失败：{exception.Message}", "校验失败");
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// 执行恢复：先以不覆盖方式调用备份服务，遇同名冲突经确认后以覆盖方式重试，
    /// 完成后提示需重输密码的数据源与需重配的 LLM apiKey 并引导打开数据源管理窗口。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private async Task RestoreBackupAsync()
    {
        if (string.IsNullOrWhiteSpace(BackupFilePath) || !File.Exists(BackupFilePath))
        {
            _dialogService.ShowInfo("请先选择备份文件。", "恢复");
            return;
        }

        CancellationTokenSource cts = BeginOperation();
        try
        {
            RestoreStatusText = "正在恢复备份…";
            RestoreResult result = await _backupRestoreService.RestoreBackupAsync(BackupFilePath, overwriteUserPackages: false, cts.Token);
            if (result.NeedsConfirmation)
            {
                // 同名冲突未允许覆盖时弹确认，用户同意后以覆盖方式重试
                bool confirmed = await _confirmDialogService.ConfirmAsync(
                    "覆盖确认",
                    BuildConflictMessage(result.ConflictingPackageNames));
                if (!confirmed)
                {
                    RunOnUiThread(() => RestoreStatusText = "已取消恢复，未覆盖任何内容。");
                    return;
                }

                RestoreStatusText = "正在覆盖恢复备份…";
                result = await _backupRestoreService.RestoreBackupAsync(BackupFilePath, overwriteUserPackages: true, cts.Token);
            }

            RunOnUiThread(() =>
            {
                RestoreStatusText = "恢复完成。";
                HasRestorePreview = false;
                HasConflicts = false;
                RestorePackageNames.Clear();
                ConflictPackageNames.Clear();
            });

            await ShowRestoreCompletionAsync(result);
        }
        catch (OperationCanceledException)
        {
            RunOnUiThread(() => RestoreStatusText = "恢复已取消。");
        }
        catch (BackupValidationException exception)
        {
            _logger.LogError(exception, "恢复备份失败。");
            RunOnUiThread(() => RestoreStatusText = "恢复失败。");
            _dialogService.ShowError($"恢复失败：{exception.Message}", "恢复失败");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "恢复备份发生未预期异常。");
            RunOnUiThread(() => RestoreStatusText = "恢复失败。");
            _dialogService.ShowError($"恢复失败：{exception.Message}", "恢复失败");
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// 判定操作命令是否可执行：无在途备份/恢复操作时可用。
    /// </summary>
    private bool CanStartOperation() => !IsBusy;

    /// <summary>
    /// 组装覆盖确认提示文本，列出同名冲突的用户模板包清单。
    /// </summary>
    /// <param name="conflictingPackages">同名冲突的用户模板包名清单。</param>
    /// <returns>覆盖确认对话框正文。</returns>
    private static string BuildConflictMessage(IReadOnlyList<string> conflictingPackages)
    {
        var lines = new List<string> { $"检测到 {conflictingPackages.Count} 个同名用户模板包：" };
        foreach (string packageName in conflictingPackages)
        {
            lines.Add($"  - {packageName}");
        }

        lines.Add(string.Empty);
        lines.Add("是否覆盖这些同名用户模板包？恢复不会写回任何密码或 apiKey。");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 恢复完成后展示结果提示：还原包数量、需重输密码的数据源与需重配的 LLM apiKey，
    /// 需要重输时弹确认引导打开数据源管理窗口。
    /// </summary>
    /// <param name="result">恢复服务返回的已完成结果。</param>
    private async Task ShowRestoreCompletionAsync(RestoreResult result)
    {
        var lines = new List<string> { $"已还原用户模板包 {result.RestoredPackageNames.Count} 个。" };
        if (result.PasswordRequiredDataSources.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"{result.PasswordRequiredDataSources.Count} 个数据源密码需重新输入：");
            foreach (string dataSourceName in result.PasswordRequiredDataSources)
            {
                lines.Add($"  - {dataSourceName}");
            }
        }

        if (result.LlmNeedsReconfigure)
        {
            lines.Add(string.Empty);
            lines.Add("LLM apiKey 需重新配置。");
        }

        string message = string.Join("\n", lines);
        bool needsReentry = result.PasswordRequiredDataSources.Count > 0 || result.LlmNeedsReconfigure;
        if (!needsReentry)
        {
            _dialogService.ShowInfo(message, "恢复完成");
            return;
        }

        // 需要重输密码时引导打开数据源管理窗口，LLM apiKey 提示经“文件 → 设置”重配
        bool openManager = await _confirmDialogService.ConfirmAsync(
            "恢复完成",
            message + "\n\n是否现在打开数据源管理窗口重新输入密码？LLM apiKey 可在“文件 → 设置…”中重新配置。");
        if (openManager)
        {
            OpenDataSourceManager();
        }
    }

    /// <summary>
    /// 打开数据源管理窗口，供恢复后重新输入各连接密码。
    /// </summary>
    private void OpenDataSourceManager()
    {
        try
        {
            DataSourceManagerWindow window = _dataSourceManagerWindowFactory();
            window.Owner = Application.Current?.MainWindow;
            window.ShowDialog();
        }
        catch (Exception exception)
        {
            // 窗口创建或展示失败时给出可读提示，不中断迁移窗口运行
            _logger.LogError(exception, "打开数据源管理窗口失败。");
            _dialogService.ShowError($"打开数据源管理窗口失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 汇总备份页配置摘要文本：数据源数量、LLM apiKey 状态、模板搜索目录数量与工作区根。
    /// </summary>
    /// <param name="config">当前应用配置。</param>
    private void BuildBackupSummaryText(AppConfig config)
    {
        int dataSourceCount = config.DataSources?.Count ?? 0;
        bool llmConfigured = !string.IsNullOrEmpty(config.Llm?.ApiKeyEncrypted);
        int templateDirectoryCount = config.TemplateSearchDirectories?.Count ?? 0;
        string workspaceRoot = string.IsNullOrWhiteSpace(config.WorkspaceRoot) ? "（未设置）" : config.WorkspaceRoot;
        BackupSummaryText = $"配置摘要：数据源 {dataSourceCount} 个 · LLM apiKey {(llmConfigured ? "已配置" : "未配置")} · 模板搜索目录 {templateDirectoryCount} 个 · 工作区根 {workspaceRoot}";
    }

    /// <summary>
    /// 汇总恢复页配置快照摘要文本：备份内的数据源数量、LLM apiKey 状态、模板搜索目录数量与工作区根。
    /// </summary>
    /// <param name="manifest">备份清单，含脱敏配置快照。</param>
    private void BuildRestoreSummaryText(BackupManifest manifest)
    {
        int dataSourceCount = manifest.Config.DataSources.Count;
        int templateDirectoryCount = manifest.Config.TemplateSearchDirectories.Count;
        string workspaceRoot = string.IsNullOrWhiteSpace(manifest.Config.WorkspaceRoot) ? "（未设置）" : manifest.Config.WorkspaceRoot;
        string llmText = manifest.Config.LlmApiKeyConfigured ? "已配置（恢复后需重配）" : "未配置";
        RestoreConfigSummaryText = $"配置快照：数据源 {dataSourceCount} 个 · LLM apiKey {llmText} · 模板搜索目录 {templateDirectoryCount} 个 · 工作区根 {workspaceRoot}";
    }

    /// <summary>
    /// 读取 .dbcg 备份包内的 manifest.json 清单并做版本校验，供恢复页校验预览。
    /// </summary>
    /// <param name="filePath">备份文件绝对路径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>解析并校验通过的备份清单。</returns>
    /// <exception cref="BackupValidationException">清单缺失、解析失败或版本不支持时抛出。</exception>
    private static async Task<BackupManifest> ReadBackupManifestAsync(string filePath, CancellationToken cancellationToken)
    {
        using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry? entry = archive.GetEntry(ManifestEntryName);
        if (entry is null)
        {
            throw new BackupValidationException("备份文件缺少 manifest.json 清单。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using StreamReader reader = new(entry.Open(), Encoding.UTF8);
        string json = await reader.ReadToEndAsync(cancellationToken);

        BackupManifest? manifest = JsonSerializer.Deserialize<BackupManifest>(json, JsonOptions);
        if (manifest is null)
        {
            throw new BackupValidationException("备份清单 manifest.json 内容为空。");
        }

        if (manifest.Version != SupportedBackupVersion)
        {
            throw new BackupValidationException($"不支持的备份文件版本：{manifest.Version}，当前支持版本 {SupportedBackupVersion}。");
        }

        // 兜底补齐可能缺失的集合，与 Core 校验口径一致，避免篡改清单导致后续遍历空引用
        manifest.PackageNames ??= new List<string>();
        manifest.Config ??= new BackupManifestConfig();
        manifest.Config.DataSources ??= new List<BackupManifestConfig.DataSourceSnapshot>();
        manifest.Config.TemplateSearchDirectories ??= new List<string>();

        return manifest;
    }

    /// <summary>
    /// 检测默认模板库目录下是否存在与备份同名的用户模板包，返回冲突包名清单。
    /// </summary>
    /// <param name="packageNames">备份清单声明的用户模板包名。</param>
    /// <returns>已存在同名目录的包名清单。</returns>
    private static IReadOnlyList<string> FindPackageConflicts(IReadOnlyList<string> packageNames)
    {
        string defaultTemplateDirectory = GetDefaultTemplateDirectory();
        var conflicts = new List<string>();
        foreach (string packageName in packageNames ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(packageName))
            {
                continue;
            }

            if (Directory.Exists(Path.Combine(defaultTemplateDirectory, packageName)))
            {
                conflicts.Add(packageName);
            }
        }

        return conflicts;
    }

    /// <summary>
    /// 取默认模板库目录，与 Core 备份服务还原目标保持一致。
    /// </summary>
    /// <returns>默认模板库目录绝对路径。</returns>
    private static string GetDefaultTemplateDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DbCodeGen",
            "Templates");
    }

    /// <summary>
    /// 开启一次备份/恢复操作：取消并回收上次在途取消源，置繁忙状态。
    /// </summary>
    /// <returns>本次操作的取消源。</returns>
    private CancellationTokenSource BeginOperation()
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        IsBusy = true;
        return _operationCts;
    }

    /// <summary>
    /// 结束当前备份/恢复操作：回收取消源并复位繁忙状态。
    /// </summary>
    private void EndOperation()
    {
        _operationCts?.Dispose();
        _operationCts = null;
        IsBusy = false;
    }

    /// <summary>
    /// 将指定动作调度到 UI 线程执行，已在 UI 线程时直接执行，保证集合与属性更新不跨线程。
    /// </summary>
    /// <param name="action">需要在 UI 线程执行的更新动作。</param>
    private void RunOnUiThread(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.Invoke(action);
    }
}

/// <summary>
/// 备份预览行视图模型，承载用户模板包名称与说明的只读展示。
/// </summary>
public sealed class BackupPackageRowViewModel
{
    /// <summary>
    /// 使用包名与说明构造备份预览行。
    /// </summary>
    /// <param name="name">用户模板包包名。</param>
    /// <param name="description">用户模板包说明，可为空。</param>
    public BackupPackageRowViewModel(string name, string description)
    {
        Name = name;
        Description = string.IsNullOrWhiteSpace(description) ? "（无说明）" : description;
    }

    /// <summary>
    /// 用户模板包包名。
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// 用户模板包说明，为空时展示占位文案。
    /// </summary>
    public string Description { get; }
}
