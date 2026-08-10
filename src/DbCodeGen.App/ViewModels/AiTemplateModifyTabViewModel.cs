using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbCodeGen.App.Services;
using DbCodeGen.App.Views;
using DbCodeGen.Core.Ai;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.App.ViewModels;

/// <summary>
/// 改模板对话会话状态：空闲 / 发送中 / 结果已返回，多轮以「结果已返回 → 发送中」回环，失败与取消回空闲。
/// </summary>
internal enum AiModifySessionState
{
    /// <summary>
    /// 空闲：可输入指令发送，可接收放弃结果。
    /// </summary>
    Idle,

    /// <summary>
    /// 发送中：在途调用 LLM，可停止取消。
    /// </summary>
    Sending,

    /// <summary>
    /// 结果已返回：AI 结果可应用到编辑器、可放弃，可继续追加指令回环。
    /// </summary>
    ResultReady
}

/// <summary>
/// 「AI 模板助手」改模板 Tab 视图模型（App.Ai）：以对话方式修改模板文件——
/// 维护「选择要修改的模板」多选面板（勾选当前包文件，默认勾选②区当前文件）、用户/AI 会话消息、
/// 发送修改请求并按下述两条链路分流：
/// 单文件模式（仅勾选②区当前文件）走既有「AI 单文件 → 结果已返回 → 应用到编辑器」流程不回退，
/// 含目标一致性守卫与内置包编辑-复制保存路径；
/// 多文件模式（勾选多个文件或勾选非当前文件）单次调用 LLM 一次返回全部文件结果、成功结果直接写当前用户包、
/// 当前文件被改时经 ApplyExternalWriteToCurrentFile 刷新编辑器并复位脏标记。
/// 与写模板 Tab 共享同一窗口级参考文件上下文实例；会话历史仅成功轮追加，失败/取消轮不追加避免上下文污染。
/// </summary>
public sealed partial class AiTemplateModifyTabViewModel : ObservableObject
{
    private readonly ITemplateAiModifier _templateAiModifier;
    private readonly TemplateViewModel _templateViewModel;
    private readonly TemplateFileWriter _templateFileWriter;
    private readonly IConfigService _configService;
    private readonly IDialogService _dialogService;
    private readonly IConfirmDialogService _confirmDialogService;
    private readonly Func<SettingsWindow> _settingsWindowFactory;
    private readonly ILogger<AiTemplateModifyTabViewModel> _logger;
    private readonly AiAssistantSharedContext _sharedContext;

    /// <summary>
    /// 会话历史消息（LlmChatMessage），多轮对话全量携带；仅成功轮追加 user+assistant，失败/取消轮不追加。
    /// </summary>
    private readonly List<LlmChatMessage> _historyMessages = new();

    /// <summary>
    /// 在途发送操作的取消源，停止或窗口关闭时取消调用。
    /// </summary>
    private CancellationTokenSource? _modifyCts;

    /// <summary>
    /// 本次可应用结果与发送时目标快照，供「应用到编辑器」目标一致性守卫比对。
    /// </summary>
    private PendingModifyResult? _pendingResult;

    /// <summary>
    /// 会话状态，驱动发送中/结果面板/输入区可用性。
    /// </summary>
    private AiModifySessionState _state = AiModifySessionState.Idle;

    /// <summary>
    /// 全选/清空批量更新勾选期间抑制逐项刷新，循环结束统一刷新一次，避免冗余通知。
    /// </summary>
    private bool _isBulkUpdatingSelection;

    /// <summary>
    /// 修改指令输入文本，必填非空才可发送。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _instructionText = string.Empty;

    /// <summary>
    /// 会话消息列表，用户指令与 AI 结果气泡按加入顺序展示。
    /// </summary>
    public ObservableCollection<AiModifyChatItem> Messages { get; } = new();

    /// <summary>
    /// 「选择要修改的模板」多选面板勾选项，按当前模板包文件重建，默认勾选②区当前编辑文件。
    /// </summary>
    public ObservableCollection<ModifyFileSelectionItem> SelectableFiles { get; } = new();

    /// <summary>
    /// 是否空闲：可输入指令并发送。
    /// </summary>
    public bool IsIdle => _state == AiModifySessionState.Idle;

    /// <summary>
    /// 是否发送中：展示停止按钮并禁用输入与发送。
    /// </summary>
    public bool IsSending => _state == AiModifySessionState.Sending;

    /// <summary>
    /// 是否结果已返回：展示「应用到编辑器」「放弃」操作入口。
    /// </summary>
    public bool IsResultReady => _state == AiModifySessionState.ResultReady;

    /// <summary>
    /// 当前修改目标展示文本：②区包名 / 相对路径 + 脏标记；未打开文件时展示"未选择模板文件"。
    /// </summary>
    public string TargetDisplayText
    {
        get
        {
            if (!_templateViewModel.HasDocument)
            {
                return "未选择模板文件";
            }

            string packageName = _templateViewModel.CurrentPackage?.Name ?? "-";
            string filePath = _templateViewModel.CurrentFileRelativePath ?? "-";
            string dirtySuffix = _templateViewModel.IsDirty ? "    ● 未保存" : string.Empty;
            return $"{packageName} / {filePath}{dirtySuffix}";
        }
    }

    /// <summary>
    /// 多选面板勾选摘要文本：按当前勾选数量组合展示，供面板底部引导用户确认批量范围。
    /// </summary>
    public string SelectionInfoText
    {
        get
        {
            int count = SelectableFiles.Count(item => item.IsSelected);
            return count == 0 ? "未选择文件" : $"已选择 {count} 个文件";
        }
    }

    /// <summary>
    /// 使用改模板对话服务、②区模板编辑器视图模型、模板文件读写服务、配置服务、对话框服务、
    /// 二次确认服务、设置窗口工厂、日志器与窗口级共享参考文件上下文构造改模板 Tab 视图模型。
    /// 共享上下文由宿主视图模型传入同一实例，与写模板 Tab 共读共改。
    /// </summary>
    /// <param name="templateAiModifier">改模板对话服务，承载 LLM 调用与结果解析。</param>
    /// <param name="templateViewModel">②区模板编辑器视图模型，目标展示与应用入口的事实源。</param>
    /// <param name="templateFileWriter">模板文件读写服务，批量修改读盘与写盘保存。</param>
    /// <param name="configService">配置服务，读取 LLM 配置与 apiKey 是否已配置。</param>
    /// <param name="dialogService">消息提示服务，用于前置校验与守卫失败提示。</param>
    /// <param name="confirmDialogService">二次确认服务，用于 LLM 未配置跳设置与内容一致性覆盖确认。</param>
    /// <param name="settingsWindowFactory">设置窗口工厂，供 LLM 未配置时引导跳转设置页。</param>
    /// <param name="logger">改模板 Tab 视图模型日志器，不记录模板正文、指令、参考文件内容与 LLM 原文。</param>
    /// <param name="sharedContext">窗口级共享参考文件上下文，与写模板 Tab 同一实例，发送时取快照。</param>
    /// <exception cref="ArgumentNullException">任一依赖参数为 null 时抛出。</exception>
    public AiTemplateModifyTabViewModel(
        ITemplateAiModifier templateAiModifier,
        TemplateViewModel templateViewModel,
        TemplateFileWriter templateFileWriter,
        IConfigService configService,
        IDialogService dialogService,
        IConfirmDialogService confirmDialogService,
        Func<SettingsWindow> settingsWindowFactory,
        ILogger<AiTemplateModifyTabViewModel> logger,
        AiAssistantSharedContext sharedContext)
    {
        ArgumentNullException.ThrowIfNull(templateAiModifier);
        ArgumentNullException.ThrowIfNull(templateViewModel);
        ArgumentNullException.ThrowIfNull(templateFileWriter);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(confirmDialogService);
        ArgumentNullException.ThrowIfNull(settingsWindowFactory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(sharedContext);

        _templateAiModifier = templateAiModifier;
        _templateViewModel = templateViewModel;
        _templateFileWriter = templateFileWriter;
        _configService = configService;
        _dialogService = dialogService;
        _confirmDialogService = confirmDialogService;
        _settingsWindowFactory = settingsWindowFactory;
        _logger = logger;
        _sharedContext = sharedContext;

        // 订阅②区模板编辑器状态与文档加载/关闭事件，实时刷新当前修改目标展示、发送可用性与多选面板
        _templateViewModel.PropertyChanged += OnTemplateViewModelPropertyChanged;
        _templateViewModel.LoadDocumentRequested += OnTemplateDocumentLoadRequested;
        _templateViewModel.ClearDocumentRequested += OnTemplateDocumentClearRequested;
        OnPropertyChanged(nameof(TargetDisplayText));
        RebuildSelectableFiles();
    }

    /// <summary>
    /// ②区模板编辑器状态变化回调：目标展示依赖的文档开关与脏标记变化时刷新展示文本与发送可用性。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">属性变化事件参数。</param>
    private void OnTemplateViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TemplateViewModel.HasDocument)
            || e.PropertyName == nameof(TemplateViewModel.IsDirty))
        {
            RefreshTargetDisplay();
        }
    }

    /// <summary>
    /// ②区模板文档加载回调：文档开关不变的文件切换也会触发，保证目标展示的包名与相对路径实时刷新，
    /// 并按当前包文件重建多选面板、默认勾选当前编辑文件。
    /// </summary>
    /// <param name="_">新载入的模板文本，目标展示与多选面板不依赖正文内容。</param>
    private void OnTemplateDocumentLoadRequested(string _)
    {
        RefreshTargetDisplay();
        RebuildSelectableFiles();
    }

    /// <summary>
    /// ②区模板文档关闭回调：目标回落到"未选择模板文件"，按当前包文件重建多选面板（无当前文件可默认勾选）。
    /// </summary>
    private void OnTemplateDocumentClearRequested()
    {
        RefreshTargetDisplay();
        RebuildSelectableFiles();
    }

    /// <summary>
    /// 刷新当前修改目标展示文本与发送命令可用性，供文档加载/关闭/脏标记变化回调复用。
    /// </summary>
    private void RefreshTargetDisplay()
    {
        OnPropertyChanged(nameof(TargetDisplayText));
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 按②区当前模板包文件重建多选面板勾选项：先解绑旧项订阅再重建，默认勾选②区当前编辑文件；
    /// 当前包未加载时清空面板。重建后刷新勾选摘要与全选/清空命令可用性。
    /// </summary>
    private void RebuildSelectableFiles()
    {
        // 解绑旧勾选项的勾选变更订阅，避免悬挂引用与重复刷新
        foreach (ModifyFileSelectionItem item in SelectableFiles)
        {
            item.PropertyChanged -= OnSelectionItemPropertyChanged;
        }

        SelectableFiles.Clear();

        TemplatePackageInfo? package = _templateViewModel.CurrentPackage;
        string? currentPath = _templateViewModel.CurrentFileRelativePath;
        if (package is null)
        {
            RefreshSelectionState();
            return;
        }

        // 遍历当前包文件重建可选项，相对路径与②区当前编辑文件一致时默认勾选
        foreach (TemplateFileInfo file in package.Files)
        {
            if (file is null)
            {
                continue;
            }

            bool isCurrent = string.Equals(file.RelativeTemplatePath, currentPath, StringComparison.OrdinalIgnoreCase);
            ModifyFileSelectionItem item = new(file, isCurrent);
            item.PropertyChanged += OnSelectionItemPropertyChanged;
            SelectableFiles.Add(item);
        }

        RefreshSelectionState();
    }

    /// <summary>
    /// 勾选项勾选状态变化回调：单次勾选影响发送可用性与勾选摘要，统一刷新相关状态；
    /// 全选/清空批量更新期间跳过逐项刷新，由批量命令末尾统一刷新。
    /// </summary>
    /// <param name="sender">变更的勾选项。</param>
    /// <param name="e">属性变化事件参数。</param>
    private void OnSelectionItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModifyFileSelectionItem.IsSelected) && !_isBulkUpdatingSelection)
        {
            RefreshSelectionState();
        }
    }

    /// <summary>
    /// 刷新勾选摘要与发送/全选/清空命令可用性，供勾选变化、面板重建与全选清空命令复用。
    /// </summary>
    private void RefreshSelectionState()
    {
        OnPropertyChanged(nameof(SelectionInfoText));
        SendCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 全选当前包内全部待修改文件，随后刷新勾选摘要与命令可用性。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSelectAll))]
    private void SelectAll()
    {
        // 批量置勾选期间抑制逐项刷新，循环结束统一刷新一次
        _isBulkUpdatingSelection = true;
        try
        {
            foreach (ModifyFileSelectionItem item in SelectableFiles)
            {
                item.IsSelected = true;
            }
        }
        finally
        {
            _isBulkUpdatingSelection = false;
        }

        RefreshSelectionState();
    }

    /// <summary>
    /// 判定全选命令是否可执行：面板非空且存在未勾选项时可全选。
    /// </summary>
    private bool CanSelectAll()
    {
        return SelectableFiles.Count > 0 && SelectableFiles.Any(item => !item.IsSelected);
    }

    /// <summary>
    /// 清空全部勾选，随后刷新勾选摘要与命令可用性。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanClearSelection))]
    private void ClearSelection()
    {
        // 批量置勾选期间抑制逐项刷新，循环结束统一刷新一次
        _isBulkUpdatingSelection = true;
        try
        {
            foreach (ModifyFileSelectionItem item in SelectableFiles)
            {
                item.IsSelected = false;
            }
        }
        finally
        {
            _isBulkUpdatingSelection = false;
        }

        RefreshSelectionState();
    }

    /// <summary>
    /// 判定清空命令是否可执行：存在已勾选项时可清空。
    /// </summary>
    private bool CanClearSelection()
    {
        return SelectableFiles.Any(item => item.IsSelected);
    }

    /// <summary>
    /// 收集当前勾选的待修改文件项，供发送分流判断与批量请求组装使用。
    /// </summary>
    /// <returns>勾选文件项清单。</returns>
    private List<ModifyFileSelectionItem> GetSelectedItems()
    {
        return SelectableFiles.Where(item => item.IsSelected).ToList();
    }

    /// <summary>
    /// 发送修改指令：先做指令非空与勾选非空前置校验，再按勾选范围分流——
    /// 仅勾选②区当前文件走单文件预览-应用流程，其余走批量修改并直接写盘流程。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        // 前置校验：修改指令必填非空
        if (string.IsNullOrWhiteSpace(InstructionText))
        {
            _dialogService.ShowInfo("请输入修改指令。");
            return;
        }

        // 前置校验：至少勾选一个待修改文件，未勾选时提示先选择
        List<ModifyFileSelectionItem> selectedItems = GetSelectedItems();
        if (selectedItems.Count == 0)
        {
            _dialogService.ShowInfo("请至少勾选一个要修改的模板文件。");
            return;
        }

        // 前置校验：LLM 未配置时引导跳设置页，未配置完成则不发送
        if (!await EnsureLlmConfiguredAsync())
        {
            return;
        }

        string instruction = InstructionText.Trim();

        // 单文件模式判定：仅勾选②区当前文件时保持既有单文件预览-应用流程不回退
        string? currentFilePath = _templateViewModel.CurrentFileRelativePath;
        bool singleFileMode = selectedItems.Count == 1
            && currentFilePath is not null
            && string.Equals(selectedItems[0].RelativePath, currentFilePath, StringComparison.OrdinalIgnoreCase);

        if (singleFileMode)
        {
            await SendSingleFileAsync(instruction);
        }
        else
        {
            await SendBatchAsync(instruction, selectedItems);
        }
    }

    /// <summary>
    /// 判定发送命令是否可执行：非发送中、指令非空且多选面板存在已勾选文件时可发送。
    /// </summary>
    private bool CanSend()
    {
        return !IsSending
            && !string.IsNullOrWhiteSpace(InstructionText)
            && SelectableFiles.Any(item => item.IsSelected);
    }

    /// <summary>
    /// 单文件修改发送：快照②区当前文件最新内容与共享参考文件集合，调用单文件改模板服务，
    /// 成功进入结果已返回状态供「应用到编辑器」目标一致性守卫后回填，失败与取消回空闲。
    /// </summary>
    /// <param name="instruction">修改指令。</param>
    private async Task SendSingleFileAsync(string instruction)
    {
        // 前置校验：②区已打开模板文件才可发送，未打开时提示先选择文件（单文件模式判定已保证，防御性兜底）
        if (!_templateViewModel.HasDocument)
        {
            _dialogService.ShowInfo("请先在②区打开一个模板文件。");
            return;
        }

        // 校验通过后清空指令输入框，避免重发时残留旧指令
        InstructionText = string.Empty;

        // 快照②区当前修改目标与编辑器最新内容（含未保存编辑，用户所见即所改，不用磁盘原文）
        string packageName = _templateViewModel.CurrentPackage?.Name ?? string.Empty;
        string filePath = _templateViewModel.CurrentFileRelativePath ?? string.Empty;
        string contentSnapshot = _templateViewModel.EditorText;

        // 追加用户指令气泡，随后取共享参考文件集合快照构造请求
        Messages.Add(new AiModifyChatItem(AiModifyChatRole.User, instruction, false, null));
        IReadOnlyList<AiReferenceFileItem> referenceFiles = _sharedContext.Snapshot();

        var request = new AiModifyTemplateRequest
        {
            CurrentTemplateFilePath = filePath,
            CurrentTemplateContent = contentSnapshot,
            ModificationInstruction = instruction,
            ReferenceFiles = referenceFiles,
            HistoryMessages = _historyMessages.ToList()
        };

        // 复用取消源，保证上一轮在途调用被取消后再发起新一轮
        _modifyCts?.Cancel();
        _modifyCts?.Dispose();
        _modifyCts = new CancellationTokenSource();
        CancellationToken ct = _modifyCts.Token;

        SetState(AiModifySessionState.Sending);
        _logger.LogInformation("AI 改模板发送开始，目标文件 {FilePath}。", filePath);

        try
        {
            AiModifyTemplateResult result = await _templateAiModifier.ModifyAsync(request, ct);
            if (result.IsSuccess && !string.IsNullOrEmpty(result.NewContent))
            {
                // 仅成功轮追加 user+assistant 至历史，供下一轮多轮对话以上一轮结果为上下文回环
                _historyMessages.Add(new LlmChatMessage { Role = "user", Content = instruction });
                _historyMessages.Add(new LlmChatMessage { Role = "assistant", Content = result.NewContent });

                // 记录本次可应用结果与发送时目标快照，供「应用到编辑器」目标一致性守卫比对
                _pendingResult = new PendingModifyResult
                {
                    PackageName = packageName,
                    FilePath = filePath,
                    ContentSnapshot = contentSnapshot,
                    NewContent = result.NewContent
                };

                Messages.Add(new AiModifyChatItem(AiModifyChatRole.Ai, string.Empty, true, result.NewContent));
                SetState(AiModifySessionState.ResultReady);
                _logger.LogInformation(
                    "AI 改模板成功，目标文件 {FilePath}，结果字符数 {ContentLength}。", filePath, result.NewContent.Length);
            }
            else
            {
                // 失败轮不追加历史：展示错误清单，状态回空闲可修正指令重试
                string errorText = result.Errors.Count > 0 ? string.Join("\n", result.Errors) : "AI 改模板失败，请重试。";
                Messages.Add(new AiModifyChatItem(AiModifyChatRole.Ai, errorText, false, null));
                SetState(AiModifySessionState.Idle);
                _logger.LogWarning("AI 改模板失败，目标文件 {FilePath}，错误数 {ErrorCount}。", filePath, result.Errors.Count);
            }
        }
        catch (OperationCanceledException)
        {
            // 用户停止或窗口关闭取消在途调用：不追加历史，回空闲
            Messages.Add(new AiModifyChatItem(AiModifyChatRole.Ai, "发送已取消。", false, null));
            SetState(AiModifySessionState.Idle);
        }
        catch (Exception exception)
        {
            // 调用异常：不追加历史，展示可读错误回空闲
            _logger.LogError(exception, "AI 改模板调用异常，目标文件 {FilePath}。", filePath);
            Messages.Add(new AiModifyChatItem(AiModifyChatRole.Ai, $"调用失败：{exception.Message}", false, null));
            SetState(AiModifySessionState.Idle);
        }
    }

    /// <summary>
    /// 批量修改发送：当前包必须为用户包（内置包只读中止），逐文件收集内容（当前文件取编辑器、其余读盘），
    /// 单次调用 LLM 一次返回全部文件修改结果，AI 成功结果逐文件写盘，当前文件被改时刷新编辑器并复位脏标记，
    /// 完成后追加 user 指令 + assistant 紧凑摘要历史与批量结果气泡，状态回空闲。
    /// </summary>
    /// <param name="instruction">修改指令。</param>
    /// <param name="selectedItems">勾选的待修改文件项。</param>
    private async Task SendBatchAsync(string instruction, List<ModifyFileSelectionItem> selectedItems)
    {
        // 前置校验：当前包已加载才可批量修改，未加载时提示先选择模板包
        TemplatePackageInfo? package = _templateViewModel.CurrentPackage;
        if (package is null)
        {
            _dialogService.ShowInfo("请先在②区选择模板包。");
            return;
        }

        // 内置包只读安全边界：批量修改直接写盘无法落盘，提示先复制为可编辑用户包，中止本次批量
        if (package.IsBuiltin)
        {
            _dialogService.ShowError("内置包只读，无法批量修改保存，请先在②区/模板包管理复制为可编辑用户包后再操作。");
            return;
        }

        // 校验通过后清空指令输入框，避免重发时残留旧指令
        InstructionText = string.Empty;

        string packageName = package.Name;
        string? currentFilePath = _templateViewModel.CurrentFileRelativePath;

        // 追加用户指令气泡，随后取共享参考文件集合快照
        Messages.Add(new AiModifyChatItem(AiModifyChatRole.User, instruction, false, null));
        IReadOnlyList<AiReferenceFileItem> referenceFiles = _sharedContext.Snapshot();

        // 复用取消源，保证上一轮在途调用被取消后再发起新一轮
        _modifyCts?.Cancel();
        _modifyCts?.Dispose();
        _modifyCts = new CancellationTokenSource();
        CancellationToken ct = _modifyCts.Token;

        // 批量不产生可应用的单文件结果，清除更早单文件轮遗留的可应用结果，防旧气泡「应用到编辑器」误覆盖已写盘内容
        _pendingResult = null;

        SetState(AiModifySessionState.Sending);
        _logger.LogInformation(
            "AI 改模板批量发送开始，包 {PackageName}，勾选文件数 {FileCount}。", packageName, selectedItems.Count);

        try
        {
            // 收集各文件内容：②区当前文件取编辑器最新内容（含未保存编辑），其余从磁盘读取；
            // 读取失败记入本地失败清单，不进入 AI 批量请求，不中断其它文件
            var aiFiles = new List<AiModifyFileItem>(selectedItems.Count);
            var readFailures = new List<AiModifyFileResult>();
            string? currentFileAiInput = null;
            foreach (ModifyFileSelectionItem item in selectedItems)
            {
                if (string.Equals(item.RelativePath, currentFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    string editorContent = _templateViewModel.EditorText;
                    currentFileAiInput = editorContent;
                    aiFiles.Add(new AiModifyFileItem { RelativePath = item.RelativePath, Content = editorContent });
                    continue;
                }

                try
                {
                    string content = await _templateFileWriter.ReadAsync(package, item.RelativePath, ct);
                    aiFiles.Add(new AiModifyFileItem { RelativePath = item.RelativePath, Content = content });
                }
                catch (Exception exception) when (exception is TemplatePackageException or IOException or UnauthorizedAccessException)
                {
                    // 单文件读取失败记入失败清单，继续处理其它文件，不中断批量
                    _logger.LogWarning(exception, "AI 改模板批量读取文件失败，路径 {RelativePath}。", item.RelativePath);
                    readFailures.Add(AiModifyFileResult.ForFailure(item.RelativePath, $"读取模板文件失败：{exception.Message}"));
                }
            }

            // 无任何可修改文件时跳过 AI 调用，避免空请求进入 LLM；有文件时调用批量改模板服务
            AiModifyMultipleResult modifyResult;
            if (aiFiles.Count > 0)
            {
                var request = new AiModifyMultipleRequest
                {
                    PackageName = packageName,
                    Files = aiFiles,
                    ModificationInstruction = instruction,
                    ReferenceFiles = referenceFiles,
                    HistoryMessages = _historyMessages.ToList()
                };
                modifyResult = await _templateAiModifier.ModifyMultipleAsync(request, ct);
            }
            else
            {
                modifyResult = AiModifyMultipleResult.Failed(new List<string> { "所有勾选文件均无法读取。" });
            }

            // 合并读失败与 AI 逐文件结果，统一按文件粒度写盘与汇总，保证失败文件不落盘
            var allFileResults = new List<AiModifyFileResult>(readFailures);
            allFileResults.AddRange(modifyResult.FileResults);

            // 逐文件写盘：AI 成功结果写当前用户包，写盘失败记入该文件错误；
            // 写盘为本地小文件操作且结果已完整产出，用不可取消令牌保证已收集结果不被半途丢弃
            var savedPaths = new List<string>();
            var failedEntries = new List<(string Path, string Error)>();
            foreach (AiModifyFileResult fileResult in allFileResults)
            {
                if (!fileResult.IsSuccess)
                {
                    failedEntries.Add((fileResult.RelativePath, fileResult.Error ?? "AI 修改失败。"));
                    continue;
                }

                TemplateSaveResult saveResult = await _templateFileWriter.WriteAsync(
                    package, fileResult.RelativePath, fileResult.NewContent!, CancellationToken.None);
                if (saveResult.IsSuccess)
                {
                    savedPaths.Add(fileResult.RelativePath);
                    _logger.LogInformation(
                        "AI 改模板批量写盘成功，包 {PackageName}，相对路径 {RelativePath}。", packageName, fileResult.RelativePath);
                }
                else
                {
                    failedEntries.Add((fileResult.RelativePath, saveResult.Message ?? "写盘失败。"));
                }
            }

            // 批量级错误（文件清单为空等）并入失败清单首部，随气泡逐条展示
            if (modifyResult.Errors.Count > 0)
            {
                var batchErrors = new List<(string Path, string Error)>(modifyResult.Errors.Count);
                foreach (string error in modifyResult.Errors)
                {
                    batchErrors.Add(("批量修改", error));
                }

                batchErrors.AddRange(failedEntries);
                failedEntries = batchErrors;
            }

            // 当前②区文件被批量写盘成功时刷新编辑器并复位脏标记，触发预览重渲染；
            // 批量期间②区编辑器被用户键入时先二次确认，防批量写盘结果静默覆盖未保存键入
            string? savedCurrentContent = null;
            foreach (AiModifyFileResult fileResult in allFileResults)
            {
                if (fileResult.IsSuccess
                    && string.Equals(fileResult.RelativePath, currentFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    savedCurrentContent = fileResult.NewContent;
                    break;
                }
            }

            if (savedCurrentContent is not null && currentFilePath is not null)
            {
                // 仅当②区当前文件仍为批量修改时的目标文件才处理编辑器刷新，用户已切换文件则不打扰
                bool stillOnTargetFile = string.Equals(
                    _templateViewModel.CurrentFileRelativePath, currentFilePath, StringComparison.OrdinalIgnoreCase);
                if (stillOnTargetFile)
                {
                    // 批量期间②区编辑器被用户键入时先二次确认，防批量写盘结果静默覆盖未保存键入
                    bool editorChangedDuringBatch = currentFileAiInput is not null
                        && !string.Equals(_templateViewModel.EditorText, currentFileAiInput, StringComparison.Ordinal);
                    if (!editorChangedDuringBatch
                        || await _confirmDialogService.ConfirmAsync(
                            "检测到编辑器内容已在批量修改期间被修改",
                            "检测到编辑器内容已在批量修改期间被修改，是否以批量写盘结果刷新编辑器？"))
                    {
                        _templateViewModel.ApplyExternalWriteToCurrentFile(currentFilePath, savedCurrentContent);
                    }
                }
            }

            // 历史：仅当批量实际写盘存在成功结果且未被取消时追加 user 指令 + assistant 紧凑摘要，
            // 摘要以实际写盘结果为准而非 AI 解析结果（写盘失败的文件记失败），与单文件「仅成功轮追加」契约一致
            bool hasSuccessfulResult = savedPaths.Count > 0;
            if (hasSuccessfulResult && !ct.IsCancellationRequested)
            {
                _historyMessages.Add(new LlmChatMessage { Role = "user", Content = instruction });

                // 按文件路径建立写盘失败原因映射与成功集合，供逐文件摘要判定；批量级合成错误项不参与逐文件摘要
                var savedPathSet = new HashSet<string>(savedPaths, StringComparer.OrdinalIgnoreCase);
                var failedByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach ((string path, string error) in failedEntries)
                {
                    failedByPath.TryAdd(path, error);
                }

                var summaryLines = new List<string>(allFileResults.Count);
                foreach (AiModifyFileResult fileResult in allFileResults)
                {
                    if (savedPathSet.Contains(fileResult.RelativePath))
                    {
                        summaryLines.Add($"{fileResult.RelativePath}：成功");
                    }
                    else
                    {
                        string error = failedByPath.TryGetValue(fileResult.RelativePath, out string? failedError)
                            && failedError is not null
                            ? failedError
                            : "写盘失败。";
                        summaryLines.Add($"{fileResult.RelativePath}：{error}");
                    }
                }

                _historyMessages.Add(new LlmChatMessage { Role = "assistant", Content = string.Join("\n", summaryLines) });
            }

            // 会话气泡：多行摘要展示 ✅ 已保存 N 个 / ❌ 失败 M 个逐文件列出，已自动写盘无需「应用到编辑器」
            var bubbleLines = new List<string>();
            if (savedPaths.Count > 0)
            {
                bubbleLines.Add($"✅ 已保存 {savedPaths.Count} 个文件");
            }

            if (failedEntries.Count > 0)
            {
                bubbleLines.Add($"❌ 失败 {failedEntries.Count} 个：");
                foreach ((string path, string error) in failedEntries)
                {
                    bubbleLines.Add($"  {path}：{error}");
                }
            }

            if (bubbleLines.Count == 0)
            {
                bubbleLines.Add("批量修改未产生任何结果。");
            }

            Messages.Add(new AiModifyChatItem(AiModifyChatRole.Ai, string.Join("\n", bubbleLines), false, null));
            _logger.LogInformation(
                "AI 改模板批量完成，包 {PackageName}，成功 {SuccessCount} 个，失败 {FailureCount} 个。",
                packageName,
                savedPaths.Count,
                failedEntries.Count);
            SetState(AiModifySessionState.Idle);
        }
        catch (OperationCanceledException)
        {
            // 用户停止或窗口关闭取消在途批量调用：不追加历史，回空闲
            Messages.Add(new AiModifyChatItem(AiModifyChatRole.Ai, "批量发送已取消。", false, null));
            SetState(AiModifySessionState.Idle);
        }
        catch (Exception exception)
        {
            // 调用异常：不追加历史，展示可读错误回空闲
            _logger.LogError(exception, "AI 改模板批量调用异常，包 {PackageName}。", packageName);
            Messages.Add(new AiModifyChatItem(AiModifyChatRole.Ai, $"批量修改调用失败：{exception.Message}", false, null));
            SetState(AiModifySessionState.Idle);
        }
    }

    /// <summary>
    /// 停止发送：取消在途 LLM 调用，核心调用链经取消令牌感知取消后由发送流程捕获复位状态。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _modifyCts?.Cancel();
        _logger.LogInformation("AI 改模板发送已请求取消。");
    }

    /// <summary>
    /// 判定停止命令是否可执行：发送中时可停止。
    /// </summary>
    private bool CanCancel() => IsSending;

    /// <summary>
    /// 应用 AI 结果到②区编辑器：先执行目标一致性守卫（目标文件比对 + 内容一致性确认），
    /// 通过后整体替换编辑器文本并置脏，触发替换文档与预览渲染，不直接写盘。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyToEditor))]
    private async Task ApplyToEditorAsync()
    {
        if (_pendingResult is null)
        {
            return;
        }

        // 守卫一：目标文件比对——发送时包名+相对路径与当前②区目标一致才可应用，防跨文件误替换
        string currentPackageName = _templateViewModel.CurrentPackage?.Name ?? string.Empty;
        string currentFilePath = _templateViewModel.CurrentFileRelativePath ?? string.Empty;
        if (AiModifyTargetGuard.IsTargetChanged(
                currentPackageName, currentFilePath, _pendingResult.PackageName, _pendingResult.FilePath))
        {
            _dialogService.ShowError("②区当前模板文件已变化，无法应用本次结果。");
            _pendingResult = null;
            SetState(AiModifySessionState.Idle);
            return;
        }

        // 守卫二：内容一致性确认——发送后②区编辑器内容被手动编辑时弹确认，确认才覆盖、放弃则不应用且结果保留
        if (AiModifyTargetGuard.IsContentChanged(_templateViewModel.EditorText, _pendingResult.ContentSnapshot))
        {
            bool confirmed = await _confirmDialogService.ConfirmAsync(
                "检测到编辑器内容已在发送后被修改",
                "检测到编辑器内容已在发送后被修改，是否仍以 AI 结果覆盖？");
            if (!confirmed)
            {
                SetState(AiModifySessionState.Idle);
                return;
            }
        }

        // 应用 AI 结果：整体替换②区编辑器文本并置脏，触发替换文档与预览渲染，落盘仍走既有保存链路
        _templateViewModel.ApplyAiEditedTemplate(_pendingResult.NewContent);
        _logger.LogInformation("AI 改模板结果已应用到编辑器，目标文件 {FilePath}。", _pendingResult.FilePath);
        _pendingResult = null;
        SetState(AiModifySessionState.Idle);
    }

    /// <summary>
    /// 判定应用命令是否可执行：存在可应用的 AI 结果时可用。
    /// </summary>
    private bool CanApplyToEditor() => _pendingResult is not null;

    /// <summary>
    /// 放弃本次可应用结果：丢弃结果回空闲，对话历史保留供参考，可重新发送或开始新指令。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDiscardResult))]
    private void DiscardResult()
    {
        if (_pendingResult is null)
        {
            return;
        }

        _pendingResult = null;
        SetState(AiModifySessionState.Idle);
    }

    /// <summary>
    /// 判定放弃命令是否可执行：存在可应用的 AI 结果时可用。
    /// </summary>
    private bool CanDiscardResult() => _pendingResult is not null;

    /// <summary>
    /// 查看完整内容：切换指定 AI 消息气泡的完整内容展开/折叠状态。
    /// </summary>
    /// <param name="item">目标消息项，为空时忽略。</param>
    [RelayCommand]
    private void ViewFullContent(AiModifyChatItem? item)
    {
        if (item is null)
        {
            return;
        }

        item.IsFullContentVisible = !item.IsFullContentVisible;
    }

    /// <summary>
    /// 取消在途发送任务，供窗口关闭钩子调用，取消经取消令牌贯穿核心调用链。
    /// </summary>
    public void CancelPendingSend()
    {
        _modifyCts?.Cancel();
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
            "LLM 未配置", "尚未配置 LLM API Key，无法修改模板。是否立即前往设置页配置？");
        if (!goToSettings)
        {
            return false;
        }

        // 打开设置窗口，配置保存后内存快照同步更新，返回后据最新快照判断是否可发送
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
    /// 打开设置窗口，供 LLM 未配置时引导用户前往配置 API Key。
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
            // 设置窗口创建或展示失败时给出可读提示，不阻断改模板会话继续使用
            _dialogService.ShowError($"打开设置窗口失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 切换会话状态并广播相关计算属性与命令可用性，驱动界面在各状态间流转。
    /// </summary>
    /// <param name="newState">目标会话状态。</param>
    private void SetState(AiModifySessionState newState)
    {
        if (_state == newState)
        {
            return;
        }

        _state = newState;
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsSending));
        OnPropertyChanged(nameof(IsResultReady));
        SendCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ApplyToEditorCommand.NotifyCanExecuteChanged();
        DiscardResultCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 待应用的 AI 结果与发送时目标快照，供「应用到编辑器」目标一致性守卫比对。
    /// </summary>
    private sealed class PendingModifyResult
    {
        /// <summary>
        /// 发送时②区目标包名。
        /// </summary>
        public string PackageName { get; init; } = string.Empty;

        /// <summary>
        /// 发送时②区目标文件相对包根路径。
        /// </summary>
        public string FilePath { get; init; } = string.Empty;

        /// <summary>
        /// 发送时②区编辑器内容快照（含未保存编辑），供内容一致性确认比对。
        /// </summary>
        public string ContentSnapshot { get; init; } = string.Empty;

        /// <summary>
        /// AI 返回的完整新文件，供应用到编辑器整体替换。
        /// </summary>
        public string NewContent { get; init; } = string.Empty;
    }
}
