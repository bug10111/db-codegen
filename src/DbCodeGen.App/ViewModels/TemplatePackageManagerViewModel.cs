using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbCodeGen.App.Services;
using DbCodeGen.Core.Templates.Packages;

namespace DbCodeGen.App.ViewModels;

/// <summary>
/// 模板包列表行视图模型，承载包列表各列的只读展示信息与内置包只读标记。
/// 列表展示所需的名称、说明、来源、文件数、引擎与修改时间均直接取自加载后的包运行时信息。
/// </summary>
public sealed class TemplatePackageListItemViewModel
{
    /// <summary>
    /// 使用已加载的模板包运行时信息构造列表行。
    /// </summary>
    /// <param name="package">已通过校验的模板包运行时信息。</param>
    /// <exception cref="ArgumentNullException">package 为 null 时抛出。</exception>
    public TemplatePackageListItemViewModel(TemplatePackageInfo package)
    {
        Package = package ?? throw new ArgumentNullException(nameof(package));
    }

    /// <summary>
    /// 模板包运行时信息，列表各列直接绑定其字段。
    /// </summary>
    public TemplatePackageInfo Package { get; }

    /// <summary>
    /// 包名。
    /// </summary>
    public string Name => Package.Name;

    /// <summary>
    /// 包说明；清单未填写说明时展示占位文本，避免空列。
    /// </summary>
    public string Description => string.IsNullOrWhiteSpace(Package.Description) ? "（无说明）" : Package.Description;

    /// <summary>
    /// 是否内置包，为 true 时列表以醒目样式展示只读标识。
    /// </summary>
    public bool IsBuiltin => Package.IsBuiltin;

    /// <summary>
    /// 来源展示文本：内置包标注“内置 · 只读”，用户包标注“用户”。
    /// </summary>
    public string SourceText => Package.IsBuiltin ? "内置 · 只读" : "用户";

    /// <summary>
    /// 包内模板文件数量。
    /// </summary>
    public int FileCount => Package.Files.Count;

    /// <summary>
    /// 模板引擎名。
    /// </summary>
    public string Engine => Package.Engine;

    /// <summary>
    /// 包目录最新修改时间。
    /// </summary>
    public DateTime ModifiedTime => Package.ModifiedTime;
}

/// <summary>
/// 模板包管理窗口视图模型，承载模板包列表加载、zip/文件夹导入、复制、导出与删除。
/// 内置包只读边界由服务契约保证：删除与同名覆盖在界面层对内置包禁用，服务层对同名覆盖返回只读拒绝。
/// 同名用户包覆盖前必须二次确认，取消确认则本次操作终止。
/// </summary>
public sealed partial class TemplatePackageManagerViewModel : ObservableObject
{
    private readonly ITemplatePackageService _packageService;
    private readonly IDialogService _dialogService;
    private readonly IConfirmDialogService _confirmDialogService;
    private readonly IFolderPickerService _folderPickerService;
    private readonly IFilePickerService _filePickerService;
    private readonly IPromptDialogService _promptDialogService;

    /// <summary>
    /// 复制模式下被复制的源包名，复制确认时据此定位，避免期间列表选中项变化影响操作目标。
    /// </summary>
    private string? _copySourceName;

    /// <summary>
    /// 模板包列表，按服务契约以内置包优先、包名升序排列。
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<TemplatePackageListItemViewModel> _packages = new();

    /// <summary>
    /// 列表当前选中包，供复制、导出与删除操作定位目标。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BeginCopyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private TemplatePackageListItemViewModel? _selectedPackage;

    /// <summary>
    /// 复制输入区是否可见，进入复制模式后展示新包名输入行。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCopyCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCopyCommand))]
    private bool _isCopyModeActive;

    /// <summary>
    /// 复制模式下输入的新包名，确认复制前须非空。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCopyCommand))]
    private string _newPackageName = string.Empty;

    /// <summary>
    /// 底部状态提示文本，用于内置包只读提醒与复制操作引导。
    /// </summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    /// 是否处于操作繁忙状态，繁忙时整体禁用界面防止并发操作。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(CreatePackageCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportZipCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(BeginCopyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCopyCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCopyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;

    /// <summary>
    /// 界面是否空闲可用，与繁忙状态相反，用于绑定窗口整体启用。
    /// </summary>
    public bool IsIdle => !IsBusy;

    /// <summary>
    /// 复制输入区的引导文本，展示当前被复制的源包名。
    /// </summary>
    public string CopySourceDisplay => string.IsNullOrWhiteSpace(_copySourceName) ? "复制模板包" : $"从“{_copySourceName}”复制";

    /// <summary>
    /// 使用模板包服务与对话框服务构造模板包管理视图模型。
    /// </summary>
    /// <param name="packageService">模板包管理服务，承载列表加载与导入复制导出删除操作。</param>
    /// <param name="dialogService">消息提示服务，用于操作结果与错误反馈。</param>
    /// <param name="confirmDialogService">二次确认服务，用于同名覆盖与删除前的确认。</param>
    /// <param name="folderPickerService">目录选择服务，用于选择待导入的模板包文件夹。</param>
    /// <param name="filePickerService">文件选择服务，用于选择待导入与导出的 zip 文件。</param>
    /// <param name="promptDialogService">文本输入提示服务，用于新增模板包时收集包名/说明/首文件路径。</param>
    /// <exception cref="ArgumentNullException">任一依赖参数为 null 时抛出。</exception>
    public TemplatePackageManagerViewModel(
        ITemplatePackageService packageService,
        IDialogService dialogService,
        IConfirmDialogService confirmDialogService,
        IFolderPickerService folderPickerService,
        IFilePickerService filePickerService,
        IPromptDialogService promptDialogService)
    {
        ArgumentNullException.ThrowIfNull(packageService);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(confirmDialogService);
        ArgumentNullException.ThrowIfNull(folderPickerService);
        ArgumentNullException.ThrowIfNull(filePickerService);
        ArgumentNullException.ThrowIfNull(promptDialogService);

        _packageService = packageService;
        _dialogService = dialogService;
        _confirmDialogService = confirmDialogService;
        _folderPickerService = folderPickerService;
        _filePickerService = filePickerService;
        _promptDialogService = promptDialogService;
    }

    /// <summary>
    /// 异步初始化：加载模板包列表。窗口呈现完成后调用。
    /// </summary>
    public async Task InitializeAsync()
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
    /// 刷新模板包列表。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOperateWhenIdle))]
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
    /// 新增模板包：依次收集包名/说明/首模板文件路径，经服务创建成功后刷新列表并选中新包。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOperateWhenIdle))]
    private async Task CreatePackageAsync()
    {
        // 依次收集新建包输入：包名必填，说明可空，首模板文件默认 main.tpl
        string? packageName = await _promptDialogService.PromptAsync(
            "新增模板包", "请输入新包名（字母/数字/中划线/下划线）：");
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return;
        }

        string name = packageName.Trim();
        string? description = await _promptDialogService.PromptAsync("新增模板包", "请输入包说明（可留空）：", "");
        if (description is null)
        {
            return;
        }

        string? templatePath = await _promptDialogService.PromptAsync(
            "新增模板包", "请输入首模板文件相对路径（可含分组目录）：", "main.tpl");
        if (string.IsNullOrWhiteSpace(templatePath))
        {
            return;
        }

        string firstTemplate = templatePath.Trim();
        IsBusy = true;
        try
        {
            TemplatePackageOperationResult result = await _packageService.CreatePackageAsync(
                name, description.Trim(), firstTemplate, firstTemplate, CancellationToken.None);
            if (result.Status != TemplatePackageOperationStatus.Succeeded || result.Package is null)
            {
                _dialogService.ShowError(result.Message ?? "新增模板包失败。");
                return;
            }

            await ReloadPackagesAsync();
            SelectedPackage = Packages.FirstOrDefault(package =>
                string.Equals(package.Name, result.Package.Name, StringComparison.OrdinalIgnoreCase));
            _dialogService.ShowInfo($"已新增模板包“{result.Package.Name}”，首模板文件“{firstTemplate}”已创建。");
        }
        catch (Exception exception) when (exception is TemplatePackageException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _dialogService.ShowError($"新增模板包失败：{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 导入 zip：选择 zip 文件后经服务校验安装，同名用户包覆盖确认、内置包同名只读拒绝。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOperateWhenIdle))]
    private async Task ImportZipAsync()
    {
        string? zipPath = await _filePickerService.PickOpenZipAsync();
        if (string.IsNullOrWhiteSpace(zipPath))
        {
            return;
        }

        // 冲突处理与安装结果反馈走统一流程，服务内部已做防穿越与解压上限校验
        await RunInstallOperationAsync(
            overwrite => _packageService.ImportFromZipAsync(zipPath, overwrite, CancellationToken.None),
            "导入 zip");
    }

    /// <summary>
    /// 导入文件夹：选择模板包目录后经服务校验安装，同名覆盖规则与 zip 导入一致。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOperateWhenIdle))]
    private async Task ImportFolderAsync()
    {
        string? folderPath = await _folderPickerService.PickFolderAsync(null, "选择模板包文件夹");
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        // 冲突处理与安装结果反馈走统一流程，服务内部已校验目录含 template.json 并完成单包校验
        await RunInstallOperationAsync(
            overwrite => _packageService.ImportFromFolderAsync(folderPath, overwrite, CancellationToken.None),
            "导入文件夹");
    }

    /// <summary>
    /// 进入复制模式：以选中包名为新包名初值，等待用户修改后确认复制。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanBeginCopy))]
    private void BeginCopy()
    {
        if (SelectedPackage is null)
        {
            return;
        }

        // 记录被复制源包名并预填新包名，用户可修改后确认；内置包复制后转为可编辑用户包
        _copySourceName = SelectedPackage.Name;
        NewPackageName = SelectedPackage.Name;
        IsCopyModeActive = true;
        OnPropertyChanged(nameof(CopySourceDisplay));
        StatusText = SelectedPackage.IsBuiltin
            ? "内置包复制到用户库后转为可读写。"
            : "请输入新包名后确认复制。";
    }

    /// <summary>
    /// 确认复制：按输入的新包名调用复制服务，内置包同名拒绝、用户包同名覆盖确认。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConfirmCopy))]
    private async Task ConfirmCopyAsync()
    {
        // 提前捕获源包名，复制成功退出复制模式后会清空该字段，避免提示消息引用失效
        string? sourceName = _copySourceName;
        if (sourceName is null || string.IsNullOrWhiteSpace(NewPackageName))
        {
            return;
        }

        string newName = NewPackageName.Trim();
        IsBusy = true;
        try
        {
            TemplatePackageOperationResult result = await _packageService.CopyPackageAsync(sourceName, newName, overwrite: false, CancellationToken.None);

            // 新包名与用户包同名时经用户确认后以覆盖标志重试；取消确认则本次复制终止
            if (result.Status == TemplatePackageOperationStatus.NameConflict)
            {
                bool confirmed = await _confirmDialogService.ConfirmAsync(
                    "同名用户包已存在", $"{result.Message}\n是否确认覆盖同名用户包？");
                if (confirmed)
                {
                    result = await _packageService.CopyPackageAsync(sourceName, newName, overwrite: true, CancellationToken.None);
                }
            }

            if (result.Status == TemplatePackageOperationStatus.Succeeded)
            {
                await ReloadPackagesAsync();
                ExitCopyMode();
                _dialogService.ShowInfo($"已复制模板包“{sourceName}”为“{result.Package?.Name}”。");
            }
            else if (result.Status == TemplatePackageOperationStatus.BuiltinConflict)
            {
                // 新包名与内置包同名：内置包只读，直接拒绝且不进入覆盖确认
                _dialogService.ShowError(result.Message);
            }
            else if (result.Status == TemplatePackageOperationStatus.NameConflict)
            {
                // 用户取消覆盖确认，复制不执行，保留复制输入供重试
            }
            else
            {
                _dialogService.ShowError($"复制模板包失败：{result.Message}");
            }
        }
        catch (Exception exception) when (exception is TemplatePackageException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _dialogService.ShowError($"复制模板包失败：{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 取消复制：退出复制模式并清空新包名输入，不执行任何操作。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancelCopy))]
    private void CancelCopy()
    {
        ExitCopyMode();
    }

    /// <summary>
    /// 导出 zip：选择目标路径后打包清单与包内全部模板文件，内置包与用户包均可导出。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanManageSelected))]
    private async Task ExportAsync()
    {
        if (SelectedPackage is null)
        {
            return;
        }

        string? targetPath = await _filePickerService.PickSaveZipAsync($"{SelectedPackage.Name}.zip");
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return;
        }

        IsBusy = true;
        try
        {
            string exportedPath = await _packageService.ExportToZipAsync(SelectedPackage.Name, targetPath, CancellationToken.None);
            _dialogService.ShowInfo($"已导出模板包“{SelectedPackage.Name}”到：{exportedPath}");
        }
        catch (Exception exception) when (exception is TemplatePackageException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _dialogService.ShowError($"导出模板包失败：{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 删除模板包：删除前二次确认，内置包只读在界面层禁用且服务层兜底拒绝。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteAsync()
    {
        if (SelectedPackage is null)
        {
            return;
        }

        // 提前捕获目标包名，删除后刷新列表会清空选中项，避免后续提示消息解引用失效
        string packageName = SelectedPackage.Name;
        bool confirmed = await _confirmDialogService.ConfirmAsync(
            "删除模板包", $"确定要删除模板包“{packageName}”吗？删除后不可恢复。");
        if (!confirmed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _packageService.DeletePackageAsync(packageName, CancellationToken.None);
            await ReloadPackagesAsync();
            _dialogService.ShowInfo($"已删除模板包“{packageName}”。");
        }
        catch (Exception exception) when (exception is TemplatePackageException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _dialogService.ShowError($"删除模板包失败：{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 判定刷新与导入命令是否可执行：非繁忙状态即可操作。
    /// </summary>
    private bool CanOperateWhenIdle() => !IsBusy;

    /// <summary>
    /// 判定进入复制命令是否可执行：列表存在选中包且非繁忙。
    /// </summary>
    private bool CanBeginCopy() => SelectedPackage is not null && !IsBusy;

    /// <summary>
    /// 判定确认复制命令是否可执行：处于复制模式、新包名非空且非繁忙。
    /// </summary>
    private bool CanConfirmCopy() => IsCopyModeActive && !string.IsNullOrWhiteSpace(NewPackageName) && !IsBusy;

    /// <summary>
    /// 判定取消复制命令是否可执行：处于复制模式且非繁忙。
    /// </summary>
    private bool CanCancelCopy() => IsCopyModeActive && !IsBusy;

    /// <summary>
    /// 判定导出命令是否可执行：列表存在选中包且非繁忙。
    /// </summary>
    private bool CanManageSelected() => SelectedPackage is not null && !IsBusy;

    /// <summary>
    /// 判定删除命令是否可执行：选中用户包（非内置）且非繁忙，内置包只读禁止删除。
    /// </summary>
    private bool CanDeleteSelected() => SelectedPackage is not null && !SelectedPackage.IsBuiltin && !IsBusy;

    /// <summary>
    /// 执行导入类操作并统一处理冲突结果：同名用户包覆盖确认、内置包同名只读拒绝、成功刷新列表。
    /// </summary>
    /// <param name="operation">以覆盖标志为入参的安装操作委托。</param>
    /// <param name="actionName">操作名称，用于结果与错误提示。</param>
    private async Task RunInstallOperationAsync(Func<bool, Task<TemplatePackageOperationResult>> operation, string actionName)
    {
        IsBusy = true;
        try
        {
            TemplatePackageOperationResult result = await operation(false);

            // 与用户包同名时经用户确认后以覆盖标志重试；取消确认则本次安装终止
            if (result.Status == TemplatePackageOperationStatus.NameConflict)
            {
                bool confirmed = await _confirmDialogService.ConfirmAsync(
                    "同名用户包已存在", $"{result.Message}\n是否确认覆盖同名用户包？");
                if (confirmed)
                {
                    result = await operation(true);
                }
            }

            if (result.Status == TemplatePackageOperationStatus.Succeeded)
            {
                await ReloadPackagesAsync();
                _dialogService.ShowInfo($"{actionName}成功：{result.Package?.Name}。");
            }
            else if (result.Status == TemplatePackageOperationStatus.BuiltinConflict)
            {
                // 目标为内置包同名：内置包只读，直接拒绝且不进入覆盖确认
                _dialogService.ShowError(result.Message);
            }
            else if (result.Status == TemplatePackageOperationStatus.NameConflict)
            {
                // 用户取消覆盖确认，本次安装不执行
            }
            else
            {
                _dialogService.ShowError($"{actionName}失败：{result.Message}");
            }
        }
        catch (Exception exception) when (exception is TemplatePackageException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _dialogService.ShowError($"{actionName}失败：{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 从服务重新加载模板包列表，单个包加载异常由服务内部跳过，不中断整体列表。
    /// </summary>
    private async Task ReloadPackagesAsync()
    {
        try
        {
            IReadOnlyList<TemplatePackageInfo> packages = await _packageService.ListPackagesAsync(CancellationToken.None);
            Packages.Clear();
            foreach (TemplatePackageInfo package in packages)
            {
                Packages.Add(new TemplatePackageListItemViewModel(package));
            }
        }
        catch (Exception exception) when (exception is TemplatePackageException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _dialogService.ShowError($"刷新模板包列表失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 退出复制模式：清空新包名与源包名并收起复制输入区。
    /// </summary>
    private void ExitCopyMode()
    {
        _copySourceName = null;
        NewPackageName = string.Empty;
        IsCopyModeActive = false;
        OnPropertyChanged(nameof(CopySourceDisplay));
        StatusText = string.Empty;
    }

    /// <summary>
    /// 列表选中项变化时更新底部状态提示：内置包提示只读边界与复制引导。
    /// </summary>
    /// <param name="value">变更后的选中包，无选中时为 null。</param>
    partial void OnSelectedPackageChanged(TemplatePackageListItemViewModel? value)
    {
        if (value?.IsBuiltin == true)
        {
            StatusText = "内置包只读：不可删除、不可覆盖，可先复制为可编辑的用户包。";
        }
        else
        {
            StatusText = string.Empty;
        }
    }
}
