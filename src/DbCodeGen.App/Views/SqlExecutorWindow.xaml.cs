using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DbCodeGen.App.ViewModels;
using ICSharpCode.AvalonEdit.Highlighting;

namespace DbCodeGen.App.Views;

/// <summary>
/// SQL 执行面板窗口，承载数据源选择、SQL 编辑、执行参数与结果/错误展示，经 SqlExecutorViewModel 完成执行闭环。
/// 编辑器文本经事件桥接到视图模型；窗口关闭时取消在途执行并解除当前连接订阅。
/// </summary>
public partial class SqlExecutorWindow : Window
{
    private readonly SqlExecutorViewModel _viewModel;

    /// <summary>
    /// 加载 SQL 文档期间抑制编辑器文本变化事件，避免载入文本被误判为用户编辑。
    /// </summary>
    private bool _isLoadingDocument;

    /// <summary>
    /// 使用指定视图模型构造 SQL 执行面板窗口，并应用 AvalonEdit 内置 SQL 高亮。
    /// </summary>
    /// <param name="viewModel">SQL 执行面板视图模型，承载执行闭环与结果展示。</param>
    /// <exception cref="ArgumentNullException">viewModel 为 null 时抛出。</exception>
    public SqlExecutorWindow(SqlExecutorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;

        // SQL 高亮采用 AvalonEdit 内置 SQL 定义，与模板预览区 SQL 高亮一致
        SqlEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("TSQL");

        _viewModel.LoadSqlRequested += OnLoadSqlRequested;
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 窗口呈现完成后初始化面板：加载数据源列表并默认选中当前连接，仅触发一次后解除订阅。
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync();
    }

    /// <summary>
    /// SQL 编辑器文本变化事件：加载文档期间跳过，其余情况同步到视图模型并触发执行命令可用性重评估。
    /// </summary>
    private void OnSqlEditorTextChanged(object? sender, EventArgs e)
    {
        if (_isLoadingDocument)
        {
            return;
        }

        _viewModel.NotifySqlTextChanged(SqlEditor.Text);
    }

    /// <summary>
    /// 视图模型载入 SQL 文本请求：重置撤销栈后写入编辑器，加载期间抑制置脏，随后光标归零并聚焦编辑器。
    /// </summary>
    /// <param name="text">待载入的 SQL 文本。</param>
    private void OnLoadSqlRequested(string text)
    {
        _isLoadingDocument = true;
        try
        {
            SqlEditor.Clear();
            SqlEditor.Document.Text = text;
        }
        finally
        {
            _isLoadingDocument = false;
        }

        SqlEditor.CaretOffset = 0;
        SqlEditor.Focus();
    }

    /// <summary>
    /// 结果表格自动生成列时替换表头为“列名（显示类型）”，列键保持纯列名保证绑定路径安全。
    /// </summary>
    private void OnResultGridAutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.PropertyName is not null && _viewModel.TryGetResultColumnHeader(e.PropertyName, out string? header))
        {
            e.Column.Header = header;
        }
    }

    /// <summary>
    /// 窗口关闭前取消在途执行，保证后台调用随窗口关闭停止。
    /// </summary>
    /// <param name="e">关闭事件参数。</param>
    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.CancelPendingExecution();
        base.OnClosing(e);
    }

    /// <summary>
    /// 窗口关闭时解除当前连接变更订阅与文本载入事件订阅，避免悬挂引用。
    /// </summary>
    /// <param name="e">关闭事件参数。</param>
    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Detach();
        _viewModel.LoadSqlRequested -= OnLoadSqlRequested;
        base.OnClosed(e);
    }
}
