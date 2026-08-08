using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbCodeGen.App.Services;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.DataSource;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Security;

namespace DbCodeGen.App.ViewModels;

/// <summary>
/// 数据源列表行视图模型，承载连接列表的只读展示信息与当前连接标记。
/// 密码只在配置中存密文，列表行一律以掩码展示，绝不出现明文。
/// </summary>
public sealed partial class DataSourceListItemViewModel : ObservableObject
{
    /// <summary>
    /// 使用已保存的数据源配置构造列表行。
    /// </summary>
    /// <param name="config">已保存的数据源连接配置。</param>
    /// <exception cref="ArgumentNullException">config 为 null 时抛出。</exception>
    public DataSourceListItemViewModel(DataSourceConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// 已保存的数据源连接配置，列表各列直接绑定其字段。
    /// </summary>
    public DataSourceConfig Config { get; }

    /// <summary>
    /// 密码掩码展示文本，列表绝不展示明文密码。
    /// </summary>
    public string MaskedPassword => "******";

    /// <summary>
    /// 是否为当前连接，为 true 时列表展示当前连接标记。
    /// </summary>
    [ObservableProperty]
    private bool _isCurrent;
}

/// <summary>
/// 数据源管理窗口视图模型，承载连接配置的增删改查、测试连接、保存落盘与当前连接设置。
/// 密码明文只在表单输入与加密/测试的瞬态存在，列表与持久化只持有 DPAPI 密文。
/// 测试连接成功才允许保存为固定强制规则，任何表单变更后须重新测试。
/// </summary>
public sealed partial class DataSourceViewModel : ObservableObject
{
    /// <summary>
    /// MySQL 默认端口。
    /// </summary>
    private const int MySqlDefaultPort = 3306;

    /// <summary>
    /// PostgreSQL 默认端口。
    /// </summary>
    private const int PostgreSqlDefaultPort = 5432;

    /// <summary>
    /// 尚未测试连接时的状态提示文本。
    /// </summary>
    private const string UntestedStatusText = "尚未测试连接，请先测试连接后再保存。";

    private readonly IConfigService _configService;
    private readonly IDataSourceService _dataSourceService;
    private readonly ICurrentDataSourceService _currentDataSourceService;
    private readonly IDialogService _dialogService;
    private readonly IConfirmDialogService _confirmDialogService;
    private readonly CredentialProtector _credentialProtector;

    /// <summary>
    /// 本次测试连接是否成功，保存前的固定强制前置条件。
    /// </summary>
    private bool _isConnectionTested;

    /// <summary>
    /// 端口是否跟随数据库类型默认值，用户手工定制端口后停止跟随。
    /// </summary>
    private bool _portFollowsType = true;

    /// <summary>
    /// 编辑模式下被编辑连接的原始名称，用于名称唯一性排除与落盘定位。
    /// </summary>
    private string? _editingOriginalName;

    /// <summary>
    /// 编辑模式下被编辑连接的原始密码密文，密码留空时保持此密文。
    /// </summary>
    private string _editingPasswordCipher = string.Empty;

    /// <summary>
    /// 测试连接是否正在进行中，防止并发触发多次测试。
    /// </summary>
    private bool _isTestInProgress;

    /// <summary>
    /// 密码框当前输入的明文，由窗口在密码变化时回填；留空表示编辑时保持原密文。
    /// </summary>
    private string _passwordInput = string.Empty;

    /// <summary>
    /// 已保存的连接列表，行内只读展示配置字段与当前连接标记。
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<DataSourceListItemViewModel> _dataSources = new();

    /// <summary>
    /// 列表当前选中行，供编辑、删除与设为当前连接操作定位目标。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetCurrentCommand))]
    private DataSourceListItemViewModel? _selectedDataSource;

    /// <summary>
    /// 表单连接名称。
    /// </summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>
    /// 表单数据库类型，切换时端口默认值联动。
    /// </summary>
    [ObservableProperty]
    private DataSourceType _selectedType;

    /// <summary>
    /// 表单主机名或 IP 地址。
    /// </summary>
    [ObservableProperty]
    private string _host = string.Empty;

    /// <summary>
    /// 表单端口文本，保存与测试前按 1-65535 整数校验。
    /// </summary>
    [ObservableProperty]
    private string _portText = string.Empty;

    /// <summary>
    /// 表单数据库名。
    /// </summary>
    [ObservableProperty]
    private string _database = string.Empty;

    /// <summary>
    /// 表单用户名。
    /// </summary>
    [ObservableProperty]
    private string _userId = string.Empty;

    /// <summary>
    /// 测试连接状态提示文本，展示给用户确认当前表单是否可保存。
    /// </summary>
    [ObservableProperty]
    private string _testStatusText = UntestedStatusText;

    /// <summary>
    /// 表单是否处于测试连接等繁忙状态，繁忙时整体禁用界面。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFormIdle))]
    private bool _isFormBusy;

    /// <summary>
    /// 供可选的数据库类型列表，绑定到类型下拉框。
    /// </summary>
    public DataSourceType[] AvailableTypes { get; } = new[] { DataSourceType.MySql, DataSourceType.PostgreSql };

    /// <summary>
    /// 界面是否空闲可用，与繁忙状态相反，用于禁用表单。
    /// </summary>
    public bool IsFormIdle => !IsFormBusy;

    /// <summary>
    /// 请求窗口清空密码框的事件，表单重置或切换编辑对象时触发。
    /// </summary>
    public event EventHandler? PasswordClearRequested;

    /// <summary>
    /// 密码框当前输入的明文，留空表示编辑时保持原密文不重新加密。
    /// </summary>
    public string PasswordInput
    {
        get => _passwordInput;
        set
        {
            _passwordInput = value ?? string.Empty;
            ResetTestState();
        }
    }

    /// <summary>
    /// 使用配置服务、数据源服务、当前连接服务与对话框服务构造数据源管理视图模型，
    /// 并立即加载已保存的连接列表与默认表单。
    /// </summary>
    /// <param name="configService">配置持久化服务，连接列表读写的唯一通道。</param>
    /// <param name="dataSourceService">连接测试服务，用于保存前的连接可用性验证。</param>
    /// <param name="currentDataSourceService">当前连接共享状态服务，用于设为当前与删除联动清除。</param>
    /// <param name="dialogService">消息提示服务，用于校验与操作结果反馈。</param>
    /// <param name="confirmDialogService">二次确认服务，用于删除连接前的确认。</param>
    /// <param name="credentialProtector">Windows DPAPI 凭据保护器，用于密码加密落盘。</param>
    /// <exception cref="ArgumentNullException">任一依赖参数为 null 时抛出。</exception>
    public DataSourceViewModel(
        IConfigService configService,
        IDataSourceService dataSourceService,
        ICurrentDataSourceService currentDataSourceService,
        IDialogService dialogService,
        IConfirmDialogService confirmDialogService,
        CredentialProtector credentialProtector)
    {
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(dataSourceService);
        ArgumentNullException.ThrowIfNull(currentDataSourceService);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(confirmDialogService);
        ArgumentNullException.ThrowIfNull(credentialProtector);

        _configService = configService;
        _dataSourceService = dataSourceService;
        _currentDataSourceService = currentDataSourceService;
        _dialogService = dialogService;
        _confirmDialogService = confirmDialogService;
        _credentialProtector = credentialProtector;

        // 确保配置已加载，随后订阅当前连接变更以同步列表中的当前标记
        _configService.Load();
        _currentDataSourceService.CurrentChanged += OnCurrentChanged;
        ResetToNewMode();
        ReloadDataSourceList();
    }

    /// <summary>
    /// 窗口关闭时调用，解除当前连接变更事件订阅，避免悬挂引用。
    /// </summary>
    public void Detach()
    {
        _currentDataSourceService.CurrentChanged -= OnCurrentChanged;
    }

    /// <summary>
    /// 新增连接：清空表单进入新增模式，类型与端口恢复默认值。
    /// </summary>
    [RelayCommand]
    private void New()
    {
        ResetToNewMode();
    }

    /// <summary>
    /// 编辑连接：将选中连接的值回填到表单，密码留空表示保持原密文。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanManageSelected))]
    private void Edit()
    {
        if (SelectedDataSource is null)
        {
            return;
        }

        LoadConfigToForm(SelectedDataSource.Config);
    }

    /// <summary>
    /// 删除连接：删除前二次确认，删除的为当前连接时联动清除当前连接。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanManageSelected))]
    private async Task DeleteAsync()
    {
        if (SelectedDataSource is null)
        {
            return;
        }

        DataSourceConfig target = SelectedDataSource.Config;
        bool confirmed = await _confirmDialogService.ConfirmAsync(
            "删除连接", $"确定要删除连接“{target.Name}”吗？删除后不可恢复。");
        if (!confirmed)
        {
            return;
        }

        // 从配置快照移除并落盘，本地工具无外键关联，不级联删除已生成代码
        AppConfig config = _configService.Current;
        int removedIndex = config.DataSources.FindIndex(existing =>
            string.Equals(existing.Name, target.Name, StringComparison.Ordinal));
        if (removedIndex < 0)
        {
            // 目标连接已不在内存列表中，仅刷新列表保证界面与内存一致
            ReloadDataSourceList();
            return;
        }

        DataSourceConfig removed = config.DataSources[removedIndex];
        config.DataSources.RemoveAt(removedIndex);
        try
        {
            _configService.Save();
        }
        catch (ConfigSaveException exception)
        {
            // 写盘失败回滚删除并刷新列表，保证界面与内存一致
            config.DataSources.Insert(removedIndex, removed);
            ReloadDataSourceList();
            _dialogService.ShowError($"删除连接失败：{exception.Message}");
            return;
        }

        // 删除的连接为当前连接时联动清除，表浏览/SQL 面板回到未连接
        if (string.Equals(_currentDataSourceService.Current?.Name, target.Name, StringComparison.Ordinal))
        {
            _currentDataSourceService.ClearCurrent();
        }

        ReloadDataSourceList();

        // 正在编辑的连接被删除时回到新增模式，避免表单持有已删除的连接
        if (string.Equals(_editingOriginalName, target.Name, StringComparison.Ordinal))
        {
            ResetToNewMode();
        }

        _dialogService.ShowInfo($"已删除连接“{target.Name}”。");
    }

    /// <summary>
    /// 设为当前连接：将选中连接设置为当前连接，表浏览/SQL 面板据此联动。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanManageSelected))]
    private void SetCurrent()
    {
        if (SelectedDataSource is null)
        {
            return;
        }

        _currentDataSourceService.SetCurrent(SelectedDataSource.Config);
        RefreshCurrentFlags();
        _dialogService.ShowInfo($"已将“{SelectedDataSource.Config.Name}”设为当前连接。");
    }

    /// <summary>
    /// 测试连接：先校验表单再调用连接服务测试，成功后允许保存。
    /// </summary>
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (_isTestInProgress)
        {
            return;
        }

        if (!TryValidateForm(out string? errorMessage))
        {
            _dialogService.ShowError(errorMessage);
            return;
        }

        _isTestInProgress = true;
        IsFormBusy = true;
        try
        {
            TestConnectionInput input = BuildTestInput();
            TestConnectionResult result = await _dataSourceService.TestConnectionAsync(input, CancellationToken.None);

            if (result.IsSuccess)
            {
                // 测试成功置位保存前置标记，表单不再变更时允许保存
                _isConnectionTested = true;
                TestStatusText = $"测试连接成功，服务端版本：{result.ServerVersion ?? "未知"}，可以保存。";
            }
            else
            {
                _isConnectionTested = false;
                TestStatusText = $"测试连接失败：{result.Message}";
                _dialogService.ShowError($"测试连接失败：{result.Message}");
            }
        }
        finally
        {
            IsFormBusy = false;
            _isTestInProgress = false;
        }
    }

    /// <summary>
    /// 保存连接：校验表单与测试前置条件，密码加密落盘后刷新列表。
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        if (!TryValidateForm(out string? errorMessage))
        {
            _dialogService.ShowError(errorMessage);
            return;
        }

        // 测试连接成功才允许保存为固定强制规则，未测试或表单变更后均不允许保存
        if (!_isConnectionTested)
        {
            _dialogService.ShowError("请先测试连接，测试连接成功后才允许保存。");
            return;
        }

        int port = int.Parse(PortText.Trim());
        DateTime now = DateTime.Now;
        AppConfig config = _configService.Current;
        bool isNew = _editingOriginalName is null;

        // 编辑场景先定位原配置并留存原值，保存失败时用于回滚内存快照
        int? editingIndex = null;
        DataSourceConfig? original = null;
        if (!isNew)
        {
            editingIndex = config.DataSources.FindIndex(existing =>
                string.Equals(existing.Name, _editingOriginalName, StringComparison.Ordinal));
            if (editingIndex < 0)
            {
                // 原连接已不在内存列表中（例如先删除后编辑），刷新列表并回到新增模式避免继续操作失效目标
                _dialogService.ShowError($"原连接“{_editingOriginalName}”已不存在，请刷新列表后重试。");
                ReloadDataSourceList();
                ResetToNewMode();
                return;
            }

            original = CloneDataSourceConfig(config.DataSources[editingIndex.Value]);
        }

        DataSourceConfig target;
        if (isNew)
        {
            // 新增连接：构造新配置并追加到列表，创建与更新时间统一取当前时间
            target = new DataSourceConfig
            {
                Name = Name.Trim(),
                Type = SelectedType,
                Host = Host.Trim(),
                Port = port,
                Database = Database.Trim(),
                UserId = UserId.Trim(),
                CreatedAt = now,
                UpdatedAt = now
            };
            config.DataSources.Add(target);
        }
        else
        {
            // 编辑连接：覆写可编辑字段并保留创建时间，目标对象仍在原列表位置
            target = config.DataSources[editingIndex!.Value];
            target.Name = Name.Trim();
            target.Type = SelectedType;
            target.Host = Host.Trim();
            target.Port = port;
            target.Database = Database.Trim();
            target.UserId = UserId.Trim();
            target.UpdatedAt = now;
        }

        // 密码输入了新值才重新加密覆盖，留空保持原密文
        if (!string.IsNullOrWhiteSpace(PasswordInput))
        {
            target.PasswordCipher = _credentialProtector.Encrypt(PasswordInput);
        }

        try
        {
            _configService.Save();
        }
        catch (ConfigSaveException exception)
        {
            // 写盘失败回滚内存快照：新增移除、编辑恢复原值，随后刷新列表保证界面与内存一致
            if (isNew)
            {
                config.DataSources.Remove(target);
            }
            else
            {
                config.DataSources[editingIndex!.Value] = original!;
            }

            ReloadDataSourceList();
            _dialogService.ShowError($"配置保存失败：{exception.Message}");
            return;
        }

        // 编辑的正是当前连接时刷新当前连接快照，保证消费方读到最新配置
        if (!isNew && string.Equals(_currentDataSourceService.Current?.Name, _editingOriginalName, StringComparison.Ordinal))
        {
            _currentDataSourceService.SetCurrent(target);
        }

        ReloadDataSourceList();
        ResetToNewMode();
        _dialogService.ShowInfo(isNew ? $"已新增连接“{target.Name}”。" : $"已更新连接“{target.Name}”。");
    }

    /// <summary>
    /// 取消当前编辑：清空表单回到新增模式，不写入任何配置。
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        ResetToNewMode();
    }

    /// <summary>
    /// 判定编辑、删除与设为当前连接命令是否可执行：列表存在选中项时可操作。
    /// </summary>
    private bool CanManageSelected() => SelectedDataSource is not null;

    /// <summary>
    /// 将表单重置为新增模式：清除编辑上下文与密码，类型和端口恢复默认值。
    /// </summary>
    private void ResetToNewMode()
    {
        _editingOriginalName = null;
        _editingPasswordCipher = string.Empty;
        _isConnectionTested = false;
        _portFollowsType = true;
        Name = string.Empty;
        SelectedType = DataSourceType.MySql;
        PortText = GetDefaultPort(DataSourceType.MySql).ToString();
        Host = string.Empty;
        Database = string.Empty;
        UserId = string.Empty;
        PasswordInput = string.Empty;
        TestStatusText = UntestedStatusText;
        PasswordClearRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 将选中连接配置回填到表单进入编辑模式，密码框清空以表达保持原密文语义。
    /// </summary>
    /// <param name="config">要编辑的数据源连接配置。</param>
    private void LoadConfigToForm(DataSourceConfig config)
    {
        _editingOriginalName = config.Name;
        _editingPasswordCipher = config.PasswordCipher;
        _isConnectionTested = false;
        _portFollowsType = config.Port == GetDefaultPort(config.Type);
        Name = config.Name;
        SelectedType = config.Type;
        PortText = config.Port.ToString();
        Host = config.Host;
        Database = config.Database;
        UserId = config.UserId;
        PasswordInput = string.Empty;
        TestStatusText = UntestedStatusText;
        PasswordClearRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 从配置快照重建连接列表，并同步各行的当前连接标记。
    /// </summary>
    private void ReloadDataSourceList()
    {
        DataSources.Clear();
        foreach (DataSourceConfig config in _configService.Current.DataSources)
        {
            if (config is not null)
            {
                DataSources.Add(new DataSourceListItemViewModel(config));
            }
        }

        RefreshCurrentFlags();
    }

    /// <summary>
    /// 按当前连接名称逐行刷新当前标记，保证列表与共享状态一致。
    /// </summary>
    private void RefreshCurrentFlags()
    {
        string? currentName = _currentDataSourceService.Current?.Name;
        foreach (DataSourceListItemViewModel item in DataSources)
        {
            item.IsCurrent = string.Equals(item.Config.Name, currentName, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 依据当前表单值构造测试连接输入，按明文/密文二选一契约承载密码。
    /// </summary>
    private TestConnectionInput BuildTestInput()
    {
        bool hasNewPassword = !string.IsNullOrWhiteSpace(PasswordInput);
        int port = int.Parse(PortText.Trim());

        return new TestConnectionInput
        {
            Type = SelectedType,
            Host = Host.Trim(),
            Port = port,
            Database = Database.Trim(),
            UserId = UserId.Trim(),
            // 密码输入了新值用明文测试；否则编辑场景沿用已保存密文（密文为空视为无密码），新增场景按空密码
            PlainPassword = hasNewPassword ? PasswordInput : null,
            SavedPasswordCipher = hasNewPassword || _editingOriginalName is null || string.IsNullOrEmpty(_editingPasswordCipher)
                ? null
                : _editingPasswordCipher
        };
    }

    /// <summary>
    /// 复制数据源配置为等值新实例，用于保存失败时回滚被编辑的原值。
    /// </summary>
    /// <param name="source">需要复制的数据源连接配置。</param>
    private static DataSourceConfig CloneDataSourceConfig(DataSourceConfig source)
    {
        return new DataSourceConfig
        {
            Name = source.Name,
            Type = source.Type,
            Host = source.Host,
            Port = source.Port,
            Database = source.Database,
            UserId = source.UserId,
            PasswordCipher = source.PasswordCipher,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt
        };
    }

    /// <summary>
    /// 校验表单字段，任一不合法时输出可读错误消息。
    /// </summary>
    /// <param name="errorMessage">校验失败的可读消息；校验通过为 null。</param>
    private bool TryValidateForm([NotNullWhen(false)] out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            errorMessage = "连接名称不能为空。";
            return false;
        }

        // 名称唯一性校验：与列表中除自身以外的连接比较，忽略大小写避免下拉引用歧义
        string trimmedName = Name.Trim();
        bool nameExists = _configService.Current.DataSources.Any(existing =>
            !string.Equals(existing.Name, _editingOriginalName, StringComparison.Ordinal) &&
            string.Equals(existing.Name, trimmedName, StringComparison.OrdinalIgnoreCase));
        if (nameExists)
        {
            errorMessage = $"连接名称“{trimmedName}”已存在，请更换名称。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            errorMessage = "主机地址不能为空。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Database))
        {
            errorMessage = "数据库名不能为空。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(UserId))
        {
            errorMessage = "用户名不能为空。";
            return false;
        }

        // 端口必须为 1-65535 之间的整数，非法不进入连接串组装
        if (!int.TryParse(PortText.Trim(), out int port) || port is < 1 or > 65535)
        {
            errorMessage = "端口必须为 1-65535 之间的整数。";
            return false;
        }

        // 新增场景密码必填；编辑场景留空表示保持原密文
        if (_editingOriginalName is null && string.IsNullOrWhiteSpace(PasswordInput))
        {
            errorMessage = "密码不能为空，请输入连接密码。";
            return false;
        }

        errorMessage = null;
        return true;
    }

    /// <summary>
    /// 当前连接变更时同步列表当前标记，支持主窗口下拉与删除联动场景。
    /// </summary>
    /// <param name="config">变更后的当前连接，清除时为 null。</param>
    private void OnCurrentChanged(DataSourceConfig? config)
    {
        RefreshCurrentFlags();
    }

    /// <summary>
    /// 表单任一关键字段变更后重置测试前置标记，防止保存已过期的测试结果。
    /// </summary>
    private void ResetTestState()
    {
        _isConnectionTested = false;
        if (!string.Equals(TestStatusText, UntestedStatusText, StringComparison.Ordinal))
        {
            TestStatusText = UntestedStatusText;
        }
    }

    /// <summary>
    /// 数据库类型切换时端口默认值联动：仅当端口未被用户手工定制时跟随新类型默认端口。
    /// </summary>
    /// <param name="value">切换后的数据库类型。</param>
    partial void OnSelectedTypeChanged(DataSourceType value)
    {
        if (_portFollowsType)
        {
            PortText = GetDefaultPort(value).ToString();
        }

        ResetTestState();
    }

    /// <summary>
    /// 端口文本变更时重算端口跟随标记：与当前类型默认值不一致视为用户手工定制。
    /// </summary>
    /// <param name="value">变更后的端口文本。</param>
    partial void OnPortTextChanged(string value)
    {
        _portFollowsType = string.Equals(
            value.Trim(), GetDefaultPort(SelectedType).ToString(), StringComparison.Ordinal);
        ResetTestState();
    }

    /// <summary>
    /// 连接名称变更后重置测试前置标记。
    /// </summary>
    /// <param name="value">变更后的连接名称。</param>
    partial void OnNameChanged(string value)
    {
        ResetTestState();
    }

    /// <summary>
    /// 主机地址变更后重置测试前置标记。
    /// </summary>
    /// <param name="value">变更后的主机地址。</param>
    partial void OnHostChanged(string value)
    {
        ResetTestState();
    }

    /// <summary>
    /// 数据库名变更后重置测试前置标记。
    /// </summary>
    /// <param name="value">变更后的数据库名。</param>
    partial void OnDatabaseChanged(string value)
    {
        ResetTestState();
    }

    /// <summary>
    /// 用户名变更后重置测试前置标记。
    /// </summary>
    /// <param name="value">变更后的用户名。</param>
    partial void OnUserIdChanged(string value)
    {
        ResetTestState();
    }

    /// <summary>
    /// 取指定数据库类型的默认端口。
    /// </summary>
    /// <param name="type">数据库类型。</param>
    /// <returns>MySQL 返回 3306，PostgreSQL 返回 5432。</returns>
    private static int GetDefaultPort(DataSourceType type)
    {
        return type == DataSourceType.MySql ? MySqlDefaultPort : PostgreSqlDefaultPort;
    }
}
