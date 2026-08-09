using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbCodeGen.App.Services;
using DbCodeGen.App.Views;
using DbCodeGen.Core.Ai;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.DataSource;
using DbCodeGen.Core.Model;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.App.ViewModels;

/// <summary>
/// 「AI 模板助手」宿主视图模型（App.Ai）：承载「写模板」「改模板」双 Tab 的窗口级共享参考文件上下文
/// 与写模板生成闭环骨架。共享上下文由写/改两 Tab 共读共改，参考文件命令调多选对话框并按 F04 配置校验；
/// 写模板生成逻辑保留供写模板 Tab 填充时复用，窗口关闭经取消源取消在途任务。
/// </summary>
public sealed partial class AiTemplateAssistantViewModel : ObservableObject
{
    private readonly IConfigService _configService;
    private readonly ITemplateAiGenerator _templateAiGenerator;
    private readonly ICurrentDataSourceService _currentDataSourceService;
    private readonly TableCatalogService _tableCatalogService;
    private readonly TableListViewModel _tableListViewModel;
    private readonly IDialogService _dialogService;
    private readonly IConfirmDialogService _confirmDialogService;
    private readonly IFilePickerService _filePickerService;
    private readonly Func<SettingsWindow> _settingsWindowFactory;
    private readonly Func<TemplatePackageManagerWindow> _templateManagerWindowFactory;
    private readonly ILogger<AiTemplateAssistantViewModel> _logger;
    private readonly Dispatcher _dispatcher;

    /// <summary>
    /// 参考文件校验器，写/改两 Tab 共用：上传时按 F04 配置逐项校验并读取内容快照，
    /// 发送时对共享上下文快照复核，校验逻辑与改模板 Tab 共用不重复实现。
    /// </summary>
    private readonly ReferenceFileValidator _referenceFileValidator = new();

    /// <summary>
    /// 生成在途操作的取消源，重复生成或停止/关闭时取消上次未完成的调用。
    /// </summary>
    private CancellationTokenSource? _generationCts;

    /// <summary>
    /// 样例表详情读取的取消源，选中表快速切换时取消上次未完成的读取。
    /// </summary>
    private CancellationTokenSource? _detailCts;

    /// <summary>
    /// 样例表候选集刷新的取消源，重复刷新或取消时中断在途读取。
    /// </summary>
    private CancellationTokenSource? _refreshCts;

    /// <summary>
    /// 技术栈描述，必填，如"Java + MyBatis-Plus，三层分层"。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private string _techStackDescription = string.Empty;

    /// <summary>
    /// 是否正在调用生成服务，期间展示进度并禁用输入与生成按钮。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInputPanelVisible))]
    [NotifyPropertyChangedFor(nameof(IsProgressVisible))]
    [NotifyPropertyChangedFor(nameof(IsIdleVisible))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshSampleTablesCommand))]
    private bool _isGenerating;

    /// <summary>
    /// 是否正在读取样例表详情，期间禁止触发生成防止使用未就绪的样例元数据。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshSampleTablesCommand))]
    [NotifyPropertyChangedFor(nameof(SampleTableStatusText))]
    private bool _isLoadingSampleDetail;

    /// <summary>
    /// 生成是否已成功落库，驱动成功面板展示与输入面板隐藏。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInputPanelVisible))]
    [NotifyPropertyChangedFor(nameof(IsSuccessVisible))]
    [NotifyPropertyChangedFor(nameof(IsIdleVisible))]
    private bool _generationSucceeded;

    /// <summary>
    /// 生成是否失败，驱动失败面板展示（错误清单+保留原文+重试）。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFailureVisible))]
    [NotifyPropertyChangedFor(nameof(IsIdleVisible))]
    [NotifyCanExecuteChangedFor(nameof(CopyRawOutputCommand))]
    private bool _generationFailed;

    /// <summary>
    /// 宿主当前状态文本，展示加载样例表、生成进度与阶段结果。
    /// </summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    /// 样例表候选集中当前选中的表行，选中后触发真实列元数据惰性读取。
    /// </summary>
    [ObservableProperty]
    private TableRowViewModel? _selectedSampleRow;

    /// <summary>
    /// 样例表完整元数据，选中后惰性读取，注入生成请求前已含真实列集合。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyPropertyChangedFor(nameof(SampleTableStatusText))]
    private TableInfo? _sampleTable;

    /// <summary>
    /// 生成成功后的模板包包名。
    /// </summary>
    [ObservableProperty]
    private string? _generatedPackageName;

    /// <summary>
    /// 生成成功后的模板包落库目录绝对路径。
    /// </summary>
    [ObservableProperty]
    private string? _generatedTemplateDir;

    /// <summary>
    /// 失败时保留的原始 LLM 输出文本，供人工修复，仅结果页展示不落日志。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyRawOutputCommand))]
    private string _rawLlmOutput = string.Empty;

    /// <summary>
    /// 窗口级共享参考文件栏当前选中的参考文件项，供单项移除命令使用。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveReferenceFileCommand))]
    private AiReferenceFileItem? _selectedReferenceFile;

    /// <summary>
    /// 是否正在执行参考文件校验与读取，期间禁止重复添加，防止并发添加以过期数量/大小放行超限。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddReferenceFilesCommand))]
    private bool _isAddingReferenceFiles;

    /// <summary>
    /// 窗口级共享参考文件上下文，写/改两 Tab 共读共改的窗口级内存状态，关闭即弃不持久化。
    /// 由宿主持有并在构造时传入改模板 Tab 视图模型，保证两个 Tab 引用同一实例。
    /// </summary>
    public AiAssistantSharedContext SharedContext { get; } = new();

    /// <summary>
    /// 改模板 Tab 视图模型，与写模板 Tab 共享同一窗口级参考文件上下文实例；
    /// 由宿主组合创建，随窗口关闭即弃。
    /// </summary>
    public AiTemplateModifyTabViewModel ModifyTabViewModel { get; }

    /// <summary>
    /// 共享参考文件清单，绑定窗口级共享栏列表，写/改两 Tab 的 UI 均引用此集合。
    /// </summary>
    public ObservableCollection<AiReferenceFileItem> ReferenceFiles => SharedContext.ReferenceFiles;

    /// <summary>
    /// 样例表候选集，向导打开时从主窗口①区快照，或经“刷新样例表”按当前连接加载。
    /// </summary>
    public ObservableCollection<TableRowViewModel> AvailableTables { get; } = new();

    /// <summary>
    /// 失败原因清单，逐条展示 LLM 错误、解析错误、校验错误与包名冲突。
    /// </summary>
    public ObservableCollection<string> Errors { get; } = new();

    /// <summary>
    /// 输入面板是否可见：生成中与生成成功后隐藏，失败后恢复可见供修改重试。
    /// </summary>
    public bool IsInputPanelVisible => !IsGenerating && !GenerationSucceeded;

    /// <summary>
    /// 进度面板是否可见，生成期间展示状态文本与不确定进度条。
    /// </summary>
    public bool IsProgressVisible => IsGenerating;

    /// <summary>
    /// 成功面板是否可见，生成成功后展示包名、落库目录与跳转模板包管理入口。
    /// </summary>
    public bool IsSuccessVisible => GenerationSucceeded;

    /// <summary>
    /// 失败面板是否可见，生成失败后展示错误清单、保留原文与重试入口。
    /// </summary>
    public bool IsFailureVisible => GenerationFailed;

    /// <summary>
    /// 待生成引导是否可见：既未在生成、也未成功或失败时展示待生成提示，对齐状态机“待生成”初始态。
    /// </summary>
    public bool IsIdleVisible => !IsGenerating && !GenerationSucceeded && !GenerationFailed;

    /// <summary>
    /// 样例表状态提示文本，按当前选中、读取中与候选集空态组合展示引导。
    /// </summary>
    public string SampleTableStatusText
    {
        get
        {
            if (SampleTable is not null)
            {
                return $"样例表“{SampleTable.RawName}”：列 {SampleTable.Columns.Count}，主键 {SampleTable.PrimaryKeys.Count}";
            }

            if (IsLoadingSampleDetail)
            {
                return "正在读取样例表元数据…";
            }

            return AvailableTables.Count == 0
                ? "暂无可选表，请先选择数据源并刷新表清单，或点击“刷新样例表”。"
                : "请选择样例表，选中后自动读取真实列元数据。";
        }
    }

    /// <summary>
    /// 窗口级共享参考文件栏摘要文本，按当前数量与总大小组合展示。
    /// </summary>
    public string ReferenceFileSummaryText
    {
        get
        {
            int count = SharedContext.ReferenceFiles.Count;
            if (count == 0)
            {
                return "未添加参考文件";
            }

            return $"共 {count} 个，总大小 {FormatFileSize(SharedContext.TotalSizeBytes)}";
        }
    }

    /// <summary>
    /// 使用配置服务、AI 模板生成服务、当前连接服务、表元数据服务、表列表视图模型、
    /// 对话框服务、二次确认服务、文件选择服务、设置窗口工厂与模板包管理窗口工厂构造 AI 模板助手宿主视图模型。
    /// </summary>
    /// <param name="configService">配置服务，读取 LLM 配置与 F04 参考文件限制校验配置。</param>
    /// <param name="templateAiGenerator">AI 模板生成服务，承载提示词组装、调用与落库闭环。</param>
    /// <param name="currentDataSourceService">当前连接共享状态服务，读取当前连接加载样例表候选集。</param>
    /// <param name="tableCatalogService">表元数据编排服务，读取样例表真实列元数据。</param>
    /// <param name="tableListViewModel">主窗口①表列表区视图模型，提供样例表候选集来源。</param>
    /// <param name="dialogService">消息提示服务，用于引导与错误反馈。</param>
    /// <param name="confirmDialogService">二次确认服务，用于未配置跳设置页与同名覆盖确认。</param>
    /// <param name="filePickerService">文件选择服务，用于参考文件多选对话框。</param>
    /// <param name="settingsWindowFactory">设置窗口工厂，供未配置 LLM 时跳转设置页。</param>
    /// <param name="templateManagerWindowFactory">模板包管理窗口工厂，供生成成功后跳转查看新包。</param>
    /// <param name="templateViewModel">②区模板编辑器视图模型，改模板 Tab 目标展示与应用入口的事实源。</param>
    /// <param name="templateAiModifier">AI 改模板对话服务，供改模板 Tab 调用。</param>
    /// <param name="modifyTabLogger">改模板 Tab 视图模型日志器，日志不记录模板正文、指令与参考文件内容。</param>
    /// <param name="logger">宿主视图模型日志器，日志不输出 apiKey、LLM 原始输出与参考文件内容。</param>
    /// <exception cref="ArgumentNullException">任一依赖参数为 null 时抛出。</exception>
    public AiTemplateAssistantViewModel(
        IConfigService configService,
        ITemplateAiGenerator templateAiGenerator,
        ICurrentDataSourceService currentDataSourceService,
        TableCatalogService tableCatalogService,
        TableListViewModel tableListViewModel,
        IDialogService dialogService,
        IConfirmDialogService confirmDialogService,
        IFilePickerService filePickerService,
        Func<SettingsWindow> settingsWindowFactory,
        Func<TemplatePackageManagerWindow> templateManagerWindowFactory,
        TemplateViewModel templateViewModel,
        ITemplateAiModifier templateAiModifier,
        ILogger<AiTemplateModifyTabViewModel> modifyTabLogger,
        ILogger<AiTemplateAssistantViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(templateAiGenerator);
        ArgumentNullException.ThrowIfNull(currentDataSourceService);
        ArgumentNullException.ThrowIfNull(tableCatalogService);
        ArgumentNullException.ThrowIfNull(tableListViewModel);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(confirmDialogService);
        ArgumentNullException.ThrowIfNull(filePickerService);
        ArgumentNullException.ThrowIfNull(settingsWindowFactory);
        ArgumentNullException.ThrowIfNull(templateManagerWindowFactory);
        ArgumentNullException.ThrowIfNull(templateViewModel);
        ArgumentNullException.ThrowIfNull(templateAiModifier);
        ArgumentNullException.ThrowIfNull(modifyTabLogger);
        ArgumentNullException.ThrowIfNull(logger);

        _configService = configService;
        _templateAiGenerator = templateAiGenerator;
        _currentDataSourceService = currentDataSourceService;
        _tableCatalogService = tableCatalogService;
        _tableListViewModel = tableListViewModel;
        _dialogService = dialogService;
        _confirmDialogService = confirmDialogService;
        _filePickerService = filePickerService;
        _settingsWindowFactory = settingsWindowFactory;
        _templateManagerWindowFactory = templateManagerWindowFactory;
        _logger = logger;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        // 组合创建改模板 Tab 视图模型，传入宿主自持的同一共享参考文件上下文实例，保证写/改两 Tab 共读共改
        ModifyTabViewModel = new AiTemplateModifyTabViewModel(
            templateAiModifier,
            templateViewModel,
            configService,
            dialogService,
            confirmDialogService,
            settingsWindowFactory,
            modifyTabLogger,
            SharedContext);

        // 订阅共享参考文件集合变更，驱动共享栏摘要文本与移除/清空命令可用性刷新
        SharedContext.ReferenceFiles.CollectionChanged += OnReferenceFilesCollectionChanged;
    }

    /// <summary>
    /// 宿主窗口呈现后初始化：快照样例表候选集并执行 LLM 配置检查，未配置时提示跳转设置页。
    /// </summary>
    public async Task InitializeAsync()
    {
        // 从主窗口①表列表区快照当前已加载表作为样例表候选集，宿主为非模态窗口期间表区可继续变化
        AvailableTables.Clear();
        foreach (TableRowViewModel row in _tableListViewModel.TableRows)
        {
            AvailableTables.Add(row);
        }

        OnPropertyChanged(nameof(SampleTableStatusText));

        // 候选集非空且尚无选中样例表时默认选第一行，自动读取真实列元数据
        if (SelectedSampleRow is null && AvailableTables.Count > 0)
        {
            SelectedSampleRow = AvailableTables[0];
        }

        await EnsureLlmConfiguredAsync();
    }

    /// <summary>
    /// 取消在途任务：取消生成、样例表读取与刷新在途取消源，并取消改模板 Tab 在途发送，
    /// 供窗口关闭钩子调用，取消经取消令牌贯穿核心调用链。
    /// </summary>
    public void CancelPendingWork()
    {
        CancelPendingGeneration();
        ModifyTabViewModel.CancelPendingSend();
    }

    /// <summary>
    /// 取消在途生成与样例表读取，供宿主关闭或停止时调用，核心调用链经取消令牌感知取消。
    /// </summary>
    public void CancelPendingGeneration()
    {
        _generationCts?.Cancel();
        _detailCts?.Cancel();
        _refreshCts?.Cancel();
    }

    /// <summary>
    /// 添加参考文件：调多选文件对话框，按 F04 配置逐项校验数量/单文件/总大小并读取文本快照，
    /// 全部通过才整体加入共享上下文，任一失败整体拒绝并逐文件列出原因。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddReferenceFiles))]
    private async Task AddReferenceFilesAsync()
    {
        IsAddingReferenceFiles = true;
        try
        {
            IReadOnlyList<string> paths = await _filePickerService.PickOpenReferenceFilesAsync();
            if (paths.Count == 0)
            {
                return;
            }

            // 按 F04 限制配置一次性校验候选文件（数量/单文件/总大小），全部通过才整体加入共享上下文
            AiReferenceFileLimits limits = _configService.Current.AiReferenceFileLimits;
            ReferenceFileValidator.ReferenceFileValidationResult result = await _referenceFileValidator.ValidateAndReadAsync(
                paths,
                SharedContext.ReferenceFiles.Count,
                SharedContext.TotalSizeBytes,
                limits);

            // 任一文件失败整体拒绝并逐文件列出原因，全部通过才整体加入
            if (!result.IsValid)
            {
                _dialogService.ShowError($"参考文件添加失败：\n{string.Join("\n", result.Errors)}");
                return;
            }

            SharedContext.AddItems(result.Items);
            _logger.LogInformation("添加参考文件 {Count} 个，当前共 {Total} 个。", result.Items.Count, SharedContext.ReferenceFiles.Count);
        }
        finally
        {
            IsAddingReferenceFiles = false;
        }
    }

    /// <summary>
    /// 判定添加参考文件命令是否可执行：未在校验读取参考文件时可添加，防止并发添加绕过限制校验。
    /// </summary>
    private bool CanAddReferenceFiles() => !IsAddingReferenceFiles;

    /// <summary>
    /// 移除共享参考文件栏当前选中的单个参考文件项并同步更新总大小。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveReferenceFile))]
    private void RemoveReferenceFile()
    {
        if (SelectedReferenceFile is null)
        {
            return;
        }

        string fileName = SelectedReferenceFile.FileName;
        SharedContext.RemoveItem(SelectedReferenceFile);
        SelectedReferenceFile = null;
        _logger.LogInformation("移除参考文件 {FileName}，当前共 {Total} 个。", fileName, SharedContext.ReferenceFiles.Count);
    }

    /// <summary>
    /// 判定移除命令是否可执行：共享栏存在选中参考文件项时可移除。
    /// </summary>
    private bool CanRemoveReferenceFile() => SelectedReferenceFile is not null;

    /// <summary>
    /// 清空共享参考文件栏全部参考文件项并复位总大小。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanClearReferenceFiles))]
    private void ClearReferenceFiles()
    {
        SharedContext.Clear();
        SelectedReferenceFile = null;
        _logger.LogInformation("清空参考文件清单。");
    }

    /// <summary>
    /// 判定清空命令是否可执行：共享参考文件清单非空时可清空。
    /// </summary>
    private bool CanClearReferenceFiles() => SharedContext.ReferenceFiles.Count > 0;

    /// <summary>
    /// 共享参考文件集合变更回调：刷新共享栏摘要文本与移除/清空命令可用性。
    /// </summary>
    private void OnReferenceFilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ReferenceFileSummaryText));
        RemoveReferenceFileCommand.NotifyCanExecuteChanged();
        ClearReferenceFilesCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 触发模板包生成：先校验 LLM 配置与向导输入，再经 AI 模板生成服务生成并落库；
    /// 用户包同名冲突向导内覆盖确认后重试，内置包同名只读拒绝直接失败，取消贯穿全程。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        // 待生成 → 配置检查中：进入进度面板并校验 LLM 配置与样例表真实元数据就绪，任一不满足不进入生成
        IsGenerating = true;
        StatusText = "正在检查生成配置…";
        try
        {
            if (!await EnsureLlmConfiguredAsync())
            {
                StatusText = string.Empty;
                return;
            }

            if (SampleTable is null)
            {
                StatusText = string.Empty;
                return;
            }

            // 配置检查中：发送时对共享上下文参考文件快照复核 F04 限制，超限则整体拒绝进入失败面板
            IReadOnlyList<AiReferenceFileItem> referenceFiles = SharedContext.Snapshot();
            IReadOnlyList<string> referenceErrors = _referenceFileValidator.ValidateSnapshot(
                referenceFiles, _configService.Current.AiReferenceFileLimits);
            if (referenceErrors.Count > 0)
            {
                GenerationSucceeded = false;
                GenerationFailed = false;
                Errors.Clear();
                RawLlmOutput = string.Empty;
                ShowFailure(new AiTemplateGenerationResult
                {
                    IsSuccess = false,
                    Errors = referenceErrors.ToList(),
                    RawLlmOutput = null
                });
                return;
            }

            // 生成中：捕获输入与参考文件快照，重试与覆盖确认复用同一请求，避免期间用户修改造成不一致
            var request = new AiTemplateGenerationRequest
            {
                TechStackDescription = TechStackDescription.Trim(),
                SampleTable = SampleTable,
                ReferenceFiles = referenceFiles
            };

            _generationCts?.Cancel();
            _generationCts?.Dispose();
            _generationCts = new CancellationTokenSource();
            CancellationToken ct = _generationCts.Token;

            await RunGenerationLoopAsync(request, ct);
        }
        finally
        {
            IsGenerating = false;
        }
    }

    /// <summary>
    /// 生成主循环：以覆盖标志驱动调用生成服务，用户包同名冲突确认覆盖后以覆盖标志重试，
    /// 内置包同名只读拒绝与其它失败统一走失败展示，取消由取消令牌抛出中止。
    /// </summary>
    /// <param name="request">生成请求，技术栈描述与样例表元数据。</param>
    /// <param name="ct">取消令牌，贯穿生成调用链。</param>
    private async Task RunGenerationLoopAsync(AiTemplateGenerationRequest request, CancellationToken ct)
    {
        bool overwrite = false;
        while (true)
        {
            IsGenerating = true;
            GenerationSucceeded = false;
            GenerationFailed = false;
            Errors.Clear();
            RawLlmOutput = string.Empty;
            StatusText = overwrite ? "正在覆盖写入模板库…" : "正在调用 LLM 生成模板包…";

            try
            {
                AiTemplateGenerationResult result = await _templateAiGenerator.GenerateAsync(request, overwrite, ct);
                if (result.IsSuccess)
                {
                    GenerationSucceeded = true;
                    GeneratedPackageName = result.PackageName;
                    GeneratedTemplateDir = result.TemplateDir;
                    StatusText = "模板包生成成功。";
                    _logger.LogInformation("AI 生成模板包成功，包名 {PackageName}。", result.PackageName);
                    return;
                }

                // 用户包同名冲突：向导内询问覆盖确认，确认后以覆盖标志重试，取消则终止本次生成
                if (IsUserPackageNameConflict(result))
                {
                    StatusText = "检测到同名用户包，等待确认覆盖…";
                    bool confirmed = await _confirmDialogService.ConfirmAsync(
                        "同名用户包已存在",
                        $"{result.Errors[0]}\n是否确认覆盖同名用户包？");
                    if (!confirmed)
                    {
                        StatusText = "已取消覆盖，未写入模板库。";
                        _dialogService.ShowInfo("已取消覆盖，未写入模板库。");
                        return;
                    }

                    overwrite = true;
                    continue;
                }

                // 内置包同名只读拒绝与其它失败统一走失败展示，错误清单与保留原文供人工修复
                ShowFailure(result);
                return;
            }
            catch (OperationCanceledException)
            {
                StatusText = "生成已取消。";
                return;
            }
            finally
            {
                IsGenerating = false;
            }
        }
    }

    /// <summary>
    /// 停止当前生成：取消在途生成取消源，核心调用链感知取消后由生成循环捕获并复位状态。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _generationCts?.Cancel();
        StatusText = "正在取消生成…";
    }

    /// <summary>
    /// 判定停止命令是否可执行：生成进行中时可停止。
    /// </summary>
    private bool CanCancel() => IsGenerating;

    /// <summary>
    /// 刷新样例表候选集：按当前连接读取表清单重建候选集，供未在①区加载表时选择样例。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefreshSampleTables))]
    private async Task RefreshSampleTablesAsync()
    {
        DataSourceConfig? config = _currentDataSourceService.Current;
        if (config is null)
        {
            _dialogService.ShowError("请先在主窗口工具栏选择数据源，再刷新样例表。");
            return;
        }

        CancellationTokenSource refreshCts = new();
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = refreshCts;
        CancellationToken ct = refreshCts.Token;

        IsLoadingSampleDetail = true;
        try
        {
            IReadOnlyList<TableInfo> tables = await _tableCatalogService.GetTablesAsync(config, ct);
            RunOnUiThread(() =>
            {
                // 重建候选集并清空旧选中与详情，随后由用户重新选择样例表
                AvailableTables.Clear();
                foreach (TableInfo table in tables)
                {
                    AvailableTables.Add(new TableRowViewModel(table, null));
                }

                SelectedSampleRow = null;
                SampleTable = null;
                OnPropertyChanged(nameof(SampleTableStatusText));
                StatusText = $"样例表候选集已刷新，共 {AvailableTables.Count} 张表。";

                // 候选集非空时默认选第一行，让刷新后样例表自动就绪
                if (SelectedSampleRow is null && AvailableTables.Count > 0)
                {
                    SelectedSampleRow = AvailableTables[0];
                }
            });
        }
        catch (OperationCanceledException)
        {
            // 重复刷新或取消在途读取不提示用户
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "刷新样例表候选集失败，连接名 {ConnectionName}。", config.Name);
            RunOnUiThread(() => _dialogService.ShowError($"刷新样例表失败：{exception.Message}"));
        }
        finally
        {
            // 仅当本次刷新仍是最近一次刷新时复位加载中状态，防快速刷新的旧读取误复位
            if (ReferenceEquals(_refreshCts, refreshCts))
            {
                IsLoadingSampleDetail = false;
            }
        }
    }

    /// <summary>
    /// 判定刷新样例表命令是否可执行：未在读取样例表且未在生成时可刷新。
    /// </summary>
    private bool CanRefreshSampleTables() => !IsLoadingSampleDetail && !IsGenerating;

    /// <summary>
    /// 复制原始 LLM 输出到剪贴板，供用户粘贴到外部编辑器人工修复。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCopyRawOutput))]
    private void CopyRawOutput()
    {
        if (string.IsNullOrEmpty(RawLlmOutput))
        {
            return;
        }

        try
        {
            Clipboard.SetText(RawLlmOutput);
            _dialogService.ShowInfo("已复制原始 LLM 输出到剪贴板，可粘贴到外部编辑器人工修复。");
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // 剪贴板被占用或不可访问时给出可读错误，不中断宿主
            _dialogService.ShowError($"复制失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 判定复制原文命令是否可执行：生成失败且存在保留原文时可复制。
    /// </summary>
    private bool CanCopyRawOutput() => GenerationFailed && !string.IsNullOrEmpty(RawLlmOutput);

    /// <summary>
    /// 生成成功后跳转模板包管理窗口，新包已落库可在此勾选使用。
    /// </summary>
    [RelayCommand]
    private void OpenTemplateManager()
    {
        try
        {
            TemplatePackageManagerWindow window = _templateManagerWindowFactory();
            window.Owner = Application.Current?.MainWindow;
            window.ShowDialog();
        }
        catch (Exception exception)
        {
            // 窗口创建或展示失败时给出可读提示，不中断宿主
            _dialogService.ShowError($"打开模板包管理窗口失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 判定生成命令是否可执行：未在生成、样例表已就绪、技术栈描述非空且未在读取样例表。
    /// </summary>
    private bool CanGenerate()
    {
        return !IsGenerating
            && !IsLoadingSampleDetail
            && !string.IsNullOrWhiteSpace(TechStackDescription)
            && SampleTable is not null;
    }

    /// <summary>
    /// 样例表选中行变化后先清空旧详情，随后异步惰性读取完整列元数据供生成请求注入。
    /// </summary>
    /// <param name="value">变更后的样例表行，取消选中时为 null。</param>
    partial void OnSelectedSampleRowChanged(TableRowViewModel? value)
    {
        SampleTable = null;
        if (value is null)
        {
            return;
        }

        _ = LoadSampleTableDetailAsync(value);
    }

    /// <summary>
    /// 惰性读取选中样例表的完整列元数据：缓存命中直接返回，未命中读取后缓存；
    /// 读取期间选中行已切换则丢弃本次结果，避免过期元数据覆盖新选中表。
    /// </summary>
    /// <param name="row">当前选中的样例表行。</param>
    private async Task LoadSampleTableDetailAsync(TableRowViewModel row)
    {
        DataSourceConfig? config = _currentDataSourceService.Current;
        if (config is null)
        {
            return;
        }

        CancellationTokenSource detailCts = new();
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = detailCts;
        CancellationToken ct = detailCts.Token;

        string targetName = row.RawName;
        IsLoadingSampleDetail = true;
        try
        {
            TableInfo detail = await _tableCatalogService.GetTableDetailAsync(config, targetName, ct);
            RunOnUiThread(() =>
            {
                // 仅当选中行仍为目标表时写入样例表详情，防过期结果覆盖新选中表
                if (SelectedSampleRow is not null
                    && string.Equals(SelectedSampleRow.RawName, targetName, StringComparison.Ordinal))
                {
                    SampleTable = detail;
                    StatusText = $"样例表“{targetName}”已加载，列数量 {detail.Columns.Count}。";
                }
            });
        }
        catch (OperationCanceledException)
        {
            // 选中表快速切换或取消读取不提示用户
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception, "读取样例表详情失败，连接名 {ConnectionName}，表名 {TableName}。", config.Name, targetName);
            RunOnUiThread(() => _dialogService.ShowError($"读取样例表“{targetName}”元数据失败：{exception.Message}"));
        }
        finally
        {
            // 仅当本次读取仍是最近一次读取时复位加载中状态，防快速切换的旧读取误复位
            if (ReferenceEquals(_detailCts, detailCts))
            {
                IsLoadingSampleDetail = false;
            }
        }
    }

    /// <summary>
    /// 确保 LLM 已配置：apiKey 密文非空视为已配置；未配置时提示跳转设置页，
    /// 设置窗口关闭后重新读取内存快照判断是否已配置。
    /// </summary>
    /// <returns>LLM 已配置返回 true，未配置且用户未跳转配置返回 false。</returns>
    private async Task<bool> EnsureLlmConfiguredAsync()
    {
        if (IsLlmConfigured())
        {
            return true;
        }

        bool goToSettings = await _confirmDialogService.ConfirmAsync(
            "LLM 未配置", "尚未配置 LLM API Key，无法生成模板。是否立即前往设置页配置？");
        if (!goToSettings)
        {
            return false;
        }

        // 打开设置窗口，配置保存后内存快照同步更新，返回后据最新快照判断是否可生成
        OpenSettingsWindow();
        return IsLlmConfigured();
    }

    /// <summary>
    /// 判断 LLM 是否已配置：读取配置服务内存快照的 apiKey 密文，非空视为已配置。
    /// </summary>
    private bool IsLlmConfigured()
    {
        AppConfig config = _configService.Current;
        return config.Llm is not null && !string.IsNullOrWhiteSpace(config.Llm.ApiKeyEncrypted);
    }

    /// <summary>
    /// 打开设置窗口，供未配置 LLM 时引导用户前往配置 API Key。
    /// </summary>
    private void OpenSettingsWindow()
    {
        try
        {
            SettingsWindow window = _settingsWindowFactory();
            window.Owner = Application.Current?.MainWindow;
            window.ShowDialog();
        }
        catch (Exception exception)
        {
            // 设置窗口创建或展示失败时给出可读提示，不阻断宿主继续使用
            _dialogService.ShowError($"打开设置窗口失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 判定生成失败结果是否为用户包同名冲突：Core 对用户包同名返回
    /// “同名用户包…需确认覆盖”错误，据此进入向导内覆盖确认分支。
    /// </summary>
    /// <param name="result">生成失败结果。</param>
    /// <returns>用户包同名冲突返回 true，否则返回 false。</returns>
    private static bool IsUserPackageNameConflict(AiTemplateGenerationResult result)
    {
        return result.Errors.Count == 1
            && result.Errors[0].Contains("同名用户包", StringComparison.Ordinal);
    }

    /// <summary>
    /// 应用失败结果：回填错误清单与保留原文，展示失败面板供人工修复后重试。
    /// </summary>
    /// <param name="result">生成失败结果，含错误清单与原始 LLM 输出。</param>
    private void ShowFailure(AiTemplateGenerationResult result)
    {
        GenerationFailed = true;
        foreach (string error in result.Errors)
        {
            Errors.Add(error);
        }

        RawLlmOutput = result.RawLlmOutput ?? string.Empty;
        StatusText = "模板包生成失败，请按错误清单修正后重试。";
        _logger.LogWarning("AI 生成模板包失败，错误数 {ErrorCount}。", result.Errors.Count);
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

    /// <summary>
    /// 将字节数格式化为可读文件大小文本，供共享栏摘要与校验错误提示展示。
    /// </summary>
    /// <param name="bytes">字节数。</param>
    /// <returns>带单位的可读大小文本。</returns>
    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.#} KB";
        }

        return $"{bytes / (1024.0 * 1024.0):0.##} MB";
    }
}
