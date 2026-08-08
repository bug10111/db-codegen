using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbCodeGen.App.Services;
using DbCodeGen.App.Views;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Generation;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.App.ViewModels;

/// <summary>
/// 待写清单行视图模型，承载 dry-run 单条目的展示字段，供主窗口④生成栏 DataGrid 绑定。
/// 展示来源表名、动作分类中文文案与渲染后相对路径；错误列在写盘失败后由宿主回填，
/// 与生成日志同一来源，供界面逐条展示失败原因。
/// </summary>
public sealed partial class GenerationEntryRowViewModel : ObservableObject
{
    /// <summary>
    /// 使用 dry-run 待写条目构造清单行。
    /// </summary>
    /// <param name="entry">dry-run 待写条目，含来源表名、相对路径、动作分类与失败追溯。</param>
    /// <exception cref="ArgumentNullException">entry 为 null 时抛出。</exception>
    public GenerationEntryRowViewModel(GenerationFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        TableName = entry.TableName;
        RelativePath = entry.RelativePath;
        Error = entry.Error;
        ActionText = entry.Action switch
        {
            GenerationAction.New => "新增",
            GenerationAction.Overwrite => "覆盖",
            _ => "跳过"
        };
    }

    /// <summary>
    /// 来源表名，渲染上下文归属。
    /// </summary>
    public string TableName { get; }

    /// <summary>
    /// 动作分类中文文案：新增 / 覆盖 / 跳过。
    /// </summary>
    public string ActionText { get; }

    /// <summary>
    /// 渲染后相对输出根路径，已解析 {{变量}} 占位。
    /// </summary>
    public string RelativePath { get; }

    /// <summary>
    /// 写盘失败的条目级异常信息，供界面逐条展示失败原因；无异常为 null。
    /// </summary>
    [ObservableProperty]
    private string? _error;
}

/// <summary>
/// 主窗口④生成栏视图模型，承载批量代码生成的路径配置、dry-run 预览、覆盖确认与生成写盘全流程。
/// 勾选表集合取自①区表列表视图模型，勾选模板文件集合取自②模板区当前模板包的"勾选到层"勾选态，
/// 路径默认值取自设置与配置，生成完成后由核心服务回写最近相对输出根。
/// </summary>
public sealed partial class GenerationViewModel : ObservableObject
{
    /// <summary>
    /// 覆盖确认消息中最多列出的覆盖文件数，超出部分以计数收尾，避免确认框消息过长。
    /// </summary>
    private const int MaxOverwriteListed = 10;

    private readonly ICodeGenerator _codeGenerator;
    private readonly IConfigService _configService;
    private readonly IDialogService _dialogService;
    private readonly IConfirmDialogService _confirmDialogService;
    private readonly IFolderPickerService _folderPickerService;
    private readonly Func<UnmappedTypesWindow> _unmappedWindowFactory;
    private readonly Func<TypeMappingWindow> _mappingWindowFactory;
    private readonly TableListViewModel _tableListViewModel;
    private readonly TemplateViewModel _templateViewModel;
    private readonly ILogger<GenerationViewModel> _logger;

    /// <summary>
    /// 预览或生成在途操作的取消源，重复操作前取消上次未完成的调用。
    /// </summary>
    private CancellationTokenSource? _operationCts;

    /// <summary>
    /// 工作区根目录，绝对输出路径的根前缀，默认取自设置与配置并允许本次修改。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private string _workspaceRoot = string.Empty;

    /// <summary>
    /// 代码目录：项目内代码落盘完整相对路径（含包名），如 src/main/java/com/example/common，
    /// 与工作区根拼接作为本次输出根，生成完成后回写最近值；由它推导基础包名与相对输出根。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private string _codeDirectory = string.Empty;

    /// <summary>
    /// 由代码目录推导的基础包名，供预览渲染与生成保持一致；无包名部分时为空串（回落模板包 manifest 包名）。
    /// </summary>
    public string EffectiveBasePackage => CodeDirectoryParser.DeriveBasePackage(CodeDirectory);

    /// <summary>
    /// 是否处于预览或生成操作繁忙状态，繁忙时禁用两个操作按钮防重复提交。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private bool _isBusy;

    /// <summary>
    /// 本次会话是否已构建过 dry-run 清单，驱动生成命令可用性，输入变化时失效。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private bool _hasPreview;

    /// <summary>
    /// 待写清单摘要文本，展示新增/覆盖/跳过计数。
    /// </summary>
    [ObservableProperty]
    private string _previewSummaryText = string.Empty;

    /// <summary>
    /// 结果统计文本，展示生成/覆盖/跳过/失败四计数与取消标记。
    /// </summary>
    [ObservableProperty]
    private string _resultSummaryText = string.Empty;

    /// <summary>
    /// 操作状态文本，展示进行中的渲染/分类/写盘进度与阶段结果。
    /// </summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    /// 生成日志文本，逐行展示时间、级别与消息。
    /// </summary>
    [ObservableProperty]
    private string _logText = string.Empty;

    /// <summary>
    /// 待写清单集合，绑定④生成栏 DataGrid，按 dry-run 条目的渲染顺序排列。
    /// </summary>
    public ObservableCollection<GenerationEntryRowViewModel> PreviewEntries { get; } = new();

    /// <summary>
    /// 界面是否空闲可用，与繁忙状态相反，供④生成栏整体启用绑定。
    /// </summary>
    public bool IsIdle => !IsBusy;

    /// <summary>
    /// 使用批量生成服务、配置服务、对话框服务、目录选择服务、表列表视图模型、模板视图模型与日志器构造生成栏视图模型。
    /// 初始化路径默认值并订阅①区勾选数量与②区当前包变化，输入变化时失效已构建的预览。
    /// </summary>
    /// <param name="codeGenerator">批量生成服务，承载 dry-run 预览与确认后写盘。</param>
    /// <param name="configService">配置服务，读取工作区根与最近相对输出根默认值。</param>
    /// <param name="dialogService">消息提示服务，用于操作失败与引导反馈。</param>
    /// <param name="confirmDialogService">二次确认服务，用于覆盖写盘前确认。</param>
    /// <param name="folderPickerService">目录选择服务，用于浏览选择工作区根。</param>
    /// <param name="unmappedWindowFactory">未映射类型提示窗口工厂，用于预览时存在未映射类型的弹窗。</param>
    /// <param name="mappingWindowFactory">类型映射窗口工厂，用于弹窗“去配置映射”跳转。</param>
    /// <param name="tableListViewModel">①区表列表视图模型，提供勾选表集合与勾选数量变化通知。</param>
    /// <param name="templateViewModel">②模板区编辑器视图模型，提供当前模板包与勾选到层文件集合。</param>
    /// <param name="logger">视图模型日志器，日志不输出模板正文与敏感信息。</param>
    /// <exception cref="ArgumentNullException">任一依赖参数为 null 时抛出。</exception>
    public GenerationViewModel(
        ICodeGenerator codeGenerator,
        IConfigService configService,
        IDialogService dialogService,
        IConfirmDialogService confirmDialogService,
        IFolderPickerService folderPickerService,
        Func<UnmappedTypesWindow> unmappedWindowFactory,
        Func<TypeMappingWindow> mappingWindowFactory,
        TableListViewModel tableListViewModel,
        TemplateViewModel templateViewModel,
        ILogger<GenerationViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(codeGenerator);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(confirmDialogService);
        ArgumentNullException.ThrowIfNull(folderPickerService);
        ArgumentNullException.ThrowIfNull(unmappedWindowFactory);
        ArgumentNullException.ThrowIfNull(mappingWindowFactory);
        ArgumentNullException.ThrowIfNull(tableListViewModel);
        ArgumentNullException.ThrowIfNull(templateViewModel);
        ArgumentNullException.ThrowIfNull(logger);

        _codeGenerator = codeGenerator;
        _configService = configService;
        _dialogService = dialogService;
        _confirmDialogService = confirmDialogService;
        _folderPickerService = folderPickerService;
        _unmappedWindowFactory = unmappedWindowFactory;
        _mappingWindowFactory = mappingWindowFactory;
        _tableListViewModel = tableListViewModel;
        _templateViewModel = templateViewModel;
        _logger = logger;

        // 订阅输入源变化，勾选表数量或当前模板包变化时失效旧预览并刷新命令可用性
        _tableListViewModel.PropertyChanged += OnSelectionStateChanged;
        _templateViewModel.PropertyChanged += OnSelectionStateChanged;
        _templateViewModel.Files.CollectionChanged += OnTemplateFilesChanged;

        // 路径默认值取自设置与配置，本次生成可临时修改
        GenerationDefaults defaults = _configService.GetGenerationDefaults();
        WorkspaceRoot = defaults.WorkspaceRoot;
        CodeDirectory = defaults.LastRelativeOutputRoot;
        StatusText = "设置代码目录与工作区根后，点击预览待写查看待写清单。";
    }

    /// <summary>
    /// 解除输入源订阅并取消在途操作，供主窗口关闭时调用避免悬挂引用。
    /// 在途取消源只做取消不释放，由下一次操作启动时统一回收，避免关闭路径与后台线程竞争释放。
    /// </summary>
    public void Detach()
    {
        _tableListViewModel.PropertyChanged -= OnSelectionStateChanged;
        _templateViewModel.PropertyChanged -= OnSelectionStateChanged;
        _templateViewModel.Files.CollectionChanged -= OnTemplateFilesChanged;
        _operationCts?.Cancel();
        _operationCts = null;
    }

    /// <summary>
    /// 浏览选择工作区根目录，选中后更新工作区根并失效旧预览。
    /// </summary>
    [RelayCommand]
    private async Task PickWorkspaceRootAsync()
    {
        string? selected = await _folderPickerService.PickFolderAsync(WorkspaceRoot, "选择工作区根目录");
        if (!string.IsNullOrWhiteSpace(selected))
        {
            WorkspaceRoot = selected;
        }
    }

    /// <summary>
    /// 预览待写：组装生成请求并经批量生成服务构建 dry-run 清单，结果回填待写清单与计数摘要。
    /// 构建过程中发现未映射类型时弹窗引导补映射，直到全部映射、使用默认继续或取消。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync()
    {
        GenerationRequest? request = BuildRequest(out string errorMessage);
        if (request is null)
        {
            _dialogService.ShowError(errorMessage);
            return;
        }

        GenerationPreview? preview = await BuildPreviewWithMappingLoopAsync(request);
        if (preview is null)
        {
            return;
        }

        ApplyPreview(preview);
    }

    /// <summary>
    /// 构建 dry-run 清单并处理未映射类型：全部类型已映射时直接返回；存在未映射时弹窗，
    /// 用户选择去配置映射则打开映射窗口后重新构建，选择使用默认继续则按 String 兜底返回，取消则返回空引用。
    /// </summary>
    /// <param name="request">本次生成的完整请求。</param>
    /// <returns>dry-run 清单；用户取消返回 null。</returns>
    private async Task<GenerationPreview?> BuildPreviewWithMappingLoopAsync(GenerationRequest request)
    {
        while (true)
        {
            CancellationTokenSource cts = BeginOperation();
            IsBusy = true;
            GenerationPreview preview;
            try
            {
                // 进度经 Progress 回 UI 线程更新状态文本，预览阶段报告渲染与分类进度
                var progress = new Progress<GenerationProgress>(OnProgress);
                preview = await _codeGenerator.BuildPreviewAsync(request, progress, cts.Token);
            }
            catch (OperationCanceledException)
            {
                StatusText = "预览已取消。";
                return null;
            }
            catch (GenerationException exception)
            {
                StatusText = "预览失败：请修正模板或输出路径后重试。";
                _dialogService.ShowError(exception.Message);
                return null;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "批量生成 dry-run 预览失败。");
                StatusText = "预览失败。";
                _dialogService.ShowError($"预览失败：{exception.Message}");
                return null;
            }
            finally
            {
                IsBusy = false;
            }

            // 全部类型已映射时直接返回清单
            if (preview.UnmappedTypes.Count == 0)
            {
                return preview;
            }

            // 存在未映射类型：弹窗让用户选择去配置映射、使用默认继续或取消
            UnmappedChoice choice = AskUnmappedTypes(preview.UnmappedTypes);
            if (choice == UnmappedChoice.ContinueWithDefault)
            {
                return preview;
            }

            if (choice == UnmappedChoice.Configure)
            {
                // 打开映射窗口补全后重新构建清单，映射保存即生效
                OpenTypeMappingWindow();
                continue;
            }

            StatusText = "预览已取消：存在未映射的数据库类型。";
            return null;
        }
    }

    /// <summary>
    /// 弹出未映射类型提示窗口，返回用户对未映射类型的处理选择。
    /// </summary>
    /// <param name="types">未映射类型清单。</param>
    /// <returns>用户选择的处理方式。</returns>
    private UnmappedChoice AskUnmappedTypes(IReadOnlyList<UnmappedTypeInfo> types)
    {
        UnmappedTypesWindow window = _unmappedWindowFactory();
        window.Owner = Application.Current?.MainWindow;
        window.SetTypes(types);
        window.ShowDialog();
        return window.Result;
    }

    /// <summary>
    /// 打开类型映射窗口供用户补全映射，关闭后由调用方重新构建清单。
    /// </summary>
    private void OpenTypeMappingWindow()
    {
        TypeMappingWindow window = _mappingWindowFactory();
        window.Owner = Application.Current?.MainWindow;
        window.ShowDialog();
    }

    /// <summary>
    /// 生成写盘：先经未映射类型预检弹窗（与预览一致），再调用批量生成服务内部重算 dry-run，
    /// 存在覆盖项时先经确认再写盘，结果统计与生成日志回填底栏，最近相对输出根由核心服务回写。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        GenerationRequest? request = BuildRequest(out string errorMessage);
        if (request is null)
        {
            _dialogService.ShowError(errorMessage);
            return;
        }

        // 生成前同样做未映射类型预检：未处理或用户取消时中止，避免静默以 String 兜底写盘
        GenerationPreview? preview = await BuildPreviewWithMappingLoopAsync(request);
        if (preview is null)
        {
            return;
        }

        CancellationTokenSource cts = BeginOperation();
        IsBusy = true;
        try
        {
            // 覆盖确认回调由界面层注入，包装二次确认服务；进度报告渲染/分类/写盘三阶段
            var progress = new Progress<GenerationProgress>(OnProgress);
            GenerationResult result = await _codeGenerator.GenerateAsync(request, ConfirmOverwriteAsync, progress, cts.Token);
            ApplyResult(result);
        }
        catch (GenerationException exception)
        {
            StatusText = "生成失败：请修正模板或输出路径后重试。";
            _dialogService.ShowError(exception.Message);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "批量生成写盘失败。");
            StatusText = "生成失败。";
            _dialogService.ShowError($"生成失败：{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 判定预览命令是否可执行：非繁忙、已设置工作区根、勾选表非空且当前模板包存在勾选到层文件。
    /// </summary>
    private bool CanPreview()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(WorkspaceRoot))
        {
            return false;
        }

        if (_tableListViewModel.SelectedTables.Count == 0)
        {
            return false;
        }

        return BuildSelectedFiles().Any(file => file.IsSelected);
    }

    /// <summary>
    /// 判定生成命令是否可执行：非繁忙、已设置工作区根且本次会话已构建过 dry-run 清单。
    /// </summary>
    private bool CanGenerate()
    {
        return !IsBusy && HasPreview && !string.IsNullOrWhiteSpace(WorkspaceRoot);
    }

    /// <summary>
    /// 组装本次生成的完整请求，输入不满足要求时输出错误原因并返回 null。
    /// </summary>
    /// <param name="errorMessage">输入不满足要求时的错误原因，满足时为空串。</param>
    /// <returns>组装成功的生成请求；输入不合法返回 null。</returns>
    private GenerationRequest? BuildRequest(out string errorMessage)
    {
        errorMessage = string.Empty;

        TemplatePackageInfo? package = _templateViewModel.CurrentPackage;
        if (package is null)
        {
            errorMessage = "请先在②模板区选择一个模板包。";
            return null;
        }

        IReadOnlyList<TableInfo> tables = _tableListViewModel.SelectedTables;
        if (tables.Count == 0)
        {
            errorMessage = "请先在①表列表区勾选至少一张表。";
            return null;
        }

        IReadOnlyList<TemplateFileSelection> selectedFiles = BuildSelectedFiles();
        if (selectedFiles.All(file => !file.IsSelected))
        {
            errorMessage = "当前模板包没有勾选到层的模板文件，请先勾选参与生成的模板文件。";
            return null;
        }

        if (string.IsNullOrWhiteSpace(WorkspaceRoot))
        {
            errorMessage = "请先设置工作区根目录。";
            return null;
        }

        // 由代码目录推导相对输出根与基础包名，与预览渲染保持一致
        (string basePackage, string relativeOutputRoot) = CodeDirectoryParser.Split(CodeDirectory);
        return new GenerationRequest(
            package, tables, selectedFiles,
            WorkspaceRoot.Trim(),
            relativeOutputRoot,
            basePackage,
            _tableListViewModel.CurrentConnection,
            CodeDirectory.Trim());
    }

    /// <summary>
    /// 按当前模板包清单构建勾选模板文件集合：每文件按 manifest 勾选到层默认态携带勾选标记。
    /// </summary>
    /// <returns>当前模板包的模板文件选择集合，未选择包时为空集合。</returns>
    private IReadOnlyList<TemplateFileSelection> BuildSelectedFiles()
    {
        TemplatePackageInfo? package = _templateViewModel.CurrentPackage;
        if (package is null)
        {
            return Array.Empty<TemplateFileSelection>();
        }

        var selections = new List<TemplateFileSelection>(package.Files.Count);
        foreach (TemplateFileInfo file in package.Files)
        {
            selections.Add(new TemplateFileSelection(file.RelativeTemplatePath, file.OutputPath, file.IsEnabled));
        }

        return selections;
    }

    /// <summary>
    /// 应用 dry-run 清单结果：回填待写清单、更新摘要与状态，并复位结果统计与日志。
    /// </summary>
    /// <param name="preview">批量生成服务返回的 dry-run 清单。</param>
    private void ApplyPreview(GenerationPreview preview)
    {
        PreviewEntries.Clear();
        foreach (GenerationFileEntry entry in preview.Entries)
        {
            PreviewEntries.Add(new GenerationEntryRowViewModel(entry));
        }

        HasPreview = true;
        PreviewSummaryText = $"待写清单：新增 {preview.NewCount} · 覆盖 {preview.OverwriteCount} · 跳过 {preview.SkipCount}";
        ResultSummaryText = string.Empty;
        LogText = string.Empty;
        StatusText = $"预览完成，共 {preview.Entries.Count} 个待写条目。";
    }

    /// <summary>
    /// 应用生成结果：回填结果统计与生成日志，取消时在统计末尾标注取消标记，并回填失败条目的错误列。
    /// </summary>
    /// <param name="result">批量生成服务返回的写盘结果。</param>
    private void ApplyResult(GenerationResult result)
    {
        string cancelMark = result.IsCancelled ? "（已取消）" : string.Empty;
        ResultSummaryText = $"生成 {result.Generated} · 覆盖 {result.Overwritten} · 跳过 {result.Skipped} · 失败 {result.Failed}{cancelMark}";

        var builder = new StringBuilder();
        foreach (GenerationLogEntry log in result.Logs)
        {
            builder.AppendLine(FormatLog(log));
        }

        LogText = builder.ToString();
        BackfillFailedEntries(result.Logs);
        StatusText = result.IsCancelled ? "生成已取消，未写入全部文件。" : "生成完成。";
    }

    /// <summary>
    /// 按错误级生成日志回填待写清单条目的错误列：日志与待写条目按相对路径匹配，逐条展示写盘失败原因。
    /// 回填前先清空全部行错误，保证错误列只反映本次生成结果，避免上一轮失败信息残留。
    /// </summary>
    /// <param name="logs">生成日志，含错误级失败条目。</param>
    private void BackfillFailedEntries(IReadOnlyList<GenerationLogEntry> logs)
    {
        foreach (GenerationEntryRowViewModel row in PreviewEntries)
        {
            row.Error = null;
        }

        var failureByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (GenerationLogEntry log in logs)
        {
            if (log.Level != GenerationLogLevel.Error)
            {
                continue;
            }

            string? failedPath = ExtractFailedPath(log.Message);
            if (!string.IsNullOrEmpty(failedPath))
            {
                failureByPath[failedPath] = log.Message;
            }
        }

        if (failureByPath.Count == 0)
        {
            return;
        }

        foreach (GenerationEntryRowViewModel row in PreviewEntries)
        {
            if (failureByPath.TryGetValue(row.RelativePath, out string? failureMessage))
            {
                row.Error = failureMessage;
            }
        }
    }

    /// <summary>
    /// 从错误级日志消息中提取失败目标文件的相对路径，消息格式与写盘服务固定约定
    /// （写盘失败：{相对路径}，原因：{异常描述}），格式不匹配时返回 null 不影响其余逻辑。
    /// </summary>
    /// <param name="message">错误级日志消息。</param>
    /// <returns>提取出的相对路径；无法提取返回 null。</returns>
    private static string? ExtractFailedPath(string message)
    {
        const string prefix = "写盘失败：";
        const string reasonMarker = "，原因：";
        if (!message.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        int reasonIndex = message.IndexOf(reasonMarker, StringComparison.Ordinal);
        if (reasonIndex < 0)
        {
            return null;
        }

        return message[prefix.Length..reasonIndex];
    }

    /// <summary>
    /// 覆盖确认回调：将覆盖条目清单组装为确认框消息，经二次确认服务返回用户选择。
    /// </summary>
    /// <param name="overwriteEntries">待覆盖的目标文件条目。</param>
    /// <returns>用户确认返回 true，否则返回 false。</returns>
    private async Task<bool> ConfirmOverwriteAsync(IReadOnlyList<GenerationFileEntry> overwriteEntries)
    {
        if (overwriteEntries.Count == 0)
        {
            return true;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"以下 {overwriteEntries.Count} 个目标文件已存在且内容不同，覆盖将替换原有内容：");
        builder.AppendLine();

        // 覆盖文件过多时只列前若干个，其余以计数收尾，避免确认框消息过长
        int listed = 0;
        foreach (GenerationFileEntry entry in overwriteEntries)
        {
            if (listed >= MaxOverwriteListed)
            {
                break;
            }

            builder.AppendLine($"  · {entry.RelativePath}");
            listed++;
        }

        if (overwriteEntries.Count > MaxOverwriteListed)
        {
            builder.AppendLine($"  · 其余 {overwriteEntries.Count - MaxOverwriteListed} 个文件略…");
        }

        builder.AppendLine();
        builder.Append("是否确认覆盖？");

        return await _confirmDialogService.ConfirmAsync("确认覆盖", builder.ToString());
    }

    /// <summary>
    /// 输入源变化通知：①区勾选数量或②区当前包/文件集合变化时失效旧预览并刷新命令可用性。
    /// </summary>
    /// <param name="sender">属性变化事件发送方。</param>
    /// <param name="e">属性名变化参数。</param>
    private void OnSelectionStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TableListViewModel.SelectedCount)
            || e.PropertyName == nameof(TemplateViewModel.SelectedPackage)
            || e.PropertyName == nameof(TemplateViewModel.Files))
        {
            InvalidatePreview();
            PreviewCommand.NotifyCanExecuteChanged();
            GenerateCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// ②模板区当前包文件集合变化通知：包加载完成后文件树填充必然触发集合变更，
    /// 此时当前包已就绪，据此重新评估预览命令可用性（勾选到层文件集合是否非空）。
    /// </summary>
    /// <param name="sender">集合变化事件发送方。</param>
    /// <param name="e">集合变化参数。</param>
    private void OnTemplateFilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidatePreview();
        PreviewCommand.NotifyCanExecuteChanged();
        GenerateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 工作区根变更后失效旧预览，保证路径变化后待写清单与生成结果一致。
    /// </summary>
    /// <param name="value">变更后的工作区根文本。</param>
    partial void OnWorkspaceRootChanged(string value)
    {
        InvalidatePreview();
    }

    /// <summary>
    /// 代码目录变更后失效旧预览并刷新派生基础包名，保证输出路径与预览渲染一致。
    /// </summary>
    /// <param name="value">变更后的代码目录文本。</param>
    partial void OnCodeDirectoryChanged(string value)
    {
        InvalidatePreview();
        OnPropertyChanged(nameof(EffectiveBasePackage));
    }

    /// <summary>
    /// 失效已构建的预览：生成输入变化后旧待写清单不再准确，清空清单并复位生成命令。
    /// </summary>
    private void InvalidatePreview()
    {
        if (!HasPreview)
        {
            return;
        }

        HasPreview = false;
        PreviewEntries.Clear();
        PreviewSummaryText = string.Empty;
        StatusText = "生成输入已变化，请重新点击预览待写。";
    }

    /// <summary>
    /// 启动一次新操作：取消并释放上次在途取消源，返回本次操作的取消源。
    /// </summary>
    /// <returns>本次操作使用的取消源，操作期间禁止并发触发。</returns>
    private CancellationTokenSource BeginOperation()
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        return _operationCts;
    }

    /// <summary>
    /// 进度推送回调：按阶段与已完成数更新状态文本，当前文件过长时截断展示。
    /// </summary>
    /// <param name="progress">生成进度，含阶段、已完成数、总数与当前文件相对路径。</param>
    private void OnProgress(GenerationProgress progress)
    {
        string stageText = progress.Stage switch
        {
            GenerationStage.Rendering => "渲染",
            GenerationStage.Previewing => "分类",
            GenerationStage.Writing => "写盘",
            _ => "处理"
        };

        string currentFile = progress.CurrentFile;
        if (currentFile.Length > 80)
        {
            currentFile = currentFile[..80] + "…";
        }

        StatusText = $"{stageText}中：{progress.Completed}/{progress.Total} {currentFile}";
    }

    /// <summary>
    /// 将生成日志条目格式化为单行文本，级别映射为中文文案。
    /// </summary>
    /// <param name="log">生成日志条目。</param>
    /// <returns>格式化后的日志行。</returns>
    private static string FormatLog(GenerationLogEntry log)
    {
        string levelText = log.Level switch
        {
            GenerationLogLevel.Warning => "警告",
            GenerationLogLevel.Error => "错误",
            _ => "信息"
        };

        return $"[{log.Timestamp:HH:mm:ss}] {levelText} {log.Message}";
    }
}
