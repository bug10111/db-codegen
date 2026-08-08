using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbCodeGen.App.Services;
using DbCodeGen.Core.DataSource;
using DbCodeGen.Core.Model;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.App.ViewModels;

/// <summary>
/// 主窗口①表列表区视图模型，承载表清单加载、搜索过滤、多选勾选与当前表详情惰性读取。
/// 连接建立复用 01 连接能力（经 TableCatalogService 编排），勾选集合与当前表供批量生成/预览/AI 消费。
/// 批量选择命令作用于全部已加载表，搜索过滤仅影响可见行，两者互不干扰。
/// </summary>
public sealed partial class TableListViewModel : ObservableObject
{
    private readonly ICurrentDataSourceService _currentDataSourceService;
    private readonly TableCatalogService _tableCatalogService;
    private readonly IDialogService _dialogService;
    private readonly ILogger<TableListViewModel> _logger;
    private readonly Dispatcher _dispatcher;

    /// <summary>
    /// 全部已加载表行的内部存储，批量勾选操作作用于该集合而非过滤后的可见行。
    /// </summary>
    private readonly List<TableRowViewModel> _allRows = new();

    /// <summary>
    /// 表清单的过滤排序视图，绑定 DataGrid，过滤仅影响可见行不改变勾选集合。
    /// </summary>
    private readonly ListCollectionView _tableView;

    /// <summary>
    /// 刷新表清单的取消源，连接切换或重复刷新时取消在途读取。
    /// </summary>
    private CancellationTokenSource? _refreshCts;

    /// <summary>
    /// 当前表详情读取的取消源，选中行快速切换时取消上次未完成的读取。
    /// </summary>
    private CancellationTokenSource? _detailCts;

    /// <summary>
    /// 是否正在刷新表清单，期间禁用刷新按钮防重复提交。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isRefreshing;

    /// <summary>
    /// 搜索过滤文本，输入变化时实时刷新过滤视图。
    /// </summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    /// <summary>
    /// 当前连接配置快照，来自当前连接共享状态服务。
    /// </summary>
    [ObservableProperty]
    private DataSourceConfig? _currentConnection;

    /// <summary>
    /// 连接生命周期状态，驱动状态栏文本与刷新重试语义。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private ConnectionState _connectionState = ConnectionState.Disconnected;

    /// <summary>
    /// 当前已勾选表数量，状态栏展示并随勾选变化实时刷新。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int _selectedCount;

    /// <summary>
    /// 当前选中的表行，触发当前表详情惰性读取。
    /// </summary>
    [ObservableProperty]
    private TableRowViewModel? _selectedRow;

    /// <summary>
    /// 当前表详情，选中行后惰性读取完整列元数据，供预览与 AI 样例消费。
    /// </summary>
    [ObservableProperty]
    private TableInfo? _currentTable;

    /// <summary>
    /// 表清单行集合，DataGrid 经排序过滤视图绑定。
    /// </summary>
    public ObservableCollection<TableRowViewModel> TableRows { get; } = new();

    /// <summary>
    /// 表清单的排序过滤视图，DataGrid 绑定源，搜索过滤仅影响可见行。
    /// </summary>
    public ICollectionView TableView => _tableView;

    /// <summary>
    /// 勾选表集合的实时快照，供批量生成消费；作用于全部已加载表，不受搜索过滤影响。
    /// </summary>
    public IReadOnlyList<TableInfo> SelectedTables
    {
        get
        {
            // 勾选集合实时快照：先筛出勾选行并提取表元数据，再整体拷贝为新数组，下游消费不随集合变化
            IEnumerable<TableInfo> selectedInfos = _allRows.Where(row => row.IsSelected).Select(row => row.Table);
            return selectedInfos.ToArray();
        }
    }

    /// <summary>
    /// 状态栏文本，按连接状态与表数量、勾选数量组合展示。
    /// </summary>
    public string StatusText
    {
        get
        {
            return ConnectionState switch
            {
                ConnectionState.Connecting => "连接中：正在读取表清单…",
                ConnectionState.Connected => $"已连接：共 {TableRows.Count} 张表，已勾选 {SelectedCount} 张",
                ConnectionState.Failed => "连接失败：读取表清单出错，请点击刷新表重试",
                _ => "未连接：请选择数据源后点击刷新表"
            };
        }
    }

    /// <summary>
    /// 以当前连接服务、表元数据服务、对话框服务与日志器构造表列表区视图模型，
    /// 并初始化排序过滤视图与订阅当前连接变更。
    /// </summary>
    /// <param name="currentDataSourceService">当前连接共享状态服务，读取当前连接并接收变更通知。</param>
    /// <param name="tableCatalogService">表元数据编排服务，读取表清单与表详情。</param>
    /// <param name="dialogService">消息提示服务，用于断连与读取失败反馈。</param>
    /// <param name="logger">视图模型日志器，日志不输出密码与连接串。</param>
    /// <exception cref="ArgumentNullException">任一依赖参数为 null 时抛出。</exception>
    public TableListViewModel(
        ICurrentDataSourceService currentDataSourceService,
        TableCatalogService tableCatalogService,
        IDialogService dialogService,
        ILogger<TableListViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(currentDataSourceService);
        ArgumentNullException.ThrowIfNull(tableCatalogService);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(logger);

        _currentDataSourceService = currentDataSourceService;
        _tableCatalogService = tableCatalogService;
        _dialogService = dialogService;
        _logger = logger;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        // 过滤排序视图：默认按表名升序，过滤谓词随搜索文本实时生效
        _tableView = new ListCollectionView(TableRows)
        {
            Filter = FilterTableRow
        };
        _tableView.SortDescriptions.Add(
            new SortDescription(nameof(TableRowViewModel.RawName), ListSortDirection.Ascending));

        CurrentConnection = _currentDataSourceService.Current;
        _currentDataSourceService.CurrentChanged += OnCurrentChanged;
    }

    /// <summary>
    /// 解除当前连接变更订阅，供主窗口关闭时调用避免悬挂引用。
    /// </summary>
    public void Detach()
    {
        _currentDataSourceService.CurrentChanged -= OnCurrentChanged;
    }

    /// <summary>
    /// 刷新表清单：复用 01 连接能力读取表清单，按表名合并保留原勾选并失效详情缓存。
    /// 连接失败时回退连接状态并提示可重试，刷新期间禁用按钮防重复提交。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        DataSourceConfig? config = _currentDataSourceService.Current;
        if (config is null)
        {
            // 未选择当前连接时引导用户先选数据源，不进入连接流程
            _dialogService.ShowInfo("请先在工具栏选择数据源后再刷新表。");
            return;
        }

        CancellationTokenSource refreshCts = new();
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = refreshCts;
        CancellationToken ct = refreshCts.Token;

        IsRefreshing = true;
        ConnectionState = ConnectionState.Connecting;
        try
        {
            IReadOnlyList<TableInfo> tables = await _tableCatalogService.GetTablesAsync(config, ct);
            RunOnUiThread(() =>
            {
                MergeRows(tables);
                // 表结构可能已变化，刷新后失效详情缓存防陈旧列元数据
                _tableCatalogService.ClearCache();
                ConnectionState = ConnectionState.Connected;
            });
            _logger.LogInformation(
                "表清单刷新完成，连接名 {ConnectionName}，表数量 {TableCount}。", config.Name, tables.Count);
        }
        catch (OperationCanceledException)
        {
            // 连接切换或重复刷新取消在途读取，连接状态由后续触发方接管
        }
        catch (Exception exception)
        {
            ConnectionState = ConnectionState.Failed;
            _logger.LogError(exception, "刷新表清单失败，连接名 {ConnectionName}。", config.Name);
            RunOnUiThread(() => _dialogService.ShowError($"刷新表清单失败：{exception.Message}，可点击刷新表重试。"));
        }
        finally
        {
            // 仅当本次刷新仍是最近一次刷新时复位刷新中状态，防连接切换超车的旧刷新误启用刷新按钮
            if (ReferenceEquals(_refreshCts, refreshCts))
            {
                IsRefreshing = false;
            }
        }
    }

    /// <summary>
    /// 判定刷新命令是否可执行：仅当未处于刷新中时可触发，防止重复提交。
    /// </summary>
    private bool CanRefresh() => !IsRefreshing;

    /// <summary>
    /// 全选：将全部已加载表置为勾选，不受搜索过滤影响。
    /// </summary>
    [RelayCommand]
    private void SelectAll()
    {
        // 全选作用于全部已加载表，勾选集合语义确定且不随过滤可见行变化
        foreach (TableRowViewModel row in _allRows)
        {
            row.IsSelected = true;
        }
    }

    /// <summary>
    /// 反选：对全部已加载表做双向批量取反，已勾选取消、未勾选选中。
    /// </summary>
    [RelayCommand]
    private void InvertSelection()
    {
        // 反选为视图级批量取反，作用于全部已加载表，不受搜索过滤影响
        foreach (TableRowViewModel row in _allRows)
        {
            row.IsSelected = !row.IsSelected;
        }
    }

    /// <summary>
    /// 清空：将全部已加载表的勾选置为未勾选，不受搜索过滤影响。
    /// </summary>
    [RelayCommand]
    private void ClearSelection()
    {
        // 清空作用于全部已加载表，勾选集合回到空集
        foreach (TableRowViewModel row in _allRows)
        {
            row.IsSelected = false;
        }
    }

    /// <summary>
    /// 当前连接变更联动：清除当前连接时清空表区与详情缓存回到未连接；
    /// 切换新连接时清空旧连接的表清单并自动刷新。
    /// </summary>
    /// <param name="config">变更后的当前连接，清除时为 null。</param>
    private void OnCurrentChanged(DataSourceConfig? config)
    {
        _refreshCts?.Cancel();
        _detailCts?.Cancel();

        if (config is null)
        {
            CurrentConnection = null;
            ConnectionState = ConnectionState.Disconnected;
            ClearTableArea();
            _tableCatalogService.ClearCache();
            return;
        }

        CurrentConnection = config;
        ConnectionState = ConnectionState.Disconnected;
        ClearTableArea();
        // 切换到新连接后失效旧连接的表详情缓存，防陈旧列元数据跨连接串用
        _tableCatalogService.ClearCache();

        // 选择新数据源后自动刷新表清单，形成选择数据源即进入连接闭环
        _ = RefreshAsync();
    }

    /// <summary>
    /// 选中行变更后触发当前表详情惰性读取，读取前先清空旧详情。
    /// </summary>
    /// <param name="value">变更后的选中表行，取消选中时为 null。</param>
    partial void OnSelectedRowChanged(TableRowViewModel? value)
    {
        // 选中行变化先清空当前表详情，随后异步惰性读取完整列元数据
        CurrentTable = null;
        if (value is null)
        {
            return;
        }

        _ = LoadCurrentTableDetailAsync(value);
    }

    /// <summary>
    /// 搜索文本变更后实时刷新过滤视图，过滤仅影响可见行不改变勾选集合。
    /// </summary>
    /// <param name="value">变更后的搜索文本。</param>
    partial void OnFilterTextChanged(string value)
    {
        _tableView.Refresh();
    }

    /// <summary>
    /// 惰性读取选中表的完整列元数据：缓存命中直接返回，未命中读取后缓存；
    /// 读取期间选中行已切换则丢弃本次结果，避免过期元数据覆盖新选中表。
    /// </summary>
    /// <param name="row">当前选中的表行。</param>
    private async Task LoadCurrentTableDetailAsync(TableRowViewModel row)
    {
        DataSourceConfig? config = _currentDataSourceService.Current;
        if (config is null)
        {
            return;
        }

        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = new CancellationTokenSource();
        CancellationToken ct = _detailCts.Token;

        string targetName = row.RawName;
        try
        {
            TableInfo detail = await _tableCatalogService.GetTableDetailAsync(config, targetName, ct);
            RunOnUiThread(() =>
            {
                // 仅当选中行仍为目标表时写入当前表详情，防过期结果覆盖新选中表
                if (SelectedRow is not null && string.Equals(SelectedRow.RawName, targetName, StringComparison.Ordinal))
                {
                    CurrentTable = detail;
                }
            });
        }
        catch (OperationCanceledException)
        {
            // 选中行快速切换或连接切换取消上次读取，不提示用户
        }
        catch (Exception exception)
        {
            // 详情读取失败仅记日志并保持当前表为空，预览区据此展示空状态
            _logger.LogError(
                exception, "读取表详情失败，连接名 {ConnectionName}，表名 {TableName}。", config.Name, targetName);
        }
    }

    /// <summary>
    /// 将新读到的表清单合并进表区：按表名匹配保留原勾选集合，随后刷新过滤视图与状态文本。
    /// </summary>
    /// <param name="tables">新读取的表清单。</param>
    private void MergeRows(IReadOnlyList<TableInfo> tables)
    {
        // 收集刷新前已勾选表名，供按名合并保留勾选，刷新后同名表延续参与生成
        HashSet<string> previousSelection = new(
            _allRows.Where(row => row.IsSelected).Select(row => row.RawName),
            StringComparer.OrdinalIgnoreCase);

        _allRows.Clear();
        TableRows.Clear();
        SelectedCount = 0;
        // 刷新后行实例全部重建，显式复位当前选中行并联动清空当前表详情，防陈旧选中引用脱离新集合
        SelectedRow = null;

        foreach (TableInfo table in tables)
        {
            TableRowViewModel row = new(table, OnRowIsSelectedChanged);
            row.IsSelected = previousSelection.Contains(table.RawName);
            _allRows.Add(row);
            TableRows.Add(row);
        }

        _tableView.Refresh();
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>
    /// 单行或批量勾选变化后维护勾选数量增量计数。
    /// </summary>
    /// <param name="row">勾选状态发生变化的表行。</param>
    private void OnRowIsSelectedChanged(TableRowViewModel row)
    {
        // 以行勾选后的新状态做增量计数，避免每次全量统计影响批量操作性能
        if (row.IsSelected)
        {
            SelectedCount++;
        }
        else
        {
            SelectedCount--;
        }
    }

    /// <summary>
    /// 过滤谓词：按表名/注释包含搜索关键字（忽略大小写）决定行是否可见，关键字为空时全部可见。
    /// </summary>
    /// <param name="item">待判断的表行对象。</param>
    /// <returns>满足过滤条件的表行可见。</returns>
    private bool FilterTableRow(object item)
    {
        if (item is not TableRowViewModel row)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(FilterText))
        {
            return true;
        }

        return row.RawName.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
            || (row.Comment is not null && row.Comment.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 清空表区全部数据：行集合、过滤视图、当前选中与当前表详情。
    /// </summary>
    private void ClearTableArea()
    {
        SelectedRow = null;
        CurrentTable = null;
        _allRows.Clear();
        TableRows.Clear();
        SelectedCount = 0;
        _tableView.Refresh();
        OnPropertyChanged(nameof(StatusText));
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
