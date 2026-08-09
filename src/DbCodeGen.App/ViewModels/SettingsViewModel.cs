using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbCodeGen.App.Services;
using DbCodeGen.Core.Ai;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Security;

namespace DbCodeGen.App.ViewModels;

/// <summary>
/// 设置窗口视图模型，承载工作区根、LLM 配置、模板搜索目录、最近相对输出根与 AI 参考文件限制五项配置的加载、校验与保存。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IConfigService _configService;
    private readonly IDialogService _dialogService;
    private readonly IFolderPickerService _folderPickerService;
    private readonly CredentialProtector _credentialProtector;
    private readonly ILlmClient _llmClient;

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
    /// LLM 请求超时秒数，默认 300；写模板/改模板调用 LLM 与测试连接共用。
    /// </summary>
    [ObservableProperty]
    private int _llmTimeoutSeconds = LlmConfig.DefaultTimeoutSeconds;

    /// <summary>
    /// 模型下拉候选项，初始为常用模型清单与已保存模型，测试连接成功后按端点实际支持刷新。
    /// </summary>
    public ObservableCollection<string> ModelOptions { get; } = new();

    /// <summary>
    /// 是否正在测试 LLM 连接，期间禁用测试按钮防重复提交。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestLlmConnectionCommand))]
    private bool _isTestingLlm;

    /// <summary>
    /// 测试连接结果状态文本，供界面内联展示。
    /// </summary>
    [ObservableProperty]
    private string _llmTestStatusText = string.Empty;

    /// <summary>
    /// 测试连接结果状态颜色，成功绿色、失败红色、进行中灰色。
    /// </summary>
    [ObservableProperty]
    private Brush _llmTestStatusColor = Brushes.Gray;

    /// <summary>
    /// 最近相对输出根，单值记忆上次语义。
    /// </summary>
    [ObservableProperty]
    private string _lastRelativeOutputRoot = string.Empty;

    /// <summary>
    /// AI 参考文件数量上限，单位个，默认 20。
    /// </summary>
    [ObservableProperty]
    private int _aiReferenceMaxFileCount = AiReferenceFileLimits.DefaultMaxFileCount;

    /// <summary>
    /// AI 参考文件单文件大小上限，单位 MB，默认 1。
    /// </summary>
    [ObservableProperty]
    private int _aiReferenceMaxSingleFileMb = (int)(AiReferenceFileLimits.DefaultMaxSingleFileBytes / 1048576);

    /// <summary>
    /// AI 参考文件总大小上限，单位 MB，默认 10。
    /// </summary>
    [ObservableProperty]
    private int _aiReferenceMaxTotalMb = (int)(AiReferenceFileLimits.DefaultMaxTotalBytes / 1048576);

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
    /// <param name="llmClient">LLM 对话客户端，用于测试连接与拉取模型列表。</param>
    /// <exception cref="ArgumentNullException">任一依赖参数为 null 时抛出。</exception>
    public SettingsViewModel(
        IConfigService configService,
        IDialogService dialogService,
        IFolderPickerService folderPickerService,
        CredentialProtector credentialProtector,
        ILlmClient llmClient)
    {
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(folderPickerService);
        ArgumentNullException.ThrowIfNull(credentialProtector);
        ArgumentNullException.ThrowIfNull(llmClient);

        _configService = configService;
        _dialogService = dialogService;
        _folderPickerService = folderPickerService;
        _credentialProtector = credentialProtector;
        _llmClient = llmClient;

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
    /// 测试 LLM 连接：校验接口地址与 API Key 后发起最小对话请求，成功后按端点实际支持刷新模型下拉候选并展示结果。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTestLlmConnection))]
    private async Task TestLlmConnectionAsync()
    {
        // 前置校验：接口地址必须为合法 http/https 绝对地址，与保存校验规则一致
        if (string.IsNullOrWhiteSpace(LlmBaseUrl) ||
            !Uri.TryCreate(LlmBaseUrl.Trim(), UriKind.Absolute, out Uri? baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            LlmTestStatusColor = Brushes.Red;
            LlmTestStatusText = "接口地址不合法，请先填写正确的 http/https 地址。";
            return;
        }

        // apiKey 优先取密码框新输入，未输入时取已保存的加密密钥解密明文
        string apiKey = string.IsNullOrWhiteSpace(ApiKeyInput)
            ? (_configService.GetLlmApiKey() ?? string.Empty)
            : ApiKeyInput.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            LlmTestStatusColor = Brushes.Red;
            LlmTestStatusText = "API Key 未填写，无法测试连接。";
            return;
        }

        var options = new LlmClientOptions
        {
            BaseUrl = LlmBaseUrl.Trim(),
            Model = LlmModel.Trim(),
            ApiKey = apiKey,
            TimeoutSeconds = LlmTimeoutSeconds
        };

        IsTestingLlm = true;
        LlmTestStatusColor = Brushes.Gray;
        LlmTestStatusText = "正在测试连接…";
        try
        {
            LlmChatResponse response = await _llmClient.TestConnectionAsync(options, CancellationToken.None);
            if (response.IsSuccess)
            {
                LlmTestStatusColor = Brushes.Green;
                LlmTestStatusText = "连接成功。";
                await RefreshModelOptionsAsync(options);
            }
            else
            {
                LlmTestStatusColor = Brushes.Red;
                LlmTestStatusText = $"连接失败：{response.ErrorMessage}";
            }
        }
        catch (Exception exception)
        {
            LlmTestStatusColor = Brushes.Red;
            LlmTestStatusText = $"连接失败：{exception.Message}";
        }
        finally
        {
            IsTestingLlm = false;
        }
    }

    /// <summary>
    /// 判定测试连接命令是否可执行：未在测试中时可触发，防重复提交。
    /// </summary>
    private bool CanTestLlmConnection() => !IsTestingLlm;

    /// <summary>
    /// 按端点实际支持刷新模型下拉候选，拉取失败时保留初始候选，为尽力而为不阻断测试结果。
    /// </summary>
    /// <param name="options">瞬态调用配置，含端点与明文 apiKey。</param>
    private async Task RefreshModelOptionsAsync(LlmClientOptions options)
    {
        try
        {
            IReadOnlyList<string> models = await _llmClient.ListModelsAsync(options, CancellationToken.None);
            if (models.Count == 0)
            {
                return;
            }

            ModelOptions.Clear();
            foreach (string model in models)
            {
                ModelOptions.Add(model);
            }

            // 当前填写模型不在端点支持列表时保留原值，保证下拉不丢当前选择
            string current = LlmModel.Trim();
            if (!string.IsNullOrEmpty(current)
                && !ModelOptions.Any(model => string.Equals(model, current, StringComparison.OrdinalIgnoreCase)))
            {
                ModelOptions.Add(current);
            }
        }
        catch (Exception)
        {
            // 模型列表刷新失败保留初始候选，不改变连接测试结果
        }
    }

    /// <summary>
    /// 仅保存 LLM 配置（接口地址/模型/API Key），只校验 LLM 相关字段，不被工作区根、模板搜索目录等其它设置项卡住，保存后窗口不关闭。
    /// </summary>
    [RelayCommand]
    private void SaveLlmConfig()
    {
        // 仅校验 LLM 相关字段，其它设置项不参与本次保存，避免无关校验阻断 LLM 配置落盘
        if (string.IsNullOrWhiteSpace(LlmBaseUrl) ||
            !Uri.TryCreate(LlmBaseUrl.Trim(), UriKind.Absolute, out Uri? baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            _dialogService.ShowError("LLM 接口地址必须为合法的 http/https 地址。");
            return;
        }

        if (string.IsNullOrWhiteSpace(LlmModel))
        {
            _dialogService.ShowError("LLM 模型名不能为空。");
            return;
        }

        // 请求超时必须为正整数，防止非法值使 LLM 调用瞬间超时或无限挂起
        if (LlmTimeoutSeconds < 1)
        {
            _dialogService.ShowError("请求超时必须为不小于 1 的整数（秒）。");
            return;
        }

        // 将 LLM 表单值写入配置快照；apiKey 输入新值则重新加密覆盖，留空保持原密文
        AppConfig config = _configService.Current;
        config.Llm.BaseUrl = LlmBaseUrl.Trim();
        config.Llm.Model = LlmModel.Trim();
        config.Llm.TimeoutSeconds = LlmTimeoutSeconds;
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
            _dialogService.ShowError($"LLM 配置保存失败：{exception.Message}");
            return;
        }

        _dialogService.ShowInfo("LLM 配置已保存，将在下次使用相关功能时生效。");
    }

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
        config.Llm.TimeoutSeconds = LlmTimeoutSeconds;
        config.TemplateSearchDirectories = BuildDeduplicatedTemplateDirectories();

        // AI 参考文件限制：MB×1024×1024 换算字节写入配置快照，以 long 运算防溢出
        config.AiReferenceFileLimits.MaxFileCount = AiReferenceMaxFileCount;
        config.AiReferenceFileLimits.MaxSingleFileBytes = AiReferenceMaxSingleFileMb * 1024L * 1024L;
        config.AiReferenceFileLimits.MaxTotalBytes = AiReferenceMaxTotalMb * 1024L * 1024L;

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
        LlmTimeoutSeconds = config.Llm.TimeoutSeconds;
        LastRelativeOutputRoot = config.LastRelativeOutputRoot;

        // AI 参考文件限制：字节→MB 换算，向下取整且至少为 1，展示值不高于实际上限不虚标
        AiReferenceMaxFileCount = config.AiReferenceFileLimits.MaxFileCount;
        AiReferenceMaxSingleFileMb = Math.Max(1, (int)(config.AiReferenceFileLimits.MaxSingleFileBytes / 1048576.0));
        AiReferenceMaxTotalMb = Math.Max(1, (int)(config.AiReferenceFileLimits.MaxTotalBytes / 1048576.0));

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

        // 模型下拉初始候选：常用模型清单 + 已保存模型（不在候选时补充），保证下拉展示当前生效值
        ModelOptions.Clear();
        foreach (string model in LlmConfig.CommonModels)
        {
            ModelOptions.Add(model);
        }

        string savedModel = config.Llm.Model?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(savedModel)
            && !ModelOptions.Any(model => string.Equals(model, savedModel, StringComparison.OrdinalIgnoreCase)))
        {
            ModelOptions.Add(savedModel);
        }

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

        // 请求超时必须为正整数，防止非法值使 LLM 调用瞬间超时或无限挂起
        if (LlmTimeoutSeconds < 1)
        {
            errorMessage = "请求超时必须为不小于 1 的整数（秒）。";
            return false;
        }

        // AI 参考文件限制三个值均必须为正整数，且单文件上限不得大于总大小上限，防止 F02/F03 校验死锁
        if (AiReferenceMaxFileCount < 1)
        {
            errorMessage = "参考文件数量上限必须为不小于 1 的整数。";
            return false;
        }

        if (AiReferenceMaxSingleFileMb < 1)
        {
            errorMessage = "单文件大小上限必须为不小于 1 的整数（MB）。";
            return false;
        }

        if (AiReferenceMaxTotalMb < 1)
        {
            errorMessage = "总大小上限必须为不小于 1 的整数（MB）。";
            return false;
        }

        if (AiReferenceMaxSingleFileMb > AiReferenceMaxTotalMb)
        {
            errorMessage = "单文件大小上限不得大于总大小上限。";
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
