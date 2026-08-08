using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DbCodeGen.App.Services;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.DataSource;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.App.ViewModels;

/// <summary>
/// 预览区视图模型，承载③预览区的预览表选择与实时渲染。
/// 模板文本变化或当前表变化经 300ms 防抖后走共享渲染管线 TemplateEngine.Render，
/// 渲染结果回填预览区，语法错误结构化定位到编辑器行列；预览表与变量面板共用同一当前表。
/// </summary>
public sealed partial class PreviewViewModel : ObservableObject
{
    /// <summary>
    /// 预览防抖间隔，模板连续编辑期间不触发渲染，空闲 300ms 后集中渲染一次。
    /// </summary>
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(300);

    private readonly TemplateEngine _templateEngine;
    private readonly IConfigService _configService;
    private readonly TableListViewModel _tableListViewModel;
    private readonly TemplateViewModel _templateViewModel;
    private readonly TableCatalogService _tableCatalogService;
    private readonly ICurrentDataSourceService _currentDataSourceService;
    private readonly IDialogService _dialogService;
    private readonly ILogger<PreviewViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _debounceTimer;

    /// <summary>
    /// 在途渲染取消源，模板或当前表变化时取消上次未完成的渲染。
    /// </summary>
    private CancellationTokenSource? _renderCts;

    /// <summary>
    /// 渲染版本号，过期渲染结果据此丢弃，保证展示的是最新一次渲染。
    /// </summary>
    private int _renderVersion;

    /// <summary>
    /// 预览表详情读取的取消源，下拉快速切换时取消上次未完成的读取。
    /// </summary>
    private CancellationTokenSource? _detailCts;

    /// <summary>
    /// 程序性同步预览表下拉期间抑制再次触发切换加载，防止与用户操作互相干扰。
    /// </summary>
    private bool _isSyncingSelection;

    /// <summary>
    /// 当前预览表，驱动渲染上下文并同步变量面板。
    /// </summary>
    [ObservableProperty]
    private TableInfo? _currentTable;

    /// <summary>
    /// 预览表下拉选中项，用户切换时惰性读取该表完整列元数据。
    /// </summary>
    [ObservableProperty]
    private TableRowViewModel? _selectedTableRow;

    /// <summary>
    /// 渲染结果文本，视图层订阅变化后回填预览编辑器。
    /// </summary>
    [ObservableProperty]
    private string _previewText = string.Empty;

    /// <summary>
    /// 预览状态栏文本，展示渲染成功耗时或结构化错误。
    /// </summary>
    [ObservableProperty]
    private string _statusText = "未选择表：请先在①表列表区选择表，或在下拉中选择预览表。";

    /// <summary>
    /// 是否正在渲染，驱动预览区渲染中状态。
    /// </summary>
    [ObservableProperty]
    private bool _isRendering;

    /// <summary>
    /// 预览表下拉数据源，与①区表清单同源，保证预览表来自已加载表。
    /// </summary>
    public ObservableCollection<TableRowViewModel> AvailableTables => _tableListViewModel.TableRows;

    /// <summary>
    /// 渲染结果文本变化事件，视图层订阅后更新预览编辑器内容。
    /// </summary>
    public event Action<string>? PreviewTextChanged;

    /// <summary>
    /// 渲染错误定位事件，携带 1 基行列，视图层订阅后跳转模板编辑器对应位置。
    /// </summary>
    public event Action<int, int>? NavigateToEditor;

    /// <summary>
    /// 当前表变更事件，变量面板订阅后重建变量表达式树。
    /// </summary>
    public event Action<TableInfo?>? CurrentTableChanged;

    /// <summary>
    /// 使用共享渲染引擎、表列表视图模型、模板视图模型、表元数据服务、当前连接服务、对话框服务与日志器构造预览视图模型，
    /// 并初始化防抖定时器与订阅编辑器文本、当前表变更。
    /// </summary>
    /// <param name="templateEngine">共享渲染引擎，内容渲染统一入口。</param>
    /// <param name="configService">配置持久化服务，订阅配置保存事件以在类型映射变化后刷新预览。</param>
    /// <param name="tableListViewModel">①区表列表视图模型，提供当前表与表清单。</param>
    /// <param name="templateViewModel">模板编辑器视图模型，提供编辑文本与当前包上下文。</param>
    /// <param name="tableCatalogService">表元数据服务，预览表切换时惰性读取列详情。</param>
    /// <param name="currentDataSourceService">当前连接服务，预览表详情读取依赖当前连接。</param>
    /// <param name="dialogService">消息提示服务，用于预览表详情读取失败反馈。</param>
    /// <param name="logger">视图模型日志器，日志不记录模板正文与敏感信息。</param>
    /// <exception cref="ArgumentNullException">任一依赖参数为 null 时抛出。</exception>
    public PreviewViewModel(
        TemplateEngine templateEngine,
        IConfigService configService,
        TableListViewModel tableListViewModel,
        TemplateViewModel templateViewModel,
        TableCatalogService tableCatalogService,
        ICurrentDataSourceService currentDataSourceService,
        IDialogService dialogService,
        ILogger<PreviewViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(templateEngine);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(tableListViewModel);
        ArgumentNullException.ThrowIfNull(templateViewModel);
        ArgumentNullException.ThrowIfNull(tableCatalogService);
        ArgumentNullException.ThrowIfNull(currentDataSourceService);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(logger);

        _templateEngine = templateEngine;
        _configService = configService;
        _tableListViewModel = tableListViewModel;
        _templateViewModel = templateViewModel;
        _tableCatalogService = tableCatalogService;
        _currentDataSourceService = currentDataSourceService;
        _dialogService = dialogService;
        _logger = logger;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _debounceTimer = new DispatcherTimer { Interval = DebounceInterval };
        _debounceTimer.Tick += OnDebounceTick;

        _tableListViewModel.PropertyChanged += OnTableListPropertyChanged;
        _templateViewModel.EditorContentChanged += OnTemplateContentChanged;

        // 类型映射等配置保存后重新渲染预览，保证映射改动即时反映到预览代码
        _configService.ConfigChanged += OnConfigChanged;

        // 订阅后立即同步当前表，预览区打开即与①区选中表对齐
        SyncCurrentTable(_tableListViewModel.CurrentTable);
    }

    /// <summary>
    /// 解除表列表与模板视图模型订阅，停止防抖定时器并取消在途渲染与详情读取，供主窗口关闭时调用避免悬挂引用。
    /// </summary>
    public void Detach()
    {
        _tableListViewModel.PropertyChanged -= OnTableListPropertyChanged;
        _templateViewModel.EditorContentChanged -= OnTemplateContentChanged;
        _configService.ConfigChanged -= OnConfigChanged;
        _debounceTimer.Stop();
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _detailCts?.Cancel();
        _detailCts?.Dispose();
    }

    /// <summary>
    /// ①区当前表变化时同步预览当前表，并联动变量面板与预览渲染。
    /// </summary>
    /// <param name="sender">事件发送方。</param>
    /// <param name="e">属性变化参数。</param>
    private void OnTableListPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TableListViewModel.CurrentTable))
        {
            SyncCurrentTable(_tableListViewModel.CurrentTable);
        }
    }

    /// <summary>
    /// 配置保存后重新触发预览渲染，保证类型映射等配置改动即时反映到预览代码。
    /// 配置可能在非 UI 线程保存，统一切换到 UI 线程后经防抖渲染。
    /// </summary>
    private void OnConfigChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.CheckAccess())
        {
            ScheduleRender();
        }
        else
        {
            _dispatcher.InvokeAsync(ScheduleRender);
        }
    }

    /// <summary>
    /// 模板编辑器文本变化时重置防抖定时器，文本为空时立即清空预览。
    /// </summary>
    /// <param name="_">编辑器当前文本，仅作变化信号。</param>
    private void OnTemplateContentChanged(string _)
    {
        // 编辑器文本为空时无需后台渲染，直接清空预览展示空态
        if (string.IsNullOrEmpty(_templateViewModel.EditorText))
        {
            _debounceTimer.Stop();
            UpdatePreviewEmpty("模板为空：请在②模板区加载模板文件。");
            return;
        }

        ScheduleRender();
    }

    /// <summary>
    /// 预览表下拉选中项变化：程序性同步时跳过，用户主动选择时惰性读取该表完整列元数据。
    /// </summary>
    /// <param name="value">变更后的预览表选中项。</param>
    partial void OnSelectedTableRowChanged(TableRowViewModel? value)
    {
        if (_isSyncingSelection)
        {
            return;
        }

        if (value is null)
        {
            return;
        }

        _ = LoadTableDetailForPreviewAsync(value);
    }

    /// <summary>
    /// 惰性读取预览表完整列元数据：命中缓存直接返回，未命中读取后写入缓存；
    /// 读取成功后同步为当前表并触发变量面板与渲染刷新。
    /// </summary>
    /// <param name="row">预览区选中的表行。</param>
    private async Task LoadTableDetailForPreviewAsync(TableRowViewModel row)
    {
        DataSourceConfig? config = _currentDataSourceService.Current;
        if (config is null)
        {
            _dialogService.ShowInfo("未连接数据源，无法读取表详情。请先在工具栏选择数据源并刷新表。");
            return;
        }

        string targetName = row.RawName;
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = new CancellationTokenSource();
        CancellationToken ct = _detailCts.Token;

        try
        {
            TableInfo detail = await _tableCatalogService.GetTableDetailAsync(config, targetName, ct);

            // 仅当选中行仍为目标表时同步当前表，防快速切换后过期详情覆盖新选中表
            if (SelectedTableRow is null || !string.Equals(SelectedTableRow.RawName, targetName, StringComparison.Ordinal))
            {
                return;
            }

            SyncCurrentTable(detail);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "读取预览表详情失败，连接名 {ConnectionName}，表名 {TableName}。", config.Name, targetName);
            _dialogService.ShowError($"读取表详情失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 同步当前表：更新预览当前表、同步下拉选中项并通知变量面板与防抖渲染。
    /// </summary>
    /// <param name="table">变更后的当前表，未选择时为 null。</param>
    private void SyncCurrentTable(TableInfo? table)
    {
        CurrentTable = table;
        CurrentTableChanged?.Invoke(table);

        SyncSelectedRow(table);
        ScheduleRender();
    }

    /// <summary>
    /// 按当前表名定位并同步预览表下拉选中项，期间抑制触发详情加载。
    /// </summary>
    /// <param name="table">当前表，未选择时为 null。</param>
    private void SyncSelectedRow(TableInfo? table)
    {
        _isSyncingSelection = true;
        try
        {
            if (table is null)
            {
                SelectedTableRow = null;
                return;
            }

            SelectedTableRow = AvailableTables.FirstOrDefault(row =>
                string.Equals(row.RawName, table.RawName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    /// <summary>
    /// 重置防抖定时器：停止后重新启动，模板或当前表连续变化期间只保留最后一次渲染。
    /// </summary>
    private void ScheduleRender()
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    /// <summary>
    /// 防抖定时器触发后执行渲染。
    /// </summary>
    private async void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        await RenderAsync();
    }

    /// <summary>
    /// 执行实时渲染：组装渲染上下文后经共享渲染引擎在后台任务渲染，结果回 UI 线程展示；
    /// 渲染失败时展示结构化错误并定位编辑器行列，过期渲染结果直接丢弃。
    /// </summary>
    private async Task RenderAsync()
    {
        TableInfo? table = CurrentTable;
        TemplatePackageInfo? package = _templateViewModel.CurrentPackage;
        string templateText = _templateViewModel.EditorText;

        if (table is null)
        {
            UpdatePreviewEmpty("未选择表：请先在①表列表区选择表，或在下拉中选择预览表。");
            return;
        }

        if (package is null || string.IsNullOrEmpty(templateText))
        {
            UpdatePreviewEmpty("模板为空：请先在②模板区加载模板文件。");
            return;
        }

        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = new CancellationTokenSource();
        CancellationToken ct = _renderCts.Token;
        int version = ++_renderVersion;

        IsRendering = true;
        try
        {
            TemplatePackageContext packageContext = TemplatePackageContext.From(package);

            // Scriban 解析与渲染为 CPU 密集操作，在后台任务执行避免阻塞 UI 线程
            PreviewResult result = await Task.Run(
                () => _templateEngine.Render(templateText, table, null, packageContext, ct, package.Name),
                ct);

            if (version != _renderVersion)
            {
                return;
            }

            ApplyRenderResult(result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "模板预览渲染失败，包 {PackageName}。", package.Name);
            UpdatePreviewEmpty($"渲染失败：{exception.Message}");
        }
        finally
        {
            if (version == _renderVersion)
            {
                IsRendering = false;
            }
        }
    }

    /// <summary>
    /// 应用渲染结果：成功回填真实代码并展示耗时，失败展示结构化错误并定位编辑器。
    /// </summary>
    /// <param name="result">共享渲染引擎返回的渲染结果。</param>
    private void ApplyRenderResult(PreviewResult result)
    {
        if (result.IsSuccess)
        {
            PreviewText = result.Output;
            PreviewTextChanged?.Invoke(result.Output);
            StatusText = $"渲染成功：{result.RenderDurationMs}ms";
            return;
        }

        PreviewText = string.Empty;
        PreviewTextChanged?.Invoke(string.Empty);
        StatusText = result.ErrorMessage;

        if (result.ErrorLine is not null && result.ErrorColumn is not null)
        {
            NavigateToEditor?.Invoke(result.ErrorLine.Value, result.ErrorColumn.Value);
        }
    }

    /// <summary>
    /// 更新预览区为空态：清空渲染文本并展示提示文案。
    /// </summary>
    /// <param name="message">空态提示文本。</param>
    private void UpdatePreviewEmpty(string message)
    {
        PreviewText = string.Empty;
        PreviewTextChanged?.Invoke(string.Empty);
        StatusText = message;
    }
}
