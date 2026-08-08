using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbCodeGen.App.Services;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Security;

namespace DbCodeGen.App.ViewModels;

/// <summary>
/// 设置窗口视图模型，承载工作区根、LLM 配置、模板搜索目录与最近相对输出根四项配置的加载、校验与保存。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IConfigService _configService;
    private readonly IDialogService _dialogService;
    private readonly IFolderPickerService _folderPickerService;
    private readonly CredentialProtector _credentialProtector;

    /// <summary>
    /// 配置加载时检测到的“曾损坏恢复”标记，窗口内容呈现后据此提示用户。
    /// </summary>
    private bool _recoveryNoticePending;

    /// <summary>
    /// 密码框当前输入的 apiKey 明文，由设置窗口在密码变化时回填；留空表示保持原密文。
    /// </summary>
    private string _apiKeyInput = string.Empty;

    /// <summary>
    /// 工作区根默认路径。
    /// </summary>
    [ObservableProperty]
    private string _workspaceRoot = string.Empty;

    /// <summary>
    /// LLM 接口地址（OpenAI 兼容协议端点）。
    /// </summary>
    [ObservableProperty]
    private string _llmBaseUrl = string.Empty;

    /// <summary>
    /// LLM 模型名。
    /// </summary>
    [ObservableProperty]
    private string _llmModel = string.Empty;

    /// <summary>
    /// 最近相对输出根，单值记忆上次语义。
    /// </summary>
    [ObservableProperty]
    private string _lastRelativeOutputRoot = string.Empty;

    /// <summary>
    /// 模板搜索目录列表，支持添加与移除。
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<string> _templateDirectories = new();

    /// <summary>
    /// 当前选中的模板搜索目录项，用于移除操作。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveTemplateDirectoryCommand))]
    private string? _selectedTemplateDirectory;

    /// <summary>
    /// 保存成功后触发，设置窗口据此关闭自身。
    /// </summary>
    public event EventHandler? SaveCompleted;

    /// <summary>
    /// 使用配置服务、对话框服务与凭据保护器构造设置视图模型，并立即从配置快照回填表单。
    /// </summary>
    /// <param name="configService">配置持久化服务，读写的唯一通道。</param>
    /// <param name="dialogService">消息提示服务，用于校验失败与保存结果反馈。</param>
    /// <param name="folderPickerService">目录选择服务，用于工作区根与模板搜索目录的路径输入。</param>
    /// <param name="credentialProtector">Windows DPAPI 凭据保护器，用于 apiKey 新值的加密落盘。</param>
    /// <exception cref="ArgumentNullException">任一依赖参数为 null 时抛出。</exception>
    public SettingsViewModel(
        IConfigService configService,
        IDialogService dialogService,
        IFolderPickerService folderPickerService,
        CredentialProtector credentialProtector)
    {
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(folderPickerService);
        ArgumentNullException.ThrowIfNull(credentialProtector);

        _configService = configService;
        _dialogService = dialogService;
        _folderPickerService = folderPickerService;
        _credentialProtector = credentialProtector;

        LoadFromConfig();
    }

    /// <summary>
    /// 密码框当前输入的 apiKey 明文，留空表示保持原密文不重加密。
    /// </summary>
    public string ApiKeyInput
    {
        get => _apiKeyInput;
        set => _apiKeyInput = value ?? string.Empty;
    }

    /// <summary>
    /// 窗口内容呈现后调用，若加载时检测到配置文件曾损坏恢复则提示用户已重建默认配置。
    /// </summary>
    public void NotifyConfigurationRecoveryIfNeeded()
    {
        if (!_recoveryNoticePending)
        {
            return;
        }

        _recoveryNoticePending = false;
        _dialogService.ShowInfo("检测到配置文件此前损坏，已自动备份原文件并重建为默认配置，请检查各项设置。");
    }

    /// <summary>
    /// 弹出目录选择框并回填工作区根。
    /// </summary>
    [RelayCommand]
    private async Task BrowseWorkspaceRootAsync()
    {
        string? selected = await _folderPickerService.PickFolderAsync(WorkspaceRoot, "选择工作区根目录");
        if (!string.IsNullOrWhiteSpace(selected))
        {
            WorkspaceRoot = selected;
        }
    }

    /// <summary>
    /// 弹出目录选择框并将选中的目录追加到模板搜索目录列表，已存在的目录不重复添加。
    /// </summary>
    [RelayCommand]
    private async Task AddTemplateDirectoryAsync()
    {
        string? selected = await _folderPickerService.PickFolderAsync(null, "添加模板搜索目录");
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        // 已存在的目录不再重复添加，按路径规范化后忽略大小写比较
        bool alreadyAdded = TemplateDirectories.Any(existing =>
            string.Equals(NormalizeDirectoryPath(existing), NormalizeDirectoryPath(selected), StringComparison.OrdinalIgnoreCase));
        if (alreadyAdded)
        {
            _dialogService.ShowInfo("该目录已在模板搜索目录列表中。");
            return;
        }

        TemplateDirectories.Add(selected);
    }

    /// <summary>
    /// 从模板搜索目录列表移除当前选中项，仅移除列表条目，不删除目录内容。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveTemplateDirectory))]
    private void RemoveTemplateDirectory()
    {
        if (SelectedTemplateDirectory is null)
        {
            return;
        }

        TemplateDirectories.Remove(SelectedTemplateDirectory);
        SelectedTemplateDirectory = null;
    }

    /// <summary>
    /// 判定移除模板目录命令是否可执行：存在选中项时可移除。
    /// </summary>
    private bool CanRemoveTemplateDirectory() => SelectedTemplateDirectory is not null;

    /// <summary>
    /// 校验表单并保存配置：写入配置快照后落盘，成功后提示并触发窗口关闭。
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        if (!TryValidate(out string? errorMessage))
        {
            _dialogService.ShowError(errorMessage);
            return;
        }

        // 将表单值写入配置快照；apiKey 留空保持原密文，输入新值则重新加密覆盖
        AppConfig config = _configService.Current;
        config.WorkspaceRoot = WorkspaceRoot;
        config.LastRelativeOutputRoot = LastRelativeOutputRoot;
        config.Llm.BaseUrl = LlmBaseUrl.Trim();
        config.Llm.Model = LlmModel.Trim();
        config.TemplateSearchDirectories = BuildDeduplicatedTemplateDirectories();

        if (!string.IsNullOrWhiteSpace(ApiKeyInput))
        {
            config.Llm.ApiKeyEncrypted = _credentialProtector.Encrypt(ApiKeyInput);
        }

        try
        {
            _configService.Save();
        }
        catch (ConfigSaveException exception)
        {
            // 写盘失败时磁盘未更新，已编辑值保留在内存中，提示用户可重试且窗口不关闭
            _dialogService.ShowError($"配置保存失败：{exception.Message}");
            return;
        }

        _dialogService.ShowInfo("配置已保存，将在下次使用相应功能时生效。");
        SaveCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 从配置服务快照回填表单字段，并检测是否存在曾损坏恢复的备份文件。
    /// </summary>
    private void LoadFromConfig()
    {
        // 加载配置快照；文件不存在自动生成默认并落盘，损坏时备份重建，均在 ConfigService 内部完成
        AppConfig config = _configService.Load();

        WorkspaceRoot = config.WorkspaceRoot;
        LlmBaseUrl = config.Llm.BaseUrl;
        LlmModel = config.Llm.Model;
        LastRelativeOutputRoot = config.LastRelativeOutputRoot;

        TemplateDirectories.Clear();
        foreach (string directory in config.TemplateSearchDirectories)
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                TemplateDirectories.Add(directory);
            }
        }

        // apiKey 密码框始终留空，原密文保留在 Current.Llm.ApiKeyEncrypted，输入新值才重加密覆盖
        ApiKeyInput = string.Empty;

        // 存在备份文件说明配置曾损坏并已重建，标记待窗口呈现后提示用户
        _recoveryNoticePending = HasRecoveryBackup();
    }

    /// <summary>
    /// 校验表单字段，任一不合法时输出可读错误消息。
    /// </summary>
    /// <param name="errorMessage">校验失败的可读消息；校验通过为 null。</param>
    private bool TryValidate([NotNullWhen(false)] out string? errorMessage)
    {
        // 工作区根已填写时必须存在且可写；未填写视为未设置，允许保存
        if (!string.IsNullOrWhiteSpace(WorkspaceRoot) && !IsDirectoryWritable(WorkspaceRoot))
        {
            errorMessage = "工作区根目录不存在或不可写，请重新选择。";
            return false;
        }

        // LLM 接口地址必须为合法的 http/https 绝对地址
        if (string.IsNullOrWhiteSpace(LlmBaseUrl) ||
            !Uri.TryCreate(LlmBaseUrl.Trim(), UriKind.Absolute, out Uri? baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            errorMessage = "LLM 接口地址必须为合法的 http/https 地址。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(LlmModel))
        {
            errorMessage = "LLM 模型名不能为空。";
            return false;
        }

        // 模板搜索目录逐条校验目录存在，保证模板包管理可正常扫描
        foreach (string directory in TemplateDirectories)
        {
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                errorMessage = $"模板搜索目录不存在：{directory}";
                return false;
            }
        }

        errorMessage = null;
        return true;
    }

    /// <summary>
    /// 按路径规范化去重后构建待持久化的模板搜索目录列表，跳过空白项。
    /// </summary>
    private List<string> BuildDeduplicatedTemplateDirectories()
    {
        List<string> result = new();
        foreach (string directory in TemplateDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            string normalized = NormalizeDirectoryPath(directory);
            bool duplicated = result.Any(existing =>
                string.Equals(NormalizeDirectoryPath(existing), normalized, StringComparison.OrdinalIgnoreCase));
            if (!duplicated)
            {
                result.Add(directory);
            }
        }

        return result;
    }

    /// <summary>
    /// 检测配置文件目录下是否存在损坏恢复产生的带时间戳备份文件。
    /// </summary>
    private bool HasRecoveryBackup()
    {
        try
        {
            string configDirectory = Path.GetDirectoryName(_configService.ConfigFilePath) ?? string.Empty;
            if (!Directory.Exists(configDirectory))
            {
                return false;
            }

            string backupPattern = Path.GetFileName(_configService.ConfigFilePath) + ".bak.*";
            return Directory.EnumerateFiles(configDirectory, backupPattern).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            // 配置目录不可访问时按无备份处理，不阻断设置窗口打开
            return false;
        }
    }

    /// <summary>
    /// 判定目录是否存在且实际可写，通过写入并删除探测文件验证。
    /// </summary>
    private static bool IsDirectoryWritable(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return false;
            }

            // 写入探测文件再删除，验证目录真实可写，避免仅按存在性误判
            string probeFile = Path.Combine(path, $".write_probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probeFile, string.Empty);
            File.Delete(probeFile);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException or ArgumentException)
        {
            // 目录不可写或路径畸形时按不可写处理，交由校验提示用户
            return false;
        }
    }

    /// <summary>
    /// 将目录路径规范化为绝对路径并去除末尾目录分隔符，用于列表去重比较。
    /// </summary>
    private static string NormalizeDirectoryPath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            // 畸形路径无法规范化时原样返回，交由后续存在性校验兜底
            return path;
        }
    }
}
