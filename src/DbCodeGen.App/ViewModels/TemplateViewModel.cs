using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbCodeGen.App.Services;
using DbCodeGen.App.Views;
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
    private readonly Func<VariablePanelWindow> _variablePanelWindowFactory;
    private readonly ILogger<TemplateViewModel> _logger;

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
    /// 模板包列表，绑定②区包下拉，内置包优先、包名排序由服务契约保证。
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<TemplatePackageListItemViewModel> _packages = new();

    /// <summary>
    /// 模板包下拉选中项，切换时加载对应包的文件树。
    /// </summary>
    [ObservableProperty]
    private TemplatePackageListItemViewModel? _selectedPackage;

    /// <summary>
    /// 包内模板文件树，绑定②区文件列表，选中文件后加载编辑器。
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<TemplateFileInfo> _files = new();

    /// <summary>
    /// 文件树选中项，切换时经脏文档确认后加载文件内容。
    /// </summary>
    [ObservableProperty]
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
    /// 使用模板包服务、模板文件读写服务、对话框服务、变量面板窗口工厂与日志器构造视图模型。
    /// </summary>
    /// <param name="packageService">模板包管理服务，承载包列表与复制能力。</param>
    /// <param name="templateFileWriter">模板文件读写服务，承载读取与保存写回。</param>
    /// <param name="dialogService">消息提示服务，用于加载与保存失败反馈。</param>
    /// <param name="confirmDialogService">二次确认服务，用于脏文档与内置包复制引导确认。</param>
    /// <param name="variablePanelWindowFactory">变量面板窗口工厂，供变量面板入口按需创建。</param>
    /// <param name="logger">视图模型日志器，日志不记录模板正文与敏感信息。</param>
    /// <exception cref="ArgumentNullException">任一依赖参数为 null 时抛出。</exception>
    public TemplateViewModel(
        ITemplatePackageService packageService,
        TemplateFileWriter templateFileWriter,
        IDialogService dialogService,
        IConfirmDialogService confirmDialogService,
        Func<VariablePanelWindow> variablePanelWindowFactory,
        ILogger<TemplateViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(packageService);
        ArgumentNullException.ThrowIfNull(templateFileWriter);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(confirmDialogService);
        ArgumentNullException.ThrowIfNull(variablePanelWindowFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _packageService = packageService;
        _templateFileWriter = templateFileWriter;
        _dialogService = dialogService;
        _confirmDialogService = confirmDialogService;
        _variablePanelWindowFactory = variablePanelWindowFactory;
        _logger = logger;
    }

    /// <summary>
    /// 异步初始化：加载模板包列表。主窗口呈现后调用。
    /// </summary>
    public async Task InitializeAsync()
    {
        await ReloadPackagesAsync();
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
    /// 以 Files 属性变更通知广播给生成栏等消费方，使其重新评估“勾选到层”命令可用性。
    /// </summary>
    public void NotifyFileSelectionChanged()
    {
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
    private async Task ReloadPackagesAsync()
    {
        try
        {
            IReadOnlyList<TemplatePackageInfo> packages = await _packageService.ListPackagesAsync(CancellationToken.None);

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
        }
        catch (Exception exception) when (exception is TemplatePackageException or IOException or UnauthorizedAccessException)
        {
            _dialogService.ShowError($"加载模板包列表失败：{exception.Message}");
        }
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
            _currentPackage = package;

            Files.Clear();
            foreach (TemplateFileInfo file in package.Files)
            {
                Files.Add(file);
            }

            CloseCurrentDocument();
            StatusText = $"已加载模板包：{package.Name}（{package.Files.Count} 个模板文件）";
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
        ReloadFiles();
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
    /// 按当前包重建文件树集合。
    /// </summary>
    private void ReloadFiles()
    {
        Files.Clear();
        if (_currentPackage is null)
        {
            return;
        }

        foreach (TemplateFileInfo file in _currentPackage.Files)
        {
            Files.Add(file);
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
