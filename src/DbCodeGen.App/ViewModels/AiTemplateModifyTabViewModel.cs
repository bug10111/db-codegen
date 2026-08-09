using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbCodeGen.App.Services;
using DbCodeGen.App.Views;
using DbCodeGen.Core.Ai;
using DbCodeGen.Core.Config;
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
/// 「AI 模板助手」改模板 Tab 视图模型（App.Ai）：以对话方式修改②区当前打开的模板文件——
/// 展示当前修改目标（订阅 TemplateViewModel.PropertyChanged 实时刷新）、维护用户/AI 会话消息、
/// 发送修改请求（快照编辑器最新内容 + 共享参考文件集合 + 多轮历史）、执行「应用到编辑器」目标一致性守卫
/// （目标文件比对 + 内容一致性确认）后整体替换编辑器文本并置脏。
/// 与写模板 Tab 共享同一窗口级参考文件上下文实例；会话历史仅成功轮追加，失败/取消轮不追加避免上下文污染。
/// </summary>
public sealed partial class AiTemplateModifyTabViewModel : ObservableObject
{
    private readonly ITemplateAiModifier _templateAiModifier;
    private readonly TemplateViewModel _templateViewModel;
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
    /// 使用改模板对话服务、②区模板编辑器视图模型、配置服务、对话框服务、二次确认服务、设置窗口工厂、
    /// 日志器与窗口级共享参考文件上下文构造改模板 Tab 视图模型。
    /// 共享上下文由宿主视图模型传入同一实例，与写模板 Tab 共读共改。
    /// </summary>
    /// <param name="templateAiModifier">改模板对话服务，承载 LLM 调用与结果解析。</param>
    /// <param name="templateViewModel">②区模板编辑器视图模型，目标展示与应用入口的事实源。</param>
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
        IConfigService configService,
        IDialogService dialogService,
        IConfirmDialogService confirmDialogService,
        Func<SettingsWindow> settingsWindowFactory,
        ILogger<AiTemplateModifyTabViewModel> logger,
        AiAssistantSharedContext sharedContext)
    {
        ArgumentNullException.ThrowIfNull(templateAiModifier);
        ArgumentNullException.ThrowIfNull(templateViewModel);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(confirmDialogService);
        ArgumentNullException.ThrowIfNull(settingsWindowFactory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(sharedContext);

        _templateAiModifier = templateAiModifier;
        _templateViewModel = templateViewModel;
        _configService = configService;
        _dialogService = dialogService;
        _confirmDialogService = confirmDialogService;
        _settingsWindowFactory = settingsWindowFactory;
        _logger = logger;
        _sharedContext = sharedContext;

        // 订阅②区模板编辑器状态与文档加载/关闭事件，实时刷新当前修改目标展示与发送可用性
        _templateViewModel.PropertyChanged += OnTemplateViewModelPropertyChanged;
        _templateViewModel.LoadDocumentRequested += OnTemplateDocumentLoadRequested;
        _templateViewModel.ClearDocumentRequested += OnTemplateDocumentClearRequested;
        OnPropertyChanged(nameof(TargetDisplayText));
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
    /// ②区模板文档加载回调：文档开关不变的文件切换也会触发，保证目标展示的包名与相对路径实时刷新。
    /// </summary>
    /// <param name="_">新载入的模板文本，目标展示不依赖正文内容。</param>
    private void OnTemplateDocumentLoadRequested(string _)
    {
        RefreshTargetDisplay();
    }

    /// <summary>
    /// ②区模板文档关闭回调：目标回落到"未选择模板文件"并刷新发送可用性。
    /// </summary>
    private void OnTemplateDocumentClearRequested()
    {
        RefreshTargetDisplay();
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
    /// 发送修改指令：前置校验②区已打开文件、指令非空与 LLM 已配置，
    /// 快照②区编辑器最新内容与共享参考文件集合，调用改模板对话服务并追加会话消息。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        // 前置校验：②区已打开模板文件才可发送，未打开时提示先选择文件
        if (!_templateViewModel.HasDocument)
        {
            _dialogService.ShowInfo("请先在②区打开一个模板文件。");
            return;
        }

        // 前置校验：修改指令必填非空
        if (string.IsNullOrWhiteSpace(InstructionText))
        {
            _dialogService.ShowInfo("请输入修改指令。");
            return;
        }

        // 前置校验：LLM 未配置时引导跳设置页，未配置完成则不发送
        if (!await EnsureLlmConfiguredAsync())
        {
            return;
        }

        string instruction = InstructionText.Trim();
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
    /// 判定发送命令是否可执行：空闲、②区已打开文件且指令非空时可发送。
    /// </summary>
    private bool CanSend()
    {
        return !IsSending && _templateViewModel.HasDocument && !string.IsNullOrWhiteSpace(InstructionText);
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
