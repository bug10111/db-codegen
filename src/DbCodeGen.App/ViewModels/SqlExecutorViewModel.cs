using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbCodeGen.App.Services;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.DataSource;
using DbCodeGen.Core.Model;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.App.ViewModels;

/// <summary>
/// SQL 执行面板视图模型，承载面板闭环：数据源选择（默认当前连接并订阅变更联动）、SQL 编辑、
/// 危险语句确认、调用 SQL 执行服务、结果/错误展示与执行状态机。
/// 查询结果转换为 DataTable 供 DataGrid 绑定，影响行数与耗时展示于状态栏；
/// 建表成功后提示用户到主窗口刷新表清单重新走生成闭环。
/// </summary>
public sealed partial class SqlExecutorViewModel : ObservableObject
{
    private readonly IConfigService _configService;
    private readonly ICurrentDataSourceService _currentDataSourceService;
    private readonly SqlExecutor _sqlExecutor;
    private readonly IDialogService _dialogService;
    private readonly IConfirmDialogService _confirmDialogService;
    private readonly IFilePickerService _filePickerService;
    private readonly ILogger<SqlExecutorViewModel> _logger;
    private readonly Dispatcher _dispatcher;

    /// <summary>
    /// 执行在途操作的取消源，重复执行或取消/关闭时取消上次未完成的调用。
    /// </summary>
    private CancellationTokenSource? _executionCts;

    /// <summary>
    /// 全部已保存数据源连接，来自配置服务，面板内切换仅作用于本面板。
    /// </summary>
    public ObservableCollection<DataSourceConfig> DataSources { get; } = new();

    /// <summary>
    /// 当前选中的数据源连接，默认取当前连接，变更时联动执行命令可用性。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    private DataSourceConfig? _selectedDataSource;

    /// <summary>
    /// SQL 编辑区当前文本，经窗口事件桥接同步，驱动执行命令可用性与保存内容。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSqlCommand))]
    private string _sqlText = string.Empty;

    /// <summary>
    /// 执行会话生命周期状态，驱动执行/取消按钮禁用与状态展示。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSqlCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSqlCommand))]
    [NotifyPropertyChangedFor(nameof(IsExecuting))]
    private SqlExecutionState _executionState = SqlExecutionState.Idle;

    /// <summary>
    /// 面板状态栏文本，展示就绪引导、执行进度与执行结果。
    /// </summary>
    [ObservableProperty]
    private string _statusText = "就绪：选择数据源并输入 SQL 后点击执行。";

    /// <summary>
    /// 是否展示错误面板，执行失败时置为真并以红色展示脱敏错误信息。
    /// </summary>
    [ObservableProperty]
    private bool _hasError;

    /// <summary>
    /// 错误面板文本，组合错误码与脱敏错误描述，不含密码/连接串明文与被执行 SQL。
    /// </summary>
    [ObservableProperty]
    private string _errorText = string.Empty;

    /// <summary>
    /// 查询结果表格数据，查询分支成功后构建并绑定 DataGrid。
    /// </summary>
    [ObservableProperty]
    private DataTable? _resultTable;

    /// <summary>
    /// 是否展示查询结果表格，仅查询分支返回结果集时置为真。
    /// </summary>
    [ObservableProperty]
    private bool _hasQueryResult;

    /// <summary>
    /// 结果摘要文本，展示查询返回行数（含截断提示）或影响行数与执行耗时。
    /// </summary>
    [ObservableProperty]
    private string _resultSummaryText = string.Empty;

    /// <summary>
    /// 命令超时秒数，默认 30，0 表示不超时，需谨慎使用。
    /// </summary>
    [ObservableProperty]
    private int _timeoutSeconds = 30;

    /// <summary>
    /// 结果行读取上限，默认 1000，超限截断并提示。
    /// </summary>
    [ObservableProperty]
    private int _maxResultRows = 1000;

    /// <summary>
    /// 危险语句（DROP/TRUNCATE/无顶层 WHERE 的 DELETE·UPDATE）执行前是否弹确认，默认开启。
    /// </summary>
    [ObservableProperty]
    private bool _confirmDangerousSql = true;

    /// <summary>
    /// 结果表格列名到表头文本的映射，表头展示“列名（显示类型）”，列键保持纯列名保证绑定安全。
    /// </summary>
    [ObservableProperty]
    private IReadOnlyDictionary<string, string> _resultColumnHeaders = new Dictionary<string, string>();

    /// <summary>
    /// SQL 文本载入请求事件，窗口订阅后把文本写入编辑器并复位撤销栈。
    /// </summary>
    public event Action<string>? LoadSqlRequested;

    /// <summary>
    /// 是否处于执行中，驱动取消执行按钮可见与执行中禁用防重复。
    /// </summary>
    public bool IsExecuting => ExecutionState == SqlExecutionState.Executing;

    /// <summary>
    /// 使用配置服务、当前连接服务、SQL 执行服务、对话框服务、确认服务、
    /// 文件选择服务与日志器构造 SQL 执行面板视图模型。
    /// </summary>
    /// <param name="configService">配置服务，读取已保存数据源列表供面板下拉选择。</param>
    /// <param name="currentDataSourceService">当前连接共享状态服务，默认选中当前连接并接收变更通知。</param>
    /// <param name="sqlExecutor">SQL 执行服务，承载查询与执行分支的执行闭环。</param>
    /// <param name="dialogService">消息提示服务，用于引导与建表成功提示。</param>
    /// <param name="confirmDialogService">二次确认服务，用于危险语句执行前确认。</param>
    /// <param name="filePickerService">文件选择服务，用于打开/保存 .sql 文件。</param>
    /// <param name="logger">视图模型日志器，日志不输出密码、连接串明文与被执行的 SQL 文本。</param>
    /// <exception cref="ArgumentNullException">任一依赖参数为 null 时抛出。</exception>
    public SqlExecutorViewModel(
        IConfigService configService,
        ICurrentDataSourceService currentDataSourceService,
        SqlExecutor sqlExecutor,
        IDialogService dialogService,
        IConfirmDialogService confirmDialogService,
        IFilePickerService filePickerService,
        ILogger<SqlExecutorViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(currentDataSourceService);
        ArgumentNullException.ThrowIfNull(sqlExecutor);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(confirmDialogService);
        ArgumentNullException.ThrowIfNull(filePickerService);
        ArgumentNullException.ThrowIfNull(logger);

        _configService = configService;
        _currentDataSourceService = currentDataSourceService;
        _sqlExecutor = sqlExecutor;
        _dialogService = dialogService;
        _confirmDialogService = confirmDialogService;
        _filePickerService = filePickerService;
        _logger = logger;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    /// <summary>
    /// 面板呈现后初始化：加载已保存数据源列表、默认选中当前连接并订阅当前连接变更联动。
    /// </summary>
    public Task InitializeAsync()
    {
        ReloadDataSources();
        _currentDataSourceService.CurrentChanged += OnCurrentChanged;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 解除当前连接变更订阅，供窗口关闭时调用避免悬挂引用。
    /// </summary>
    public void Detach()
    {
        _currentDataSourceService.CurrentChanged -= OnCurrentChanged;
    }

    /// <summary>
    /// 取消在途执行，供窗口关闭或停止按钮调用，核心调用链经取消令牌感知取消。
    /// </summary>
    public void CancelPendingExecution()
    {
        _executionCts?.Cancel();
    }

    /// <summary>
    /// 同步编辑器文本到视图模型，窗口文本变化事件桥接入口，触发执行命令可用性重评估。
    /// </summary>
    /// <param name="text">编辑器当前 SQL 文本。</param>
    public void NotifySqlTextChanged(string text)
    {
        SqlText = text;
    }

    /// <summary>
    /// 按结果表格列键查询表头展示文本，供 DataGrid 自动生成列时替换表头。
    /// </summary>
    /// <param name="columnName">DataTable 列键（纯列名）。</param>
    /// <param name="header">表头文本，展示“列名（显示类型）”，未找到时为 null。</param>
    /// <returns>找到映射返回 true，否则返回 false。</returns>
    public bool TryGetResultColumnHeader(string columnName, out string? header)
    {
        return ResultColumnHeaders.TryGetValue(columnName, out header);
    }

    /// <summary>
    /// 触发 SQL 执行：先做危险语句确认，再经 SQL 执行服务执行并展示查询结果或影响行数；
    /// 取消贯穿执行调用链，执行中禁用执行按钮防重复提交。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExecuteSql))]
    private async Task ExecuteAsync()
    {
        DataSourceConfig? config = SelectedDataSource;
        string sql = SqlText.Trim();
        if (config is null || string.IsNullOrWhiteSpace(sql))
        {
            return;
        }

        // 危险语句执行前确认，确认弹窗仅作安全网不替代用户判断
        DangerousSqlKind kind = DangerousSqlDetector.Detect(sql);
        if (ConfirmDangerousSql && kind != DangerousSqlKind.None)
        {
            bool confirmed = await _confirmDialogService.ConfirmAsync(
                "危险语句确认",
                $"{DangerousSqlDetector.GetRiskDescription(kind)}\n\n当前语句：\n{sql}");
            if (!confirmed)
            {
                StatusText = "已取消执行危险语句。";
                return;
            }
        }

        // 新执行前取消在途执行并重建取消源，保证取消令牌归属本次执行
        _executionCts?.Cancel();
        _executionCts?.Dispose();
        _executionCts = new CancellationTokenSource();
        CancellationToken ct = _executionCts.Token;

        // 组装执行参数，超时与行上限做防御性钳制防止越界
        var options = new SqlExecutionOptions
        {
            TimeoutSeconds = Math.Max(0, TimeoutSeconds),
            MaxResultRows = Math.Max(1, MaxResultRows),
            ConfirmDangerousSql = ConfirmDangerousSql
        };

        ResetResultArea();
        ExecutionState = SqlExecutionState.Executing;
        StatusText = "正在执行 SQL…";

        try
        {
            SqlExecutionResult result = await _sqlExecutor.ExecuteAsync(config, sql, options, ct);
            bool isCancelled = ct.IsCancellationRequested;
            RunOnUiThread(() => ApplyResult(result, config, sql, isCancelled));
        }
        catch (Exception exception)
        {
            // SQL 执行服务已将异常收敛为失败结果，此处仅兜底未预期异常
            _logger.LogError(exception, "SQL 执行发生未预期异常，连接名 {ConnectionName}。", config.Name);
            RunOnUiThread(() =>
            {
                ExecutionState = SqlExecutionState.Error;
                HasError = true;
                ErrorText = exception.Message;
                StatusText = "执行失败：发生未预期异常。";
            });
        }
        finally
        {
            // 状态复位到就绪，恢复执行按钮可用性，用户可继续下一次执行
            RunOnUiThread(() => ExecutionState = SqlExecutionState.Idle);
        }
    }

    /// <summary>
    /// 判定执行命令是否可执行：未在执行中、已选择数据源且 SQL 非空。
    /// </summary>
    private bool CanExecuteSql()
    {
        return !IsExecuting
            && SelectedDataSource is not null
            && !string.IsNullOrWhiteSpace(SqlText);
    }

    /// <summary>
    /// 停止当前执行：取消在途取消源，核心调用链感知取消后按取消分支复位状态。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _executionCts?.Cancel();
        StatusText = "正在取消执行…";
    }

    /// <summary>
    /// 判定取消命令是否可执行：仅执行进行中时可取消。
    /// </summary>
    private bool CanCancel() => IsExecuting;

    /// <summary>
    /// 打开 SQL 脚本文件并载入编辑器，供复用已保存的建表/查询脚本。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenSql))]
    private async Task OpenSqlAsync()
    {
        string? path = await _filePickerService.PickOpenSqlAsync();
        if (path is null)
        {
            return;
        }

        try
        {
            // 读取文件文本后先同步到视图模型，再请求窗口载入编辑器（载入期间抑制置脏事件）
            string text = await File.ReadAllTextAsync(path);
            SqlText = text;
            LoadSqlRequested?.Invoke(text);
            StatusText = $"已打开：{path}";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "打开 SQL 文件失败，路径 {Path}。", path);
            _dialogService.ShowError($"打开 SQL 文件失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 判定打开 SQL 命令是否可执行：未在执行中时可打开文件。
    /// </summary>
    private bool CanOpenSql() => !IsExecuting;

    /// <summary>
    /// 保存当前 SQL 文本到 .sql 文件，供复用或归档本次编辑的语句。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveSql))]
    private async Task SaveSqlAsync()
    {
        if (string.IsNullOrWhiteSpace(SqlText))
        {
            return;
        }

        string? path = await _filePickerService.PickSaveSqlAsync("query.sql");
        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, SqlText);
            StatusText = $"已保存：{path}";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "保存 SQL 文件失败，路径 {Path}。", path);
            _dialogService.ShowError($"保存 SQL 文件失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 判定保存 SQL 命令是否可执行：未在执行中且 SQL 文本非空时可保存。
    /// </summary>
    private bool CanSaveSql() => !IsExecuting && !string.IsNullOrWhiteSpace(SqlText);

    /// <summary>
    /// 应用执行结果到面板：取消分支、失败分支、查询分支与执行分支分别复位状态并展示。
    /// </summary>
    /// <param name="result">SQL 执行服务返回的结果。</param>
    /// <param name="config">本次执行的数据源连接配置。</param>
    /// <param name="sql">本次执行的 SQL 文本，用于建表成功判定。</param>
    /// <param name="isCancelled">本次执行是否因取消令牌被取消。</param>
    private void ApplyResult(SqlExecutionResult result, DataSourceConfig config, string sql, bool isCancelled)
    {
        if (isCancelled)
        {
            ExecutionState = SqlExecutionState.Cancelled;
            StatusText = "执行已取消。";
            return;
        }

        if (!result.Success)
        {
            ExecutionState = SqlExecutionState.Error;
            HasError = true;
            ErrorText = BuildErrorText(result);
            StatusText = "执行失败，请查看下方错误信息。";
            _logger.LogWarning("SQL 执行失败，连接名 {ConnectionName}，错误码 {ErrorCode}。", config.Name, result.ErrorCode);
            return;
        }

        ExecutionState = SqlExecutionState.Success;

        // 查询分支：按列定义构建结果表格，展示行数与截断提示
        if (result.Columns.Count > 0)
        {
            ResultTable = BuildResultTable(result);
            HasQueryResult = true;
            string truncatedHint = result.Truncated ? "（已截断，仅展示前部分行）" : string.Empty;
            string summary = $"查询返回 {result.Rows.Count} 行{truncatedHint} · 耗时 {result.Duration.TotalMilliseconds:F0} 毫秒";
            ResultSummaryText = summary;
            StatusText = $"查询执行成功 · 耗时 {result.Duration.TotalMilliseconds:F0} 毫秒";
            return;
        }

        // 执行分支：仅受影响行数大于等于 0 时展示影响行数，DDL 展示执行成功
        string affectedText = result.AffectedRows is >= 0
            ? $"影响 {result.AffectedRows} 行"
            : "执行成功";
        string durationText = $" · 耗时 {result.Duration.TotalMilliseconds:F0} 毫秒";
        ResultSummaryText = affectedText + durationText;
        StatusText = affectedText + durationText;

        // 建表成功提示：CREATE TABLE 开头的成功语句引导用户回主窗口刷新表清单
        if (IsCreateTableStatement(sql))
        {
            _dialogService.ShowInfo(
                "建表成功。可在主窗口点击“刷新表”刷新表清单后，新表将进入“表浏览与选择 → 批量代码生成”闭环。",
                "建表成功");
        }
    }

    /// <summary>
    /// 依据查询结果构建 DataTable：以纯列名建列保证绑定路径安全，显示类型经表头映射展示。
    /// </summary>
    /// <param name="result">包含列定义与结果行的查询执行结果。</param>
    /// <returns>可直接绑定 DataGrid 的 DataTable。</returns>
    private DataTable BuildResultTable(SqlExecutionResult result)
    {
        DataTable table = new();
        Dictionary<string, string> headers = new(StringComparer.Ordinal);

        // 逐列建表，重名列追加序号保证列键唯一
        foreach (SqlColumnInfo column in result.Columns)
        {
            string columnName = MakeUniqueColumnName(table, column.Name);
            table.Columns.Add(columnName);
            string displayType = string.IsNullOrWhiteSpace(column.DisplayType)
                ? column.Name
                : $"{column.Name} ({column.DisplayType})";
            headers[columnName] = displayType;
        }

        // 结果行逐行写入 DataTable，null 单元格转为 DBNull 满足行约束
        foreach (IReadOnlyList<object?> row in result.Rows)
        {
            DataRow dataRow = table.NewRow();
            int cellCount = Math.Min(table.Columns.Count, row.Count);
            for (int index = 0; index < cellCount; index++)
            {
                dataRow[index] = row[index] ?? DBNull.Value;
            }

            table.Rows.Add(dataRow);
        }

        ResultColumnHeaders = headers;
        return table;
    }

    /// <summary>
    /// 为 DataTable 生成唯一列名，重名时以数字后缀追加避免列键冲突。
    /// </summary>
    /// <param name="table">目标 DataTable。</param>
    /// <param name="baseName">列名基名。</param>
    /// <returns>在目标表中不重复的列名。</returns>
    private static string MakeUniqueColumnName(DataTable table, string baseName)
    {
        string candidate = baseName;
        int suffix = 2;
        while (table.Columns.Contains(candidate))
        {
            candidate = $"{baseName}_{suffix}";
            suffix++;
        }

        return candidate;
    }

    /// <summary>
    /// 组合错误码与脱敏错误描述为错误面板文本，供红色区域展示。
    /// </summary>
    /// <param name="result">失败执行结果。</param>
    /// <returns>错误面板展示文本。</returns>
    private static string BuildErrorText(SqlExecutionResult result)
    {
        if (string.IsNullOrWhiteSpace(result.ErrorCode))
        {
            return result.ErrorMessage ?? "执行失败。";
        }

        return $"[{result.ErrorCode}] {result.ErrorMessage}";
    }

    /// <summary>
    /// 判断 SQL 是否为 CREATE TABLE 语句（含 CREATE TEMPORARY TABLE），用于建表成功提示。
    /// </summary>
    /// <param name="sql">待判断的 SQL 文本。</param>
    /// <returns>建表语句返回 true，否则返回 false。</returns>
    private static bool IsCreateTableStatement(string sql)
    {
        // 按空白切分取语句首词，识别 CREATE 后跟 TABLE 或 TEMPORARY TABLE
        string[] tokens = sql.TrimStart().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return false;
        }

        bool isCreate = string.Equals(tokens[0], "CREATE", StringComparison.OrdinalIgnoreCase);
        if (!isCreate)
        {
            return false;
        }

        return string.Equals(tokens[1], "TABLE", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(tokens[1], "TEMPORARY", StringComparison.OrdinalIgnoreCase)
                && tokens.Length >= 3
                && string.Equals(tokens[2], "TABLE", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 依据配置快照重建数据源下拉列表；默认选中当前连接，当前连接未设置但列表有数据源时默认选第一项。
    /// </summary>
    private void ReloadDataSources()
    {
        AppConfig config = _configService.Current;
        DataSources.Clear();
        foreach (DataSourceConfig source in config.DataSources)
        {
            DataSources.Add(source);
        }

        // 默认选中当前连接；当前连接未设置且列表有数据源时默认选第一项，让面板打开即具备可执行数据源
        SelectedDataSource = FindDataSource(_currentDataSourceService.Current)
            ?? (DataSources.Count > 0 ? DataSources[0] : null);
    }

    /// <summary>
    /// 当前连接变更联动：按连接名称重新定位下拉选中项，清除当前连接时回到无可执行数据源。
    /// </summary>
    /// <param name="config">变更后的当前连接，清除时为 null。</param>
    private void OnCurrentChanged(DataSourceConfig? config)
    {
        SelectedDataSource = FindDataSource(config);
    }

    /// <summary>
    /// 在下拉数据源集合中按连接名称定位匹配项，未找到返回 null。
    /// </summary>
    /// <param name="config">要定位的连接配置，可为 null。</param>
    /// <returns>与目标连接名称一致的已保存连接，不存在时返回 null。</returns>
    private DataSourceConfig? FindDataSource(DataSourceConfig? config)
    {
        if (config is null)
        {
            return null;
        }

        return DataSources.FirstOrDefault(item => string.Equals(item.Name, config.Name, StringComparison.Ordinal));
    }

    /// <summary>
    /// 复位结果展示区域：清空表格、错误与摘要，为下一次执行做准备。
    /// </summary>
    private void ResetResultArea()
    {
        ResultTable = null;
        HasQueryResult = false;
        ResultSummaryText = string.Empty;
        HasError = false;
        ErrorText = string.Empty;
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
}
