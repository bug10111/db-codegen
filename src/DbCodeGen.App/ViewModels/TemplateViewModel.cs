using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbCodeGen.App.Services;
using DbCodeGen.App.Views;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.App.ViewModels;

/// <summary>
/// 模板编辑器视图模型，承载②模板区的模板包与文件选择、编辑器文本状态、脏标记与保存写回。
/// 内置包保存写回走只读拒绝与复制引导；脏文档切换/关闭经二次确认表达保存/放弃/取消；
/// 保存成功后由 TemplateFileWriter 失效 Content 缓存并派发文件已更新事件，与共享渲染管线闭环。
/// </summary>
public sealed partial class TemplateViewModel : ObservableObject
{
    private readonly ITemplatePackageService _packageService;
    private readonly TemplateFileWriter _templateFileWriter;
    private readonly IDialogService _dialogService;
    private readonly IConfirmDialogService _confirmDialogService;
    private readonly IPromptDialogService _promptDialogService;
    private readonly IConfigService _configService;
    private readonly Func<VariablePanelWindow> _variablePanelWindowFactory;
    private readonly ILogger<TemplateViewModel> _logger;

    /// <summary>
    /// 主窗口 UI 线程调度器，配置保存事件可能在非 UI 线程触发，刷新包列表前经它切回 UI 线程。
    /// </summary>
    private readonly Dispatcher _dispatcher;

    /// <summary>
    /// 当前已加载的模板包运行时信息，提供包根目录与只读标记。
    /// </summary>
    private TemplatePackageInfo? _currentPackage;

    /// <summary>
    /// 当前已加载的模板文件，保存与预览均基于该文件相对路径。
    /// </summary>
    private TemplateFileInfo? _currentFile;

    /// <summary>
    /// 当前编辑器文本对应的磁盘原内容，脏标记按与原文是否一致判断。
    /// </summary>
    private string _originalText = string.Empty;

    /// <summary>
    /// 加载文档期间抑制 TextChanged 置脏，避免载入文本被误判为用户编辑。
    /// </summary>
    private bool _isLoadingDocument;

    /// <summary>
    /// 回退选中项期间抑制再次触发文件/包切换，避免取消确认后循环弹窗。
    /// </summary>
    private bool _isApplyingRollback;

    /// <summary>
    /// 切换前的包选中项，切换确认取消时回退选中框。
    /// </summary>
    private TemplatePackageListItemViewModel? _packageBeforeSwitch;

    /// <summary>
    /// 最近一次选中的模板包，供切换回退记录变化前选中项。
    /// </summary>
    private TemplatePackageListItemViewModel? _lastSelectedPackage;

    /// <summary>
    /// 切换前的文件选中项，切换确认取消时回退文件树。
    /// </summary>
    private TemplateFileInfo? _fileBeforeSwitch;

    /// <summary>
    /// 最近一次选中的模板文件，供切换回退记录变化前选中项。
    /// </summary>
    private TemplateFileInfo? _lastSelectedFile;

    /// <summary>
    /// 变量面板窗口实例，单开复用，关闭后允许重新创建。
    /// </summary>
    private VariablePanelWindow? _variablePanelWindow;

    /// <summary>
    /// 上次应用包顺序记忆的包名指纹，用于配置保存后比对包顺序是否变化，避免无关保存触发包列表重载。
    /// </summary>
    private string[] _lastPackageOrderFingerprint = Array.Empty<string>();

    /// <summary>
    /// 模板包列表，绑定②区包下拉，内置包优先、包名排序由服务契约保证。
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<TemplatePackageListItemViewModel> _packages = new();

    /// <summary>
    /// 模板包下拉选中项，切换时加载对应包的文件树。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTemplateFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteTemplateFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameTemplateFileCommand))]
    private TemplatePackageListItemViewModel? _selectedPackage;

    /// <summary>
    /// 包内模板文件树，绑定②区文件列表，选中文件后加载编辑器。
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<TemplateFileInfo> _files = new();

    /// <summary>
    /// 文件树选中项，切换时经脏文档确认后加载文件内容；变化时联动刷新文件排序命令可用性。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteTemplateFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameTemplateFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveFileUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveFileDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetFileOrderCommand))]
    private TemplateFileInfo? _selectedFile;

    /// <summary>
    /// 当前编辑器文本，由视图层编辑器文本变化事件同步写入。
    /// </summary>
    [ObservableProperty]
    private string _editorText = string.Empty;

    /// <summary>
    /// 脏标记，自上次保存后有改动时为 true，驱动未保存确认。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirtyMark))]
    private bool _isDirty;

    /// <summary>
    /// 是否已加载模板文件，驱动保存命令可用性。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _hasDocument;

    /// <summary>
    /// 当前编辑器高亮语言，由模板文件名推导，视图层据此应用高亮定义。
    /// </summary>
    [ObservableProperty]
    private HighlightLanguage _language = HighlightLanguage.Plain;

    /// <summary>
    /// 是否处于加载或保存等操作繁忙状态，繁忙时禁用②区整体交互。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreatePackageCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddTemplateFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteTemplateFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameTemplateFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveFileUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveFileDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetFileOrderCommand))]
    private bool _isBusy;

    /// <summary>
    /// 状态栏文本，展示加载、保存与只读引导等结果。
    /// </summary>
    [ObservableProperty]
    private string _statusText = "请选择模板包并打开模板文件。";

    /// <summary>
    /// 界面是否空闲可用，与繁忙状态相反，绑定②区整体启用。
    /// </summary>
    public bool IsIdle => !IsBusy;

    /// <summary>
    /// 脏标记展示文本，用于状态栏提示未保存状态。
    /// </summary>
    public string DirtyMark => IsDirty ? "● 未保存" : "已保存";

    /// <summary>
    /// 当前模板包运行时信息，供预览区组装渲染上下文。
    /// </summary>
    public TemplatePackageInfo? CurrentPackage => _currentPackage;

    /// <summary>
    /// 当前已加载模板文件相对包根路径，供 AI 改模板目标展示与目标一致性守卫使用。
    /// 未加载模板文件时返回 null。
    /// </summary>
    public string? CurrentFileRelativePath => _currentFile?.RelativeTemplatePath;

    /// <summary>
    /// 是否持有未保存修改，供主窗口关闭前触发未保存确认。
    /// </summary>
    public bool HasDirtyDocument => IsDirty;

    /// <summary>
    /// 加载文档请求事件，视图层订阅后把文本载入编辑器；携带待载入的模板文本。
    /// </summary>
    public event Action<string>? LoadDocumentRequested;

    /// <summary>
    /// 清空文档请求事件，视图层订阅后清空编辑器内容。
    /// </summary>
    public event Action? ClearDocumentRequested;

    /// <summary>
    /// 高亮语言变更事件，视图层订阅后按语言应用高亮定义。
    /// </summary>
    public event Action<HighlightLanguage>? LanguageChanged;

    /// <summary>
    /// 插入变量表达式请求事件，视图层订阅后在编辑器光标处插入表达式。
    /// </summary>
    public event Action<string>? InsertVariableRequested;

    /// <summary>
    /// 编辑器内容变化事件，预览区订阅后重置防抖并触发渲染。
    /// </summary>
    public event Action<string>? EditorContentChanged;

    /// <summary>
    /// 替换文档请求事件，视图层订阅后在 AvalonEdit 编辑器中整体替换当前文档文本。
    /// AI 改模板「应用到编辑器」经 ApplyAiEditedTemplate 触发，替换后重置撤销栈。
    /// </summary>
    public event Action<string>? ReplaceDocumentRequested;

    /// <summary>
    /// 使用模板包服务、模板文件读写服务、对话框服务、输入提示服务、配置服务、变量面板窗口工厂与日志器构造视图模型。
    /// </summary>
    /// <param name="packageService">模板包管理服务，承载包列表、复制与新建/增删文件能力。</param>
    /// <param name="templateFileWriter">模板文件读写服务，承载读取与保存写回。</param>
    /// <param name="dialogService">消息提示服务，用于加载与保存失败反馈。</param>
    /// <param name="confirmDialogService">二次确认服务，用于脏文档与内置包复制引导确认。</param>
    /// <param name="promptDialogService">文本输入提示服务，用于新建包与新建文件的参数收集。</param>
    /// <param name="configService">配置持久化服务，承载按包记忆的模板勾选态读写。</param>
    /// <param name="variablePanelWindowFactory">变量面板窗口工厂，供变量面板入口按需创建。</param>
    /// <param name="logger">视图模型日志器，日志不记录模板正文与敏感信息。</param>
    /// <exception cref="ArgumentNullException">任一依赖参数为 null 时抛出。</exception>
    public TemplateViewModel(
        ITemplatePackageService packageService,
        TemplateFileWriter templateFileWriter,
        IDialogService dialogService,
        IConfirmDialogService confirmDialogService,
        IPromptDialogService promptDialogService,
        IConfigService configService,
        Func<VariablePanelWindow> variablePanelWindowFactory,
        ILogger<TemplateViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(packageService);
        ArgumentNullException.ThrowIfNull(templateFileWriter);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(confirmDialogService);
        ArgumentNullException.ThrowIfNull(promptDialogService);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(variablePanelWindowFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _packageService = packageService;
        _templateFileWriter = templateFileWriter;
        _dialogService = dialogService;
        _confirmDialogService = confirmDialogService;
        _promptDialogService = promptDialogService;
        _configService = configService;
        _variablePanelWindowFactory = variablePanelWindowFactory;
        _logger = logger;

        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        // 订阅配置保存事件：模板包管理窗口排序落盘后经包顺序指纹比对刷新②区包下拉，实现跨窗口联动
        _configService.ConfigChanged += OnConfigChanged;
    }

    /// <summary>
    /// 异步初始化：加载模板包列表。主窗口呈现后调用。
    /// </summary>
    public async Task InitializeAsync()
    {
        await ReloadPackagesAsync();
    }

    /// <summary>
    /// 解除配置保存事件订阅，供主窗口关闭时调用，避免悬挂引用与重复订阅。
    /// </summary>
    public void Detach()
    {
        _configService.ConfigChanged -= OnConfigChanged;
    }

    /// <summary>
    /// 配置保存事件回调：配置可能在非 UI 线程保存，统一切换到 UI 线程后按包顺序指纹判断是否需要刷新包下拉。
    /// </summary>
    /// <param name="sender">事件发送方。</param>
    /// <param name="e">事件参数。</param>
    private void OnConfigChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.CheckAccess())
        {
            HandleConfigOrderChange();
        }
        else
        {
            _dispatcher.InvokeAsync(HandleConfigOrderChange);
        }
    }

    /// <summary>
    /// 在 UI 线程处理包顺序变化：模板包管理窗口排序落盘后，包顺序记忆指纹变化时重载包列表，
    /// 使②区包下拉即时跟随；勾选态等无关配置保存不触发重载。
    /// </summary>
    private void HandleConfigOrderChange()
    {
        // 读取当前包顺序记忆并比对上次指纹，仅包顺序变化时重载，避免无关保存触发包列表重载
        string[] currentFingerprint = _configService.Current.TemplatePackageOrder.ToArray();
        if (_lastPackageOrderFingerprint.SequenceEqual(currentFingerprint, StringComparer.Ordinal))
        {
            return;
        }

        // 先更新指纹再触发重载，防止重载期间再次收到配置事件重复进入
        _lastPackageOrderFingerprint = currentFingerprint;
        _ = ReloadPackagesAsync();
    }

    /// <summary>
    /// 编辑器文本变化入口，由视图层编辑器 TextChanged 事件调用；加载文档期间的变更直接跳过。
    /// 同步更新编辑器文本与脏标记，并通知预览区重置防抖渲染。
    /// </summary>
    /// <param name="text">编辑器当前完整文本。</param>
    public void NotifyEditorTextChanged(string text)
    {
        if (_isLoadingDocument)
        {
            return;
        }

        EditorText = text;
        IsDirty = !string.Equals(text, _originalText, StringComparison.Ordinal);
        EditorContentChanged?.Invoke(text);
    }

    /// <summary>
    /// 应用 AI 改模板结果：整体替换编辑器文本并相对磁盘原文置脏，随后触发替换文档与预览渲染事件。
    /// 仅修改内存文本，不直接写盘；落盘仍走既有保存链路（用户包写盘 / 内置包只读拒绝并复制引导）。
    /// </summary>
    /// <param name="newContent">AI 返回的完整新模板文件内容。</param>
    public void ApplyAiEditedTemplate(string newContent)
    {
        EditorText = newContent;
        IsDirty = !string.Equals(newContent, _originalText, StringComparison.Ordinal);

        // 通知视图层替换编辑器文本并重置撤销栈，通知预览区按新内容防抖渲染
        ReplaceDocumentRequested?.Invoke(newContent);
        EditorContentChanged?.Invoke(newContent);
    }

    /// <summary>
    /// 应用批量修改写盘后的外部结果到②区编辑器：仅当相对路径与当前编辑文件匹配时，
    /// 以已写盘的新内容刷新编辑器文本并复位脏标记为已保存（磁盘与编辑器一致），
    /// 随后触发载入文档与预览渲染事件；相对路径不匹配时静默忽略（当前编辑文件未被批量修改）。
    /// </summary>
    /// <param name="relativePath">批量修改写盘的文件相对包根路径。</param>
    /// <param name="content">批量修改写盘后的完整新文件内容。</param>
    public void ApplyExternalWriteToCurrentFile(string relativePath, string content)
    {
        if (_currentFile is null
            || !string.Equals(_currentFile.RelativeTemplatePath, relativePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // 载入文本期间抑制置脏，随后以已写盘内容为基准复位脏标记为已保存，保证磁盘与编辑器一致
        _isLoadingDocument = true;
        try
        {
            EditorText = content;
        }
        finally
        {
            _isLoadingDocument = false;
        }

        _originalText = content;
        IsDirty = false;

        // 通知视图层载入已写盘文本并重置光标，通知预览区按新内容防抖渲染
        LoadDocumentRequested?.Invoke(content);
        EditorContentChanged?.Invoke(content);
        _logger.LogInformation("批量修改写盘结果已应用到编辑器，相对路径 {RelativePath}。", relativePath);
    }

    /// <summary>
    /// 请求在编辑器光标处插入变量表达式，未加载文档时提示先选择模板文件。
    /// </summary>
    /// <param name="expression">变量面板生成的 Scriban 表达式。</param>
    public void RequestInsertVariable(string expression)
    {
        if (!HasDocument)
        {
            _dialogService.ShowInfo("请先在模板区选择并加载一个模板文件。");
            return;
        }

        InsertVariableRequested?.Invoke(expression);
    }

    /// <summary>
    /// 文件树 checkbox 勾选态变化入口，由视图层 CheckBox 点击事件调用：
    /// 先按包名持久化当前勾选态，再以 Files 属性变更通知广播给生成栏等消费方，使其重新评估“勾选到层”命令可用性。
    /// </summary>
    public void NotifyFileSelectionChanged()
    {
        PersistFileSelectionStates();
        OnPropertyChanged(nameof(Files));
    }

    /// <summary>
    /// 关闭前确认：存在未保存修改时弹出保存/放弃/取消二次确认，供主窗口关闭事件调用。
    /// </summary>
    /// <returns>确认关闭返回 true，留在当前编辑返回 false。</returns>
    public Task<bool> ConfirmCloseAsync()
    {
        return ConfirmSaveBeforeSwitchAsync("关闭");
    }

    /// <summary>
    /// 刷新模板包列表。
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            await ReloadPackagesAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 打开变量面板窗口：已打开时激活现有窗口，关闭后允许重新创建。
    /// </summary>
    [RelayCommand]
    private void OpenVariablePanel()
    {
        if (_variablePanelWindow is not null && _variablePanelWindow.IsVisible)
        {
            _variablePanelWindow.Activate();
            return;
        }

        _variablePanelWindow = _variablePanelWindowFactory();
        _variablePanelWindow.Owner = Application.Current?.MainWindow;
        _variablePanelWindow.Closed += (_, _) => _variablePanelWindow = null;
        _variablePanelWindow.Show();
    }

    /// <summary>
    /// 新建模板包：询问包名与说明后创建空包，创建成功后刷新包列表并自动选中新包，
    /// 随后通过"新增模板"逐个添加模板文件。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCreatePackage))]
    private async Task CreatePackageAsync()
    {
        // 收集新建包输入：包名必填，说明可空；空包创建不要求首模板文件
        string? packageName = await _promptDialogService.PromptAsync(
            "新建模板包", "请输入新包名（字母/数字/中划线/下划线）：");
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return;
        }

        string name = packageName.Trim();
        string? description = await _promptDialogService.PromptAsync("新建模板包", "请输入包说明（可留空）：", "");
        if (description is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            TemplatePackageOperationResult result = await _packageService.CreatePackageAsync(
                name, description.Trim(), null, null, CancellationToken.None);

            if (result.Status != TemplatePackageOperationStatus.Succeeded || result.Package is null)
            {
                _dialogService.ShowError(result.Message ?? "新建模板包失败。");
                return;
            }

            await ReloadPackagesAsync(defaultSelectFirstPackage: false);
            SelectPackageAndLoad(result.Package.Name);
            _dialogService.ShowInfo($"已新建模板包“{result.Package.Name}”，可点击“新增模板”添加模板文件。");
            _logger.LogInformation("新建模板包成功，包名 {PackageName}。", result.Package.Name);
        }
        catch (Exception exception) when (exception is TemplatePackageException or IOException or UnauthorizedAccessException)
        {
            _dialogService.ShowError($"新建模板包失败：{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 判定新建包命令是否可执行：非繁忙即可创建。
    /// </summary>
    private bool CanCreatePackage() => !IsBusy;

    /// <summary>
    /// 向当前用户包新增模板文件：询问相对路径（可含分组目录）与输出路径，创建成功后刷新文件树并选中新文件载入编辑器。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddTemplateFile))]
    private async Task AddTemplateFileAsync()
    {
        if (SelectedPackage is null || SelectedPackage.IsBuiltin)
        {
            return;
        }

        string packageName = SelectedPackage.Name;
        string? templatePath = await _promptDialogService.PromptAsync(
            "新增模板", $"为模板包“{packageName}”输入新文件相对路径（可含分组目录）：");
        if (string.IsNullOrWhiteSpace(templatePath))
        {
            return;
        }

        string relativePath = templatePath.Trim();
        string? outputPath = await _promptDialogService.PromptAsync(
            "新增模板", "输入该文件的输出相对路径：", relativePath);
        if (outputPath is null)
        {
            return;
        }

        // 新建文件会重建文件树并切换当前文件，脏文档先经二次确认，取消则终止本次新建
        if (IsDirty)
        {
            bool canLeave = await ConfirmSaveBeforeSwitchAsync("新增模板");
            if (!canLeave)
            {
                return;
            }
        }

        string normalizedOutput = outputPath.Trim();
        IsBusy = true;
        try
        {
            TemplatePackageOperationResult result = await _packageService.AddTemplateFileAsync(
                packageName, relativePath, normalizedOutput, CancellationToken.None);

            if (result.Status != TemplatePackageOperationStatus.Succeeded || result.Package is null)
            {
                _dialogService.ShowError(result.Message ?? "新增模板失败。");
                return;
            }

            _currentPackage = result.Package;

            // 重建文件树期间抑制选中项变更，避免旧的选中文件被移出集合时触发空选中确认
            _isApplyingRollback = true;
            try
            {
                ReloadFiles();
            }
            finally
            {
                _isApplyingRollback = false;
            }

            SelectFileAndLoad(relativePath);
            _dialogService.ShowInfo($"已新增模板“{relativePath}”。");
            _logger.LogInformation("新增模板成功，包 {PackageName}，相对路径 {RelativePath}。", packageName, relativePath);
        }
        catch (Exception exception) when (exception is TemplatePackageException or IOException or UnauthorizedAccessException)
        {
            _dialogService.ShowError($"新增模板失败：{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 判定新建文件命令是否可执行：选中用户包（非内置）且非繁忙。
    /// </summary>
    private bool CanAddTemplateFile() => SelectedPackage is not null && !SelectedPackage.IsBuiltin && !IsBusy;

    /// <summary>
    /// 删除当前用户包选中的模板文件：二次确认后删除并同步清单，删除的正是当前编辑文件时关闭文档。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeleteTemplateFile))]
    private async Task DeleteTemplateFileAsync()
    {
        if (SelectedPackage is null || SelectedFile is null)
        {
            return;
        }

        string packageName = SelectedPackage.Name;
        string relativePath = SelectedFile.RelativeTemplatePath;
        bool isCurrentFile = _currentFile is not null
            && string.Equals(_currentFile.RelativeTemplatePath, relativePath, StringComparison.OrdinalIgnoreCase);

        bool confirmed = await _confirmDialogService.ConfirmAsync(
            "删除模板", $"确定要删除模板“{relativePath}”吗？删除后不可恢复。");
        if (!confirmed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            TemplatePackageOperationResult result = await _packageService.DeleteTemplateFileAsync(
                packageName, relativePath, CancellationToken.None);

            if (result.Status != TemplatePackageOperationStatus.Succeeded || result.Package is null)
            {
                _dialogService.ShowError(result.Message ?? "删除模板失败。");
                return;
            }

            _currentPackage = result.Package;

            // 删除的正是当前编辑文件时先关闭文档，随后重建文件树并复位选中项，防悬空选中引用
            if (isCurrentFile)
            {
                CloseCurrentDocument();
            }

            _isApplyingRollback = true;
            try
            {
                ReloadFiles();
                if (isCurrentFile)
                {
                    SelectedFile = null;
                    _lastSelectedFile = null;
                    _fileBeforeSwitch = null;
                }
                else
                {
                    // 删除的是非当前编辑文件时，把选中与切换基线复位到当前编辑文件，防回退指向已删除实例
                    TemplateFileInfo? target = _currentFile is null
                        ? null
                        : Files.FirstOrDefault(file =>
                            string.Equals(file.RelativeTemplatePath, _currentFile.RelativeTemplatePath, StringComparison.OrdinalIgnoreCase));
                    SelectedFile = target;
                    _lastSelectedFile = target;
                    _fileBeforeSwitch = target;
                }
            }
            finally
            {
                _isApplyingRollback = false;
            }

            _dialogService.ShowInfo($"已删除模板“{relativePath}”。");
            _logger.LogInformation("删除模板成功，包 {PackageName}，相对路径 {RelativePath}。", packageName, relativePath);
        }
        catch (Exception exception) when (exception is TemplatePackageException or IOException or UnauthorizedAccessException)
        {
            _dialogService.ShowError($"删除模板失败：{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 判定删除文件命令是否可执行：选中用户包文件且非繁忙，内置包只读禁止删除。
    /// </summary>
    private bool CanDeleteTemplateFile() => SelectedPackage is not null && !SelectedPackage.IsBuiltin && SelectedFile is not null && !IsBusy;

    /// <summary>
    /// 重命名当前用户包选中的模板文件：询问新相对路径后经服务物理改名并同步清单，
    /// 成功后迁移该包勾选态/文件顺序/最近选中记忆中的旧路径为新路径，重建文件树并选中新路径文件。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRenameTemplateFile))]
    private async Task RenameTemplateFileAsync()
    {
        if (SelectedPackage is null || SelectedFile is null)
        {
            return;
        }

        string packageName = SelectedPackage.Name;
        string oldRelativePath = SelectedFile.RelativeTemplatePath;
        bool isCurrentFile = _currentFile is not null
            && string.Equals(_currentFile.RelativeTemplatePath, oldRelativePath, StringComparison.OrdinalIgnoreCase);

        string? newRelativePath = await _promptDialogService.PromptAsync(
            "重命名模板", $"为模板包“{packageName}”输入新相对路径（可含分组目录）：", oldRelativePath);
        if (string.IsNullOrWhiteSpace(newRelativePath))
        {
            return;
        }

        // 重命名输入经包加载器规范化为正斜杠形式（用户可能输入反斜杠），
        // 与服务侧规范化结果保持一致，后续记忆迁移与重启恢复均按同一路径形式匹配
        string normalizedNew = TemplatePackageLoader.NormalizeRelativePath(newRelativePath.Trim());

        // 重命名会重建文件树并切换当前文件，脏文档先经二次确认，取消则终止本次重命名
        if (IsDirty)
        {
            bool canLeave = await ConfirmSaveBeforeSwitchAsync("重命名模板");
            if (!canLeave)
            {
                return;
            }
        }

        IsBusy = true;
        try
        {
            TemplatePackageOperationResult result = await _packageService.RenameTemplateFileAsync(
                packageName, oldRelativePath, normalizedNew, CancellationToken.None);

            if (result.Status != TemplatePackageOperationStatus.Succeeded || result.Package is null)
            {
                _dialogService.ShowError(result.Message ?? "重命名模板失败。");
                return;
            }

            _currentPackage = result.Package;

            // 迁移该包勾选态/文件顺序/最近选中记忆中的旧路径为新路径，随后重建文件树并选中新路径文件
            MigrateFileMemoryPaths(packageName, oldRelativePath, normalizedNew);

            _isApplyingRollback = true;
            try
            {
                ReloadFiles();
            }
            finally
            {
                _isApplyingRollback = false;
            }

            if (isCurrentFile)
            {
                SelectFileAndLoad(normalizedNew);
            }
            else
            {
                // 重命名的不是当前编辑文件时，把选中与切换基线复位到当前编辑文件，防回退指向旧路径实例
                TemplateFileInfo? target = _currentFile is null
                    ? null
                    : Files.FirstOrDefault(file =>
                        string.Equals(file.RelativeTemplatePath, _currentFile.RelativeTemplatePath, StringComparison.OrdinalIgnoreCase));
                SelectedFile = target;
                _lastSelectedFile = target;
                _fileBeforeSwitch = target;
            }

            _dialogService.ShowInfo($"已重命名模板“{oldRelativePath}”为“{normalizedNew}”。");
            _logger.LogInformation(
                "重命名模板成功，包 {PackageName}，旧路径 {OldPath}，新路径 {NewPath}。", packageName, oldRelativePath, normalizedNew);
        }
        catch (Exception exception) when (exception is TemplatePackageException or IOException or UnauthorizedAccessException)
        {
            _dialogService.ShowError($"重命名模板失败：{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 判定重命名文件命令是否可执行：选中用户包文件且非繁忙，内置包只读禁止重命名。
    /// </summary>
    private bool CanRenameTemplateFile() => SelectedPackage is not null && !SelectedPackage.IsBuiltin && SelectedFile is not null && !IsBusy;

    /// <summary>
    /// 迁移指定包内模板文件重命名后的记忆路径：勾选态、文件顺序与最近选中文件中的旧路径替换为新路径，随后落盘。
    /// 写盘失败仅记录警告日志，不打断重命名流程。
    /// </summary>
    /// <param name="packageName">目标模板包包名。</param>
    /// <param name="oldPath">重命名前的相对路径。</param>
    /// <param name="newPath">重命名后的相对路径。</param>
    private void MigrateFileMemoryPaths(string packageName, string oldPath, string newPath)
    {
        AppConfig config = _configService.Current;

        // 勾选态记忆中的旧路径替换为新路径，保持勾选结果不随重命名丢失
        if (config.TemplateFileStates.TryGetValue(packageName, out List<TemplateFileState>? states) && states is not null)
        {
            foreach (TemplateFileState state in states)
            {
                if (state is not null && string.Equals(state.TemplatePath, oldPath, StringComparison.OrdinalIgnoreCase))
                {
                    state.TemplatePath = newPath;
                }
            }
        }

        // 文件顺序记忆中的旧路径替换为新路径，保持展示顺序不随重命名丢失
        if (config.TemplateFileOrder.TryGetValue(packageName, out List<string>? order) && order is not null)
        {
            for (int index = 0; index < order.Count; index++)
            {
                if (string.Equals(order[index], oldPath, StringComparison.OrdinalIgnoreCase))
                {
                    order[index] = newPath;
                }
            }
        }

        // 最近选中文件记忆指向旧路径时同步为新路径，避免下次加载包时按旧路径定位失败
        if (string.Equals(config.LastSelectedTemplateFile, oldPath, StringComparison.OrdinalIgnoreCase))
        {
            config.LastSelectedTemplateFile = newPath;
        }

        try
        {
            _configService.Save();
        }
        catch (ConfigSaveException exception)
        {
            _logger.LogWarning(exception, "重命名模板后记忆迁移保存失败，包 {PackageName}。", packageName);
        }
    }

    /// <summary>
    /// 按包名定位列表项并走正常选中流程加载该包，触发文件树重建与首文件自动载入。
    /// </summary>
    /// <param name="packageName">目标包名。</param>
    private void SelectPackageAndLoad(string packageName)
    {
        TemplatePackageListItemViewModel? match = Packages.FirstOrDefault(item =>
            string.Equals(item.Name, packageName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return;
        }

        _lastSelectedPackage = match;
        SelectedPackage = match;
    }

    /// <summary>
    /// 按相对路径定位文件树项并走正常选中流程载入编辑器，含脏文档二次确认。
    /// 用户输入可能使用反斜杠，先经包加载器规范化到树中展示的正斜杠形式再匹配。
    /// </summary>
    /// <param name="relativePath">目标文件相对路径。</param>
    private void SelectFileAndLoad(string relativePath)
    {
        string normalizedPath = TemplatePackageLoader.NormalizeRelativePath(relativePath);
        TemplateFileInfo? match = Files.FirstOrDefault(file =>
            string.Equals(file.RelativeTemplatePath, normalizedPath, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return;
        }

        _lastSelectedFile = match;
        SelectedFile = match;
    }

    /// <summary>
    /// 保存当前模板文件：内置包先引导复制到用户库再保存，用户包直接写盘。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_currentPackage is null || _currentFile is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            // 内置包只读：先引导复制到用户库，复制成功并以当前文本写盘后视为已保存
            if (_currentPackage.IsBuiltin)
            {
                bool saved = await HandleBuiltinSaveAsync();
                if (saved)
                {
                    StatusText = $"已保存：{_currentFile.RelativeTemplatePath}";
                }

                return;
            }

            await SaveToCurrentPackageAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _dialogService.ShowError($"保存模板文件失败：{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 判定保存命令是否可执行：已加载模板文件且非繁忙。
    /// </summary>
    private bool CanSave() => HasDocument && !IsBusy;

    /// <summary>
    /// 重新加载模板包列表，单个包加载异常由服务内部跳过。
    /// </summary>
    /// <param name="defaultSelectFirstPackage">无历史选中（或历史选中已失效）时是否默认选中第一包；
    /// 调用方随后要按名精确选中新包时传 false，避免默认选中与后续选中竞态。</param>
    private async Task ReloadPackagesAsync(bool defaultSelectFirstPackage = true)
    {
        try
        {
            IReadOnlyList<TemplatePackageInfo> packages = await _packageService.ListPackagesAsync(CancellationToken.None);

            // 记录本次加载后生效的包顺序记忆指纹，供跨窗口配置变化比对，避免无关保存触发重载
            _lastPackageOrderFingerprint = _configService.Current.TemplatePackageOrder.ToArray();

            // 重建期间抑制选中项变更事件，避免清空集合被误判为用户切换触发脏文档确认；随后按原包名恢复选中
            string? previousPackageName = SelectedPackage?.Name;
            _isApplyingRollback = true;
            try
            {
                Packages.Clear();
                foreach (TemplatePackageInfo package in packages)
                {
                    Packages.Add(new TemplatePackageListItemViewModel(package));
                }

                TemplatePackageListItemViewModel? match = string.IsNullOrWhiteSpace(previousPackageName)
                    ? null
                    : Packages.FirstOrDefault(item => string.Equals(item.Name, previousPackageName, StringComparison.OrdinalIgnoreCase));
                SelectedPackage = match;
                _lastSelectedPackage = match;
            }
            finally
            {
                _isApplyingRollback = false;
            }

            // 无历史选中或历史选中已失效但列表非空时，优先按配置记忆的 LastSelectedPackage 定位包，
            // 记忆命中的包被选中并走真实选中事件加载，未命中回退第一包
            if (defaultSelectFirstPackage && SelectedPackage is null && Packages.Count > 0)
            {
                TemplatePackageListItemViewModel? remembered = ResolveRememberedPackage();
                _lastSelectedPackage = remembered ?? Packages[0];
                SelectedPackage = _lastSelectedPackage;
            }
        }
        catch (Exception exception) when (exception is TemplatePackageException or IOException or UnauthorizedAccessException)
        {
            _dialogService.ShowError($"加载模板包列表失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 按配置记忆的最近选中包名定位包列表项，记忆为空或包已不存在时返回 null。
    /// </summary>
    /// <returns>记忆命中的包列表项；未命中返回 null。</returns>
    private TemplatePackageListItemViewModel? ResolveRememberedPackage()
    {
        string rememberedName = _configService.Current.LastSelectedPackage;
        if (string.IsNullOrWhiteSpace(rememberedName))
        {
            return null;
        }

        return Packages.FirstOrDefault(item =>
            string.Equals(item.Name, rememberedName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 包下拉选中项变化：记录切换前选中项，进入异步包切换流程。
    /// </summary>
    /// <param name="value">变更后的包选中项，取消选中时为 null。</param>
    partial void OnSelectedPackageChanged(TemplatePackageListItemViewModel? value)
    {
        if (_isApplyingRollback)
        {
            return;
        }

        _packageBeforeSwitch = _lastSelectedPackage;
        _lastSelectedPackage = value;
        _ = SwitchPackageAsync(value);
    }

    /// <summary>
    /// 切换模板包：脏文档先确认，随后加载包并重建文件树、关闭当前文档。
    /// </summary>
    /// <param name="packageItem">目标包选中项。</param>
    private async Task SwitchPackageAsync(TemplatePackageListItemViewModel? packageItem)
    {
        if (packageItem is null)
        {
            // 取消选中包等同关闭当前模板文档：脏文档须经二次确认，取消则回退包选中项
            if (IsDirty)
            {
                bool canClose = await ConfirmSaveBeforeSwitchAsync("关闭模板");
                if (!canClose)
                {
                    RollbackSelectedPackage();
                    return;
                }
            }

            // 复位当前包为 null，使生成栏与预览区不再引用已取消选中的包，防陈旧包参与后续生成
            _currentPackage = null;
            CloseCurrentDocument();
            Files.Clear();
            return;
        }

        // 脏文档切换前经二次确认，取消则回退包选中项
        if (IsDirty)
        {
            bool canSwitch = await ConfirmSaveBeforeSwitchAsync("切换模板包");
            if (!canSwitch)
            {
                RollbackSelectedPackage();
                return;
            }
        }

        IsBusy = true;
        try
        {
            TemplatePackageInfo package = await _packageService.LoadPackageAsync(packageItem.Name, CancellationToken.None);
            ApplyRememberedFileStates(package);
            ApplyRememberedFileOrder(package);
            _currentPackage = package;

            Files.Clear();
            foreach (TemplateFileInfo file in package.Files)
            {
                Files.Add(file);
            }

            CloseCurrentDocument();
            StatusText = $"已加载模板包：{package.Name}（{package.Files.Count} 个模板文件）";

            // 包切换成功即写入最近选中包并落盘，失败仅记日志不打断加载
            PersistLastSelectedPackage(package.Name);

            // 文件树非空时优先按配置记忆的 LastSelectedTemplateFile 恢复选中文件，未命中默认第一个文件
            if (Files.Count > 0)
            {
                _lastSelectedFile = ResolveRememberedFile() ?? Files[0];
                SelectedFile = _lastSelectedFile;
            }
        }
        catch (Exception exception) when (exception is TemplatePackageException or IOException or UnauthorizedAccessException)
        {
            _dialogService.ShowError($"加载模板包失败：{exception.Message}");
            RollbackSelectedPackage();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 按配置记忆的最近选中模板文件路径定位文件树项，记忆为空或文件已不存在时返回 null。
    /// </summary>
    /// <returns>记忆命中的文件项；未命中返回 null。</returns>
    private TemplateFileInfo? ResolveRememberedFile()
    {
        string rememberedPath = _configService.Current.LastSelectedTemplateFile;
        if (string.IsNullOrWhiteSpace(rememberedPath))
        {
            return null;
        }

        return Files.FirstOrDefault(file =>
            string.Equals(file.RelativeTemplatePath, rememberedPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 将最近选中的模板包名写入配置并保存，启动时据此恢复②区包下拉；写盘失败仅记日志不打断加载。
    /// </summary>
    /// <param name="packageName">目标包名。</param>
    private void PersistLastSelectedPackage(string packageName)
    {
        AppConfig config = _configService.Current;
        if (string.Equals(config.LastSelectedPackage, packageName, StringComparison.Ordinal))
        {
            return;
        }

        config.LastSelectedPackage = packageName;
        try
        {
            _configService.Save();
        }
        catch (ConfigSaveException exception)
        {
            _logger.LogWarning(exception, "模板包选择记忆保存失败，包 {PackageName}。", packageName);
        }
    }

    /// <summary>
    /// 将最近选中的模板文件相对路径写入配置并保存，包加载后据此恢复文件树选中；写盘失败仅记日志不打断加载。
    /// </summary>
    /// <param name="relativePath">目标文件相对包根路径。</param>
    private void PersistLastSelectedTemplateFile(string relativePath)
    {
        AppConfig config = _configService.Current;
        if (string.Equals(config.LastSelectedTemplateFile, relativePath, StringComparison.Ordinal))
        {
            return;
        }

        config.LastSelectedTemplateFile = relativePath;
        try
        {
            _configService.Save();
        }
        catch (ConfigSaveException exception)
        {
            _logger.LogWarning(exception, "模板文件选择记忆保存失败，路径 {RelativePath}。", relativePath);
        }
    }

    /// <summary>
    /// 文件树选中项变化：记录切换前选中项，进入异步文件切换流程。
    /// </summary>
    /// <param name="value">变更后的文件选中项，取消选中时为 null。</param>
    partial void OnSelectedFileChanged(TemplateFileInfo? value)
    {
        if (_isApplyingRollback)
        {
            return;
        }

        _fileBeforeSwitch = _lastSelectedFile;
        _lastSelectedFile = value;
        _ = SwitchFileAsync(value);
    }

    /// <summary>
    /// 切换模板文件：脏文档先确认，随后读取磁盘原文载入编辑器并推导高亮语言。
    /// </summary>
    /// <param name="file">目标文件，取消选中时为 null。</param>
    private async Task SwitchFileAsync(TemplateFileInfo? file)
    {
        if (file is null)
        {
            if (IsDirty)
            {
                bool canClose = await ConfirmSaveBeforeSwitchAsync("关闭模板文件");
                if (!canClose)
                {
                    RollbackSelectedFile();
                    return;
                }
            }

            CloseCurrentDocument();
            StatusText = "未选择模板文件。";
            return;
        }

        if (_currentPackage is null)
        {
            _dialogService.ShowInfo("请先选择模板包。");
            RollbackSelectedFile();
            return;
        }

        // 脏文档切换前经二次确认，取消则回退文件选中项
        if (IsDirty)
        {
            bool canSwitch = await ConfirmSaveBeforeSwitchAsync("切换模板文件");
            if (!canSwitch)
            {
                RollbackSelectedFile();
                return;
            }
        }

        IsBusy = true;
        try
        {
            string text = await _templateFileWriter.ReadAsync(_currentPackage, file.RelativeTemplatePath, CancellationToken.None);
            _currentFile = file;
            _originalText = text;
            HighlightLanguage resolvedLanguage = HighlightLanguageResolver.FromTemplateFileName(file.RelativeTemplatePath);

            // 载入文本期间抑制置脏，随后以磁盘原文为基准复位脏标记
            _isLoadingDocument = true;
            EditorText = text;
            _isLoadingDocument = false;

            IsDirty = false;
            HasDocument = true;
            Language = resolvedLanguage;
            StatusText = $"已加载：{file.RelativeTemplatePath}";

            LoadDocumentRequested?.Invoke(text);
            LanguageChanged?.Invoke(Language);
            EditorContentChanged?.Invoke(text);
            _logger.LogInformation(
                "模板文件加载成功，包 {PackageName}，相对路径 {RelativePath}。", _currentPackage.Name, file.RelativeTemplatePath);

            // 文件切换成功即写入最近选中文件并落盘，失败仅记日志不打断加载
            PersistLastSelectedTemplateFile(file.RelativeTemplatePath);
        }
        catch (Exception exception) when (exception is TemplatePackageException or IOException or UnauthorizedAccessException)
        {
            _dialogService.ShowError($"加载模板文件失败：{exception.Message}");
            RollbackSelectedFile();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 将当前编辑文本写盘到当前用户包文件，并应用保存结果。
    /// </summary>
    private async Task SaveToCurrentPackageAsync()
    {
        TemplateSaveResult result = await _templateFileWriter.WriteAsync(
            _currentPackage!, _currentFile!.RelativeTemplatePath, EditorText, CancellationToken.None);
        ApplySaveResult(result);
    }

    /// <summary>
    /// 应用保存结果：成功复位脏标记与基准文本，只读拒绝提示引导，其余失败提示可读错误。
    /// </summary>
    /// <param name="result">模板文件写盘结果。</param>
    private void ApplySaveResult(TemplateSaveResult result)
    {
        if (result.IsSuccess)
        {
            _originalText = EditorText;
            IsDirty = false;
            StatusText = $"已保存：{_currentFile?.RelativeTemplatePath}";
            _logger.LogInformation(
                "模板文件保存成功，包 {PackageName}，相对路径 {RelativePath}。", _currentPackage?.Name, _currentFile?.RelativeTemplatePath);
        }
        else if (result.IsReadOnlyBuiltin)
        {
            StatusText = result.Message ?? "内置包只读，未保存修改。";
        }
        else
        {
            _dialogService.ShowError(result.Message ?? "保存模板文件失败。");
        }
    }

    /// <summary>
    /// 内置包保存引导：确认复制为可编辑用户包，复制成功后切换当前包并立即保存当前文件。
    /// </summary>
    /// <returns>是否成功保存到用户包副本。</returns>
    private async Task<bool> HandleBuiltinSaveAsync()
    {
        if (_currentPackage is null || _currentFile is null)
        {
            return false;
        }

        string copyName = await BuildUniqueCopyNameAsync(_currentPackage.Name);
        bool confirmed = await _confirmDialogService.ConfirmAsync(
            "内置包只读",
            $"内置包 {_currentPackage.Name} 只读，无法直接保存。\n是否复制为“{copyName}”用户包后再保存？");
        if (!confirmed)
        {
            StatusText = "内置包只读，未保存修改。";
            return false;
        }

        TemplatePackageOperationResult copyResult = await _packageService.CopyPackageAsync(
            _currentPackage.Name, copyName, overwrite: false, CancellationToken.None);
        if (copyResult.Status != TemplatePackageOperationStatus.Succeeded || copyResult.Package is null)
        {
            _dialogService.ShowError(copyResult.Message ?? "复制模板包失败，无法保存。");
            return false;
        }

        // 复制成功：刷新包列表纳入副本包并切换到副本包，更新当前文件实例，随后以当前文本写盘
        _currentPackage = copyResult.Package;

        // 重建文件树期间抑制选中项变更，避免旧选中文件被移出集合时触发空选中确认，打断复制保存流程
        _isApplyingRollback = true;
        try
        {
            ReloadFiles();
        }
        finally
        {
            _isApplyingRollback = false;
        }

        SyncCurrentFileInNewPackage();
        await ReloadPackagesAsync();
        SelectPackageSilently(copyResult.Package.Name);

        TemplateSaveResult saveResult = await _templateFileWriter.WriteAsync(
            _currentPackage, _currentFile!.RelativeTemplatePath, EditorText, CancellationToken.None);
        if (saveResult.IsSuccess)
        {
            _originalText = EditorText;
            IsDirty = false;
            _logger.LogInformation(
                "内置包复制并保存成功，新包 {PackageName}，相对路径 {RelativePath}。",
                _currentPackage.Name, _currentFile.RelativeTemplatePath);
            return true;
        }

        ApplySaveResult(saveResult);
        return saveResult.IsSuccess;
    }

    /// <summary>
    /// 生成内置包复制到用户库的唯一新包名，同名时追加序号直到未占用。
    /// </summary>
    /// <param name="sourceName">源内置包名。</param>
    /// <returns>不与现有包冲突的新包名。</returns>
    private async Task<string> BuildUniqueCopyNameAsync(string sourceName)
    {
        IReadOnlyList<TemplatePackageInfo> packages = await _packageService.ListPackagesAsync(CancellationToken.None);
        HashSet<string> existingNames = new(packages.Select(package => package.Name), StringComparer.OrdinalIgnoreCase);

        string baseName = $"{sourceName}-copy";
        if (!existingNames.Contains(baseName))
        {
            return baseName;
        }

        // 基础名已占用时追加序号，逐个探测直到找到可用名称
        int suffix = 2;
        while (true)
        {
            string candidate = $"{baseName}{suffix}";
            if (!existingNames.Contains(candidate))
            {
                return candidate;
            }

            suffix++;
        }
    }

    /// <summary>
    /// 按当前包重建文件树集合，重建前先应用按包记忆的勾选态与文件展示顺序。
    /// </summary>
    private void ReloadFiles()
    {
        Files.Clear();
        if (_currentPackage is null)
        {
            return;
        }

        ApplyRememberedFileStates(_currentPackage);
        ApplyRememberedFileOrder(_currentPackage);
        foreach (TemplateFileInfo file in _currentPackage.Files)
        {
            Files.Add(file);
        }
    }

    /// <summary>
    /// 将配置中按包名记忆的模板文件勾选态覆盖到包文件清单上，还原上次勾选结果。
    /// 包名无记忆或包文件不在记忆中时保持 manifest 默认勾选态不变。
    /// </summary>
    /// <param name="package">加载后的模板包运行时信息。</param>
    private void ApplyRememberedFileStates(TemplatePackageInfo package)
    {
        if (!_configService.Current.TemplateFileStates.TryGetValue(package.Name, out List<TemplateFileState>? remembered))
        {
            return;
        }

        if (remembered is null || remembered.Count == 0)
        {
            return;
        }

        // 按规范化相对路径建立记忆勾选态查找表，包文件命中时覆盖其勾选态
        var stateByPath = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (TemplateFileState state in remembered)
        {
            if (state is null || string.IsNullOrWhiteSpace(state.TemplatePath))
            {
                continue;
            }

            stateByPath[TemplatePackageLoader.NormalizeRelativePath(state.TemplatePath)] = state.Enabled;
        }

        foreach (TemplateFileInfo file in package.Files)
        {
            if (stateByPath.TryGetValue(file.RelativeTemplatePath, out bool enabled))
            {
                file.IsEnabled = enabled;
            }
        }
    }

    /// <summary>
    /// 将配置中按包名记忆的模板文件展示顺序覆盖到包文件清单上，记忆内仍存在的文件按记忆顺序前置，
    /// 不在记忆内的新文件按 manifest 声明顺序追加末尾；包名无记忆或记忆为空时保持 manifest 声明顺序。
    /// 记忆中的失效文件（已删除）被过滤，不进入重排结果；排序记忆只在内存应用，不修改包资产。
    /// </summary>
    /// <param name="package">加载后的模板包运行时信息。</param>
    private void ApplyRememberedFileOrder(TemplatePackageInfo package)
    {
        if (!_configService.Current.TemplateFileOrder.TryGetValue(package.Name, out List<string>? remembered))
        {
            return;
        }

        if (remembered is null || remembered.Count == 0)
        {
            return;
        }

        // 按规范化相对路径建立文件查找表，记忆匹配与去重统一按大小写不敏感比较，与包内路径匹配先例一致
        var filesByPath = new Dictionary<string, TemplateFileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (TemplateFileInfo file in package.Files)
        {
            filesByPath[file.RelativeTemplatePath] = file;
        }

        // 按记忆顺序收集仍存在的文件：重复记忆只消费一次，失效文件（已删除）被过滤
        var reordered = new List<TemplateFileInfo>(package.Files.Count);
        var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rememberedPath in remembered)
        {
            string normalizedPath = TemplatePackageLoader.NormalizeRelativePath(rememberedPath);
            if (string.IsNullOrWhiteSpace(normalizedPath) || !processedPaths.Add(normalizedPath))
            {
                continue;
            }

            if (filesByPath.TryGetValue(normalizedPath, out TemplateFileInfo? file))
            {
                reordered.Add(file);
            }
        }

        // 不在记忆内的新文件按 manifest 声明顺序追加末尾，保证新增文件始终可见且顺序稳定
        foreach (TemplateFileInfo file in package.Files)
        {
            if (!processedPaths.Contains(file.RelativeTemplatePath))
            {
                reordered.Add(file);
            }
        }

        package.Files = reordered;
    }

    /// <summary>
    /// 将当前包文件树的勾选态按包名写入配置并保存，勾选即存一次点击一次写盘。
    /// 写盘失败仅记录警告日志，不打断模板区操作。
    /// </summary>
    private void PersistFileSelectionStates()
    {
        if (_currentPackage is null)
        {
            return;
        }

        // 逐文件收集勾选态，跳过空文件实例防止空引用
        var states = new List<TemplateFileState>();
        foreach (TemplateFileInfo file in Files)
        {
            if (file is null)
            {
                continue;
            }

            states.Add(new TemplateFileState
            {
                TemplatePath = file.RelativeTemplatePath,
                Enabled = file.IsEnabled
            });
        }

        _configService.Current.TemplateFileStates[_currentPackage.Name] = states;
        try
        {
            _configService.Save();
        }
        catch (ConfigSaveException exception)
        {
            _logger.LogWarning(exception, "模板勾选态保存失败，包 {PackageName}。", _currentPackage.Name);
        }
    }

    /// <summary>
    /// 将文件树选中文件上移一位：重排包文件清单与展示集合，选中项跟随，并即时持久化顺序记忆。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMoveFileUp))]
    private void MoveFileUp()
    {
        if (_currentPackage is null || SelectedFile is null)
        {
            return;
        }

        int sourceIndex = Files.IndexOf(SelectedFile);
        if (sourceIndex < 0)
        {
            return;
        }

        MoveFileCore(sourceIndex, sourceIndex - 1);
    }

    /// <summary>
    /// 将文件树选中文件下移一位：重排包文件清单与展示集合，选中项跟随，并即时持久化顺序记忆。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMoveFileDown))]
    private void MoveFileDown()
    {
        if (_currentPackage is null || SelectedFile is null)
        {
            return;
        }

        int sourceIndex = Files.IndexOf(SelectedFile);
        if (sourceIndex < 0)
        {
            return;
        }

        MoveFileCore(sourceIndex, sourceIndex + 1);
    }

    /// <summary>
    /// 按拖拽落位索引移动文件树项：经 T03 拖拽辅助计算源/目标索引后调用，复用与上移/下移一致的移动逻辑。
    /// </summary>
    /// <param name="sourceIndex">被拖拽文件当前索引。</param>
    /// <param name="targetIndex">拖拽落点目标最终索引。</param>
    public void MoveFileTo(int sourceIndex, int targetIndex)
    {
        MoveFileCore(sourceIndex, targetIndex);
    }

    /// <summary>
    /// 恢复当前包文件默认顺序：清除该包文件顺序记忆并落盘，重新加载包按 manifest 声明顺序还原，
    /// 勾选态记忆仍生效；脏文档先经二次确认，避免重建文件树打断未保存编辑。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanResetFileOrder))]
    private async Task ResetFileOrderAsync()
    {
        if (_currentPackage is null)
        {
            return;
        }

        string packageName = _currentPackage.Name;
        string? selectedPath = SelectedFile?.RelativeTemplatePath;

        // 恢复默认会重建文件树并切换当前文件，脏文档先经二次确认，取消则终止本次恢复
        if (IsDirty)
        {
            bool canLeave = await ConfirmSaveBeforeSwitchAsync("恢复默认排序");
            if (!canLeave)
            {
                return;
            }
        }

        IsBusy = true;
        try
        {
            // 重新加载包取回 manifest 声明顺序，勾选态记忆随后在重建文件树时应用
            TemplatePackageInfo package = await _packageService.LoadPackageAsync(packageName, CancellationToken.None);
            _currentPackage = package;

            // 清除当前包文件顺序记忆并落盘，使后续加载回到 manifest 声明顺序
            _configService.Current.TemplateFileOrder.Remove(packageName);
            try
            {
                _configService.Save();
            }
            catch (ConfigSaveException exception)
            {
                _logger.LogWarning(exception, "恢复默认文件顺序保存失败，包 {PackageName}。", packageName);
            }

            // 重建文件树：应用勾选态记忆并按 manifest 声明顺序填充；期间抑制选中项变更，防空选中触发脏文档确认
            _isApplyingRollback = true;
            try
            {
                ReloadFiles();
            }
            finally
            {
                _isApplyingRollback = false;
            }

            // 恢复选中到原文件（可能已随顺序变化换位），找不到时默认选中首个文件
            TemplateFileInfo? target = string.IsNullOrWhiteSpace(selectedPath)
                ? null
                : Files.FirstOrDefault(file =>
                    string.Equals(file.RelativeTemplatePath, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (target is null && Files.Count > 0)
            {
                target = Files[0];
            }

            if (target is not null)
            {
                _lastSelectedFile = target;
                SelectedFile = target;
            }

            StatusText = $"已恢复模板文件默认顺序：{package.Name}";
            ResetFileOrderCommand.NotifyCanExecuteChanged();
        }
        catch (Exception exception) when (exception is TemplatePackageException or IOException or UnauthorizedAccessException)
        {
            _dialogService.ShowError($"恢复模板文件默认顺序失败：{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 判定文件上移命令是否可执行：选中文件非空、非繁忙且未达文件树首位。
    /// </summary>
    private bool CanMoveFileUp()
    {
        if (_currentPackage is null || SelectedFile is null || IsBusy)
        {
            return false;
        }

        return CollectionReorderHelper.CanMoveUp(Files.IndexOf(SelectedFile));
    }

    /// <summary>
    /// 判定文件下移命令是否可执行：选中文件非空、非繁忙且未达文件树末位。
    /// </summary>
    private bool CanMoveFileDown()
    {
        if (_currentPackage is null || SelectedFile is null || IsBusy)
        {
            return false;
        }

        return CollectionReorderHelper.CanMoveDown(Files.IndexOf(SelectedFile), Files.Count);
    }

    /// <summary>
    /// 判定恢复默认文件顺序命令是否可执行：当前包存在文件顺序记忆且非繁忙。
    /// </summary>
    private bool CanResetFileOrder()
    {
        if (_currentPackage is null || IsBusy)
        {
            return false;
        }

        return _configService.Current.TemplateFileOrder.TryGetValue(_currentPackage.Name, out List<string>? remembered)
            && remembered is not null && remembered.Count > 0;
    }

    /// <summary>
    /// 将文件树指定项从源索引移动到目标索引：统一经重排辅助移动展示集合，同步包文件清单顺序，
    /// 选中项跟随移动，并把新顺序写入按包记忆的文件顺序配置并落盘。
    /// </summary>
    /// <param name="sourceIndex">被移动文件当前索引。</param>
    /// <param name="targetIndex">移动完成后目标最终索引。</param>
    private void MoveFileCore(int sourceIndex, int targetIndex)
    {
        if (_currentPackage is null)
        {
            return;
        }

        // 源索引越界时直接返回，防止经拖拽落位回调传入异常索引时抛到视图层事件冒泡
        if (sourceIndex < 0 || sourceIndex >= Files.Count)
        {
            return;
        }

        // 统一经重排辅助移动，目标索引超界收敛、单项不移动，返回移动后新索引；原位未动不触发后续流程
        int newIndex = CollectionReorderHelper.MoveTo(Files, sourceIndex, targetIndex);
        if (newIndex == sourceIndex)
        {
            return;
        }

        // 同步包文件清单与展示集合一致，保证生成栏 BuildSelectedFiles 迭代顺序与文件树展示一致
        _currentPackage.Files = Files.ToList();

        // 选中项跟随移动：Move 保持项实例不变，显式复位选中项到新位置，绑定 SelectedItem 随之更新
        SelectedFile = Files[newIndex];

        // 即时持久化顺序记忆并落盘，写盘失败仅记日志不打断排序交互
        PersistFileOrder();

        // 移动后首末边界与顺序记忆状态可能变化，主动刷新上移/下移与恢复默认命令的可用性
        MoveFileUpCommand.NotifyCanExecuteChanged();
        MoveFileDownCommand.NotifyCanExecuteChanged();
        ResetFileOrderCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 将当前文件树的相对路径顺序按包名写入配置并保存，一次排序一次写盘。
    /// 写盘失败仅记录警告日志，不打断模板区排序操作。
    /// </summary>
    private void PersistFileOrder()
    {
        if (_currentPackage is null)
        {
            return;
        }

        // 按重排后的展示集合逐文件收集相对路径，作为该包的文件展示顺序记忆
        var order = new List<string>(Files.Count);
        foreach (TemplateFileInfo file in Files)
        {
            if (file is null)
            {
                continue;
            }

            order.Add(file.RelativeTemplatePath);
        }

        _configService.Current.TemplateFileOrder[_currentPackage.Name] = order;
        try
        {
            _configService.Save();
        }
        catch (ConfigSaveException exception)
        {
            _logger.LogWarning(exception, "模板文件顺序保存失败，包 {PackageName}。", _currentPackage.Name);
        }
    }

    /// <summary>
    /// 将当前文件切换到新包中同名文件实例，保持编辑基准与写盘对象一致。
    /// </summary>
    private void SyncCurrentFileInNewPackage()
    {
        if (_currentFile is null)
        {
            return;
        }

        TemplateFileInfo? match = Files.FirstOrDefault(file =>
            string.Equals(file.RelativeTemplatePath, _currentFile.RelativeTemplatePath, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            _currentFile = match;
            SelectFileSilently(match);
        }
    }

    /// <summary>
    /// 静默选中文件树项，不触发文件加载流程。
    /// </summary>
    /// <param name="file">目标文件。</param>
    private void SelectFileSilently(TemplateFileInfo file)
    {
        _isApplyingRollback = true;
        try
        {
            SelectedFile = file;
        }
        finally
        {
            _isApplyingRollback = false;
        }
    }

    /// <summary>
    /// 静默选中包下拉项，不触发包切换加载流程，供内置包复制成功后同步到副本包。
    /// </summary>
    /// <param name="packageName">目标包名。</param>
    private void SelectPackageSilently(string packageName)
    {
        TemplatePackageListItemViewModel? match = Packages.FirstOrDefault(item =>
            string.Equals(item.Name, packageName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return;
        }

        _isApplyingRollback = true;
        try
        {
            SelectedPackage = match;
            _lastSelectedPackage = match;
        }
        finally
        {
            _isApplyingRollback = false;
        }
    }

    /// <summary>
    /// 回退包选中项到切换前值，期间抑制再次触发切换加载。
    /// </summary>
    private void RollbackSelectedPackage()
    {
        _isApplyingRollback = true;
        try
        {
            SelectedPackage = _packageBeforeSwitch;
            _lastSelectedPackage = _packageBeforeSwitch;
        }
        finally
        {
            _isApplyingRollback = false;
        }
    }

    /// <summary>
    /// 回退文件选中项到切换前值，期间抑制再次触发切换加载。
    /// </summary>
    private void RollbackSelectedFile()
    {
        _isApplyingRollback = true;
        try
        {
            SelectedFile = _fileBeforeSwitch;
            _lastSelectedFile = _fileBeforeSwitch;
        }
        finally
        {
            _isApplyingRollback = false;
        }
    }

    /// <summary>
    /// 关闭当前文档：清空文件、基准文本、编辑器文本与高亮语言，并通知视图与预览区清空。
    /// </summary>
    private void CloseCurrentDocument()
    {
        _currentFile = null;
        _originalText = string.Empty;
        IsDirty = false;
        HasDocument = false;
        EditorText = string.Empty;
        Language = HighlightLanguage.Plain;
        StatusText = "未选择模板文件。";
        ClearDocumentRequested?.Invoke();
        EditorContentChanged?.Invoke(string.Empty);
    }

    /// <summary>
    /// 脏文档切换/关闭前二次确认：先问是否保存，保存失败或用户选择保存但未保存成功则不可离开；
    /// 不保存则再确认放弃，放弃成功才可离开，取消则留在当前编辑。
    /// </summary>
    /// <param name="actionName">切换或关闭动作描述，用于确认文案。</param>
    /// <returns>允许离开返回 true，留在当前编辑返回 false。</returns>
    private async Task<bool> ConfirmSaveBeforeSwitchAsync(string actionName)
    {
        if (!IsDirty)
        {
            return true;
        }

        bool saveFirst = await _confirmDialogService.ConfirmAsync(
            "未保存的修改", $"当前模板有未保存的修改，是否保存后再{actionName}？");
        if (saveFirst)
        {
            // 内置包保存经复制引导，成功视为已保存；用户包直接写盘后按脏标记判断
            if (_currentPackage is null || _currentFile is null)
            {
                return false;
            }

            if (_currentPackage.IsBuiltin)
            {
                return await HandleBuiltinSaveAsync();
            }

            await SaveToCurrentPackageAsync();
            return !IsDirty;
        }

        bool discard = await _confirmDialogService.ConfirmAsync(
            "放弃修改", "确定放弃未保存的修改吗？放弃后修改将丢失。");
        if (discard)
        {
            // 放弃编辑即丢弃未保存修改：基准文本对齐当前内容并复位脏标记，后续切换/关闭直接通行
            _originalText = EditorText;
            IsDirty = false;
        }

        return discard;
    }
}
