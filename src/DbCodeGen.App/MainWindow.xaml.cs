using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DbCodeGen.App.Services;
using DbCodeGen.App.ViewModels;
using DbCodeGen.App.Views;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.App;

/// <summary>
/// 主窗口，承载工具栏数据源下拉切换当前连接与四区布局，四区功能由对应任务逐区填充。
/// ②模板区绑定模板编辑器视图模型，③预览区绑定预览视图模型，编辑器文本与渲染结果经事件在视图层桥接。
/// </summary>
public partial class MainWindow : Window
{
    private readonly IConfigService _configService;
    private readonly ICurrentDataSourceService _currentDataSourceService;
    private readonly IDialogService _dialogService;
    private readonly Func<DataSourceManagerWindow> _dataSourceManagerWindowFactory;
    private readonly Func<SettingsWindow> _settingsWindowFactory;
    private readonly IAiTemplateAssistantWindowService _aiTemplateAssistantWindowService;
    private readonly Func<SqlExecutorWindow> _sqlExecutorWindowFactory;
    private readonly Func<MigrationWindow> _migrationWindowFactory;
    private readonly Func<TypeMappingWindow> _typeMappingWindowFactory;
    private readonly Func<TemplatePackageManagerWindow> _templateManagerWindowFactory;
    private readonly TableListViewModel _tableListViewModel;
    private readonly TemplateViewModel _templateViewModel;
    private readonly PreviewViewModel _previewViewModel;
    private readonly GenerationViewModel _generationViewModel;
    private readonly HighlightingService _highlightingService;
    private readonly ILogger<MainWindow> _logger;

    /// <summary>
    /// 程序性同步下拉选中项期间抑制再次触发切换，防止与用户操作互相干扰。
    /// </summary>
    private bool _isSyncingSelection;

    /// <summary>
    /// 加载文档期间抑制编辑器 TextChanged 置脏，避免载入文本被误判为用户编辑。
    /// </summary>
    private bool _isLoadingDocument;

    /// <summary>
    /// 上区三栏各列恢复宽度的下限，防止记忆值过小导致列不可用。
    /// </summary>
    private const double MinColumnWidth = 250;

    /// <summary>
    /// 上区三栏整体行高恢复下限，与 XAML 三栏网格 MinHeight 对齐。
    /// </summary>
    private const double MinTopRowHeight = 300;

    /// <summary>
    /// ④生成栏底部日志面板高度恢复下限，与 XAML 面板 MinHeight 对齐。
    /// </summary>
    private const double MinLogPanelHeight = 64;

    /// <summary>
    /// 使用配置服务、当前连接服务、对话框服务、数据源管理窗口工厂、设置窗口工厂、AI 模板助手窗口宿主服务、
    /// SQL 执行面板窗口工厂、迁移窗口工厂、类型映射窗口工厂、模板包管理窗口工厂、表列表视图模型、
    /// 模板编辑器视图模型、预览视图模型、高亮服务与日志器构造主窗口。
    /// </summary>
    /// <param name="configService">配置持久化服务，用于读取已保存数据源列表。</param>
    /// <param name="currentDataSourceService">当前连接共享状态服务，用于切换当前连接与接收变更通知。</param>
    /// <param name="dialogService">消息提示服务，用于打开管理窗口失败等场景反馈。</param>
    /// <param name="dataSourceManagerWindowFactory">数据源管理窗口工厂，供“管理…”入口按需创建。</param>
    /// <param name="settingsWindowFactory">设置窗口工厂，供“文件”菜单“设置…”入口按需创建。</param>
    /// <param name="aiTemplateAssistantWindowService">AI 模板助手窗口宿主服务，供②工具条“AI 写模板 / AI 改模板”入口打开/激活窗口并切对应 Tab。</param>
    /// <param name="sqlExecutorWindowFactory">SQL 执行面板窗口工厂，供“工具”菜单入口按需创建。</param>
    /// <param name="migrationWindowFactory">迁移窗口工厂，供“工具”菜单“备份/恢复…”入口按需创建。</param>
    /// <param name="typeMappingWindowFactory">类型映射窗口工厂，供“工具”菜单“类型映射…”入口按需创建。</param>
    /// <param name="templateManagerWindowFactory">模板包管理窗口工厂，供“工具”菜单“模板包管理…”入口按需创建。</param>
    /// <param name="tableListViewModel">表列表区视图模型，承载表清单加载与多选勾选。</param>
    /// <param name="templateViewModel">模板编辑器视图模型，承载②模板区文件树、编辑器与保存。</param>
    /// <param name="previewViewModel">预览视图模型，承载③预览区选表渲染与错误定位。</param>
    /// <param name="generationViewModel">生成栏视图模型，承载④生成栏路径配置、预览与生成写盘。</param>
    /// <param name="highlightingService">编辑器高亮服务，按目标语言应用高亮定义。</param>
    /// <param name="logger">主窗口日志器，用于关闭路径回写失败等场景记录。</param>
    /// <exception cref="ArgumentNullException">任一依赖参数为 null 时抛出。</exception>
    public MainWindow(
        IConfigService configService,
        ICurrentDataSourceService currentDataSourceService,
        IDialogService dialogService,
        Func<DataSourceManagerWindow> dataSourceManagerWindowFactory,
        Func<SettingsWindow> settingsWindowFactory,
        IAiTemplateAssistantWindowService aiTemplateAssistantWindowService,
        Func<SqlExecutorWindow> sqlExecutorWindowFactory,
        Func<MigrationWindow> migrationWindowFactory,
        Func<TypeMappingWindow> typeMappingWindowFactory,
        Func<TemplatePackageManagerWindow> templateManagerWindowFactory,
        TableListViewModel tableListViewModel,
        TemplateViewModel templateViewModel,
        PreviewViewModel previewViewModel,
        GenerationViewModel generationViewModel,
        HighlightingService highlightingService,
        ILogger<MainWindow> logger)
    {
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(currentDataSourceService);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(dataSourceManagerWindowFactory);
        ArgumentNullException.ThrowIfNull(settingsWindowFactory);
        ArgumentNullException.ThrowIfNull(aiTemplateAssistantWindowService);
        ArgumentNullException.ThrowIfNull(sqlExecutorWindowFactory);
        ArgumentNullException.ThrowIfNull(migrationWindowFactory);
        ArgumentNullException.ThrowIfNull(typeMappingWindowFactory);
        ArgumentNullException.ThrowIfNull(templateManagerWindowFactory);
        ArgumentNullException.ThrowIfNull(tableListViewModel);
        ArgumentNullException.ThrowIfNull(templateViewModel);
        ArgumentNullException.ThrowIfNull(previewViewModel);
        ArgumentNullException.ThrowIfNull(generationViewModel);
        ArgumentNullException.ThrowIfNull(highlightingService);
        ArgumentNullException.ThrowIfNull(logger);

        InitializeComponent();
        _configService = configService;
        _currentDataSourceService = currentDataSourceService;
        _dialogService = dialogService;
        _dataSourceManagerWindowFactory = dataSourceManagerWindowFactory;
        _settingsWindowFactory = settingsWindowFactory;
        _aiTemplateAssistantWindowService = aiTemplateAssistantWindowService;
        _sqlExecutorWindowFactory = sqlExecutorWindowFactory;
        _migrationWindowFactory = migrationWindowFactory;
        _typeMappingWindowFactory = typeMappingWindowFactory;
        _templateManagerWindowFactory = templateManagerWindowFactory;
        _tableListViewModel = tableListViewModel;
        _templateViewModel = templateViewModel;
        _previewViewModel = previewViewModel;
        _generationViewModel = generationViewModel;
        _highlightingService = highlightingService;
        _logger = logger;

        // ①表列表区绑定表浏览视图模型，②模板区绑定模板编辑器视图模型，③预览区绑定预览视图模型，④生成栏绑定生成视图模型
        TableListPanel.DataContext = _tableListViewModel;
        TemplatePanel.DataContext = _templateViewModel;
        PreviewPanel.DataContext = _previewViewModel;
        GenerationPanel.DataContext = _generationViewModel;

        // ②区文件树挂接拖拽排序：落位索引由拖拽辅助计算，经视图模型复用与上移/下移一致的移动逻辑并持久化顺序记忆
        ListViewDragDropHelper.Attach(TemplateFileListView, (sourceIndex, targetIndex) =>
            _templateViewModel.MoveFileTo(sourceIndex, targetIndex));

        SubscribeEditorEvents();
        ApplyHighlighting(_templateViewModel.Language);

        // 确保配置已加载，随后订阅当前连接变更并初始化工具栏数据源下拉
        _configService.Load();
        _currentDataSourceService.CurrentChanged += OnCurrentChanged;
        ReloadDataSourceComboBox();

        // 首次布局完成后恢复上次保存的三栏列宽与纵向行高
        Loaded += OnMainWindowLoaded;

        // 异步加载模板包列表，失败由视图模型内部提示不中断主窗口
        _ = _templateViewModel.InitializeAsync();
    }

    /// <summary>
    /// 工具栏数据源下拉选中项变化时，将所选连接设为当前连接并触发变更通知。
    /// </summary>
    private void OnDataSourceComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 程序性同步选中项期间不重复触发切换，避免循环通知
        if (_isSyncingSelection)
        {
            return;
        }

        _currentDataSourceService.SetCurrent(DataSourceComboBox.SelectedItem as DataSourceConfig);
    }

    /// <summary>
    /// 当前连接变更时同步工具栏下拉选中项，覆盖数据源管理窗口设当前与删除联动场景。
    /// </summary>
    /// <param name="config">变更后的当前连接，清除时为 null。</param>
    private void OnCurrentChanged(DataSourceConfig? config)
    {
        _isSyncingSelection = true;
        try
        {
            // 按连接名称定位下拉项，找不到（例如连接已被删除）则清空选中
            DataSourceComboBox.SelectedItem = FindComboBoxItem(config);
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    /// <summary>
    /// 点击“管理…”打开数据源管理窗口，窗口关闭后重载下拉列表并同步当前连接。
    /// </summary>
    private void OnManageDataSourcesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            DataSourceManagerWindow window = _dataSourceManagerWindowFactory();
            window.Owner = this;
            window.ShowDialog();
        }
        catch (Exception exception)
        {
            // 窗口创建或展示失败时给用户可读提示，不中断主窗口运行
            _dialogService.ShowError($"打开数据源管理窗口失败：{exception.Message}");
            return;
        }

        // 管理窗口可能增删改连接或切换当前连接，关闭后重载下拉并同步当前连接
        ReloadDataSourceComboBox();
    }

    /// <summary>
    /// 点击菜单“设置…”打开设置窗口，配置保存后关闭，主窗口数据源下拉不依赖设置结果。
    /// </summary>
    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SettingsWindow window = _settingsWindowFactory();
            window.Owner = this;
            window.ShowDialog();
        }
        catch (Exception exception)
        {
            // 窗口创建或展示失败时给用户可读提示，不中断主窗口运行
            _dialogService.ShowError($"打开设置窗口失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 点击菜单“刷新表”触发①表列表区刷新，与表区内“刷新表”按钮同一命令。
    /// </summary>
    private void OnRefreshTablesClick(object sender, RoutedEventArgs e)
    {
        _tableListViewModel.RefreshCommand.Execute(null);
    }

    /// <summary>
    /// 点击菜单“退出”关闭主窗口，关闭路径经 OnClosing/OnClosed 完成未保存检查与资源释放。
    /// </summary>
    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// 点击菜单“关于 DbCodeGen…”展示应用名称、版本与简介。
    /// </summary>
    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        // 版本号由程序集版本派生，避免展示硬编码文案与实际发布版本漂移
        string version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        _dialogService.ShowInfo(
            "DbCodeGen 代码生成器\n\n数据库驱动代码生成工具：连接 MySQL/PostgreSQL，读取表元数据，\n"
            + "多选表 × 模板包（勾选到层）→ 渲染 → dry-run 预览 → 安全写盘。\n"
            + "内置模板编辑与实时预览、AI 模板生成、SQL 执行面板。\n\n版本：v" + version,
            "关于 DbCodeGen");
    }

    /// <summary>
    /// 点击②工具条「AI 写模板」打开/激活「AI 模板助手」窗口并切到写模板 Tab，
    /// 窗口单开复用生命周期由注入的窗口宿主服务承载。
    /// </summary>
    private async void OnAiWriteTemplateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _aiTemplateAssistantWindowService.ShowAsync(AiAssistantTab.Write);
        }
        catch (Exception exception)
        {
            // 窗口创建或展示失败时给用户可读提示，不中断主窗口运行
            _dialogService.ShowError($"打开 AI 模板助手失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 点击②工具条「AI 改模板」打开/激活「AI 模板助手」窗口并切到改模板 Tab，
    /// 窗口单开复用生命周期由注入的窗口宿主服务承载。
    /// </summary>
    private async void OnAiModifyTemplateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _aiTemplateAssistantWindowService.ShowAsync(AiAssistantTab.Modify);
        }
        catch (Exception exception)
        {
            // 窗口创建或展示失败时给用户可读提示，不中断主窗口运行
            _dialogService.ShowError($"打开 AI 模板助手失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 点击菜单“SQL 执行面板…”打开 SQL 执行面板窗口，面板默认取当前连接并联动当前连接变更。
    /// </summary>
    private void OnOpenSqlPanelClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SqlExecutorWindow window = _sqlExecutorWindowFactory();
            window.Owner = this;
            window.ShowDialog();
        }
        catch (Exception exception)
        {
            // 窗口创建或展示失败时给用户可读提示，不中断主窗口运行
            _dialogService.ShowError($"打开 SQL 执行面板失败：{exception.Message}");
            return;
        }
    }

    /// <summary>
    /// 点击菜单“备份/恢复…”打开迁移窗口，完成换电脑迁移场景的备份与恢复。
    /// </summary>
    private void OnOpenMigrationClick(object sender, RoutedEventArgs e)
    {
        try
        {
            MigrationWindow window = _migrationWindowFactory();
            window.Owner = this;
            window.ShowDialog();
        }
        catch (Exception exception)
        {
            // 窗口创建或展示失败时给用户可读提示，不中断主窗口运行
            _dialogService.ShowError($"打开备份/恢复窗口失败：{exception.Message}");
            return;
        }
    }

    /// <summary>
    /// 点击菜单“类型映射…”打开类型映射窗口，配置数据库类型到 Java 类型的全局映射。
    /// </summary>
    private void OnOpenTypeMappingClick(object sender, RoutedEventArgs e)
    {
        try
        {
            TypeMappingWindow window = _typeMappingWindowFactory();
            window.Owner = this;
            window.ShowDialog();
        }
        catch (Exception exception)
        {
            // 窗口创建或展示失败时给用户可读提示，不中断主窗口运行
            _dialogService.ShowError($"打开类型映射窗口失败：{exception.Message}");
            return;
        }
    }

    /// <summary>
    /// 点击菜单“模板包管理…”打开模板包管理窗口，完成导入/复制/导出/删除整包操作；
    /// 窗口关闭后刷新②区包列表，同步导入或删除的包。
    /// </summary>
    private void OnOpenTemplateManagerClick(object sender, RoutedEventArgs e)
    {
        try
        {
            TemplatePackageManagerWindow window = _templateManagerWindowFactory();
            window.Owner = this;
            window.ShowDialog();
        }
        catch (Exception exception)
        {
            // 窗口创建或展示失败时给用户可读提示，不中断主窗口运行
            _dialogService.ShowError($"打开模板包管理窗口失败：{exception.Message}");
            return;
        }

        // 管理窗口可能导入/复制/删除包，关闭后刷新②区包列表与当前选中保持最新
        _templateViewModel.RefreshCommand.Execute(null);
    }

    /// <summary>
    /// 依据配置快照重建工具栏数据源下拉项；当前连接为空但存在数据源时默认选中第一项，
    /// 经真实选中事件触发 SetCurrent 联动①表列表区自动刷新。
    /// </summary>
    private void ReloadDataSourceComboBox()
    {
        AppConfig config = _configService.Current;

        // 每次重建为独立集合副本，保证连接增删后下拉即时刷新
        List<DataSourceConfig> sources = config.DataSources.ToList();

        _isSyncingSelection = true;
        try
        {
            DataSourceComboBox.ItemsSource = sources;
        }
        finally
        {
            _isSyncingSelection = false;
        }

        if (_currentDataSourceService.Current is null && sources.Count > 0)
        {
            // 当前连接为空但列表有数据源时默认选中第一项（守卫外触发真实切换），联动①区自动刷表
            DataSourceComboBox.SelectedItem = sources[0];
        }
        else
        {
            // 有当前连接或列表为空时经守卫同步选中，避免重复触发切换通知
            _isSyncingSelection = true;
            try
            {
                DataSourceComboBox.SelectedItem = FindComboBoxItem(_currentDataSourceService.Current);
            }
            finally
            {
                _isSyncingSelection = false;
            }
        }
    }

    /// <summary>
    /// 在下拉数据源集合中按连接名称定位匹配项，未找到返回 null。
    /// </summary>
    /// <param name="config">要定位的连接配置，可为 null。</param>
    /// <returns>与目标连接名称一致的已保存连接，不存在时返回 null。</returns>
    private DataSourceConfig? FindComboBoxItem(DataSourceConfig? config)
    {
        if (config is null)
        {
            return null;
        }

        IEnumerable? items = DataSourceComboBox.ItemsSource;
        if (items is null)
        {
            return null;
        }

        // 将下拉数据源转为可枚举连接集合后按名称定位目标连接
        IEnumerable<DataSourceConfig> sources = items.Cast<DataSourceConfig>();
        return sources.FirstOrDefault(item => string.Equals(item.Name, config.Name, StringComparison.Ordinal));
    }

    /// <summary>
    /// 模板文件树 checkbox 点击事件：勾选到层切换后广播给生成栏，使其重新评估“勾选到层”命令可用性。
    /// 标记事件已处理，阻止 Click 路由事件继续向上冒泡，避免父级意外响应。
    /// </summary>
    private void OnTemplateFileCheckBoxClick(object sender, RoutedEventArgs e)
    {
        _templateViewModel.NotifyFileSelectionChanged();
        e.Handled = true;
    }

    /// <summary>
    /// 文件树行右键按下时选中该行，保证上下文菜单“删除模板”作用于右键目标而非上次左键选中项。
    /// </summary>
    private void OnTemplateFileListItemPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem item)
        {
            item.IsSelected = true;
        }
    }

    /// <summary>
    /// 订阅模板编辑器与预览视图模型的事件，完成编辑器文本、高亮、插入与渲染结果的视图层桥接。
    /// </summary>
    private void SubscribeEditorEvents()
    {
        _templateViewModel.LoadDocumentRequested += OnLoadDocumentRequested;
        _templateViewModel.ClearDocumentRequested += OnClearDocumentRequested;
        _templateViewModel.LanguageChanged += OnLanguageChanged;
        _templateViewModel.InsertVariableRequested += OnInsertVariableRequested;
        _templateViewModel.ReplaceDocumentRequested += OnReplaceDocumentRequested;
        _previewViewModel.PreviewTextChanged += OnPreviewTextChanged;
        _previewViewModel.NavigateToEditor += OnNavigateToEditor;
    }

    /// <summary>
    /// 解除模板编辑器与预览视图模型的事件订阅，避免窗口关闭后悬挂引用。
    /// </summary>
    private void UnsubscribeEditorEvents()
    {
        _templateViewModel.LoadDocumentRequested -= OnLoadDocumentRequested;
        _templateViewModel.ClearDocumentRequested -= OnClearDocumentRequested;
        _templateViewModel.LanguageChanged -= OnLanguageChanged;
        _templateViewModel.InsertVariableRequested -= OnInsertVariableRequested;
        _templateViewModel.ReplaceDocumentRequested -= OnReplaceDocumentRequested;
        _previewViewModel.PreviewTextChanged -= OnPreviewTextChanged;
        _previewViewModel.NavigateToEditor -= OnNavigateToEditor;
    }

    /// <summary>
    /// 模板编辑器 TextChanged 事件：加载文档期间跳过，其余情况同步到视图模型并触发脏标记与预览防抖。
    /// </summary>
    private void OnTemplateEditorTextChanged(object? sender, EventArgs e)
    {
        if (_isLoadingDocument)
        {
            return;
        }

        _templateViewModel.NotifyEditorTextChanged(TemplateEditor.Text);
    }

    /// <summary>
    /// 载入模板文本到编辑器：重置撤销栈后写入文本，加载期间抑制置脏，随后光标归零并聚焦编辑器。
    /// </summary>
    /// <param name="text">待载入的模板文本。</param>
    private void OnLoadDocumentRequested(string text)
    {
        _isLoadingDocument = true;
        try
        {
            TemplateEditor.Clear();
            TemplateEditor.Document.Text = text;
        }
        finally
        {
            _isLoadingDocument = false;
        }

        TemplateEditor.CaretOffset = 0;
        TemplateEditor.Focus();
    }

    /// <summary>
    /// 整体替换模板编辑器文本：AI 改模板「应用到编辑器」经 TemplateViewModel.ApplyAiEditedTemplate 触发。
    /// 加载期间抑制置脏，写入后清空撤销栈防止回退到 AI 替换前状态，光标归零并聚焦编辑器。
    /// </summary>
    /// <param name="text">AI 返回的完整新模板文件内容。</param>
    private void OnReplaceDocumentRequested(string text)
    {
        _isLoadingDocument = true;
        try
        {
            TemplateEditor.Clear();
            TemplateEditor.Document.Text = text;
        }
        finally
        {
            _isLoadingDocument = false;
        }

        // 清空撤销栈，防止回退到 AI 替换前的文档状态
        TextDocument document = TemplateEditor.Document;
        document.UndoStack.ClearAll();
        TemplateEditor.CaretOffset = 0;
        TemplateEditor.Focus();
    }

    /// <summary>
    /// 清空模板编辑器内容，加载期间抑制置脏。
    /// </summary>
    private void OnClearDocumentRequested()
    {
        _isLoadingDocument = true;
        try
        {
            TemplateEditor.Clear();
        }
        finally
        {
            _isLoadingDocument = false;
        }
    }

    /// <summary>
    /// 高亮语言变更时按目标语言应用高亮定义到模板编辑器与预览编辑器。
    /// </summary>
    /// <param name="language">模板文件推导出的目标语言。</param>
    private void OnLanguageChanged(HighlightLanguage language)
    {
        ApplyHighlighting(language);
    }

    /// <summary>
    /// 将高亮定义应用到模板编辑器与预览编辑器，二者语言保持一致。
    /// </summary>
    /// <param name="language">目标语言。</param>
    private void ApplyHighlighting(HighlightLanguage language)
    {
        IHighlightingDefinition definition = _highlightingService.GetDefinition(language);
        TemplateEditor.SyntaxHighlighting = definition;
        PreviewEditor.SyntaxHighlighting = definition;
    }

    /// <summary>
    /// 在模板编辑器光标处插入变量表达式：有选中文本时替换选中内容，否则在光标处插入，随后聚焦编辑器。
    /// </summary>
    /// <param name="expression">变量面板生成的 Scriban 表达式。</param>
    private void OnInsertVariableRequested(string expression)
    {
        int offset = TemplateEditor.CaretOffset;
        int selectionLength = TemplateEditor.SelectionLength;

        if (selectionLength > 0)
        {
            TemplateEditor.SelectedText = expression;
        }
        else
        {
            TemplateEditor.Document.Insert(offset, expression);
        }

        TemplateEditor.CaretOffset = offset + expression.Length;
        TemplateEditor.Focus();
    }

    /// <summary>
    /// 渲染结果文本变化时回填预览编辑器并回到顶部。
    /// </summary>
    /// <param name="text">渲染后的真实代码，失败时为空串。</param>
    private void OnPreviewTextChanged(string text)
    {
        PreviewEditor.Document.Text = text;
        PreviewEditor.ScrollToHome();
    }

    /// <summary>
    /// 渲染错误定位：跳转模板编辑器到错误行列并聚焦。
    /// </summary>
    /// <param name="line">错误所在模板行号（从 1 开始）。</param>
    /// <param name="column">错误所在模板列号（从 1 开始）。</param>
    private void OnNavigateToEditor(int line, int column)
    {
        TemplateEditor.TextArea.Caret.Line = line;
        TemplateEditor.TextArea.Caret.Column = column;
        TemplateEditor.ScrollTo(line, column);
        TemplateEditor.Focus();
    }

    /// <summary>
    /// 生成日志文本变化时按需滚动到底部：仅当用户已处于底部附近时自动跟随最新日志，
    /// 向上翻阅旧日志时不被新日志打断。布局尚未完成时滚动量可能过期，延后到后台优先级再执行。
    /// </summary>
    /// <param name="sender">事件源，为生成日志文本框。</param>
    /// <param name="e">文本变化事件参数。</param>
    private void OnLogTextChanged(object sender, TextChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // 用户已处于底部附近（或日志未超出视口）才滚动到底部，避免打断向上翻阅
            if (LogTextBox.VerticalOffset + LogTextBox.ViewportHeight >= LogTextBox.ExtentHeight - 1)
            {
                LogTextBox.ScrollToEnd();
            }
        }), DispatcherPriority.Background);
    }

    /// <summary>
    /// 预览编辑器与生成日志的滚轮滚动接管：悬停即滚，按系统滚轮行数逐行滚动并标记事件已处理，
    /// 解决 AvalonEdit 与只读 TextBox 在未聚焦时滚轮无响应的问题。
    /// </summary>
    /// <param name="sender">事件源，为预览编辑器或日志文本框。</param>
    /// <param name="e">滚轮事件参数。</param>
    private void OnRegionMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is TextEditor editor)
        {
            ScrollTextEditorByWheel(editor, e.Delta);
            e.Handled = true;
            return;
        }

        if (sender is TextBox textBox)
        {
            ScrollTextBoxByWheel(textBox, e.Delta);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 按滚轮增量滚动 AvalonEdit 编辑器：以默认行高换算像素偏移后定位到新垂直偏移。
    /// 增量按 120 的分数换算，高分辨率触控板产生的小增量也能滚动，避免悬停滚轮无响应。
    /// </summary>
    /// <param name="editor">目标编辑器。</param>
    /// <param name="delta">滚轮增量，正数为向上滚动。</param>
    private static void ScrollTextEditorByWheel(TextEditor editor, int delta)
    {
        // 默认行高取文本视图实际值，非法时回退固定行高，保证滚动步长稳定
        double lineHeight = editor.TextArea.TextView.DefaultLineHeight;
        if (lineHeight <= 0)
        {
            lineHeight = 15;
        }

        int linesPerNotch = SystemParameters.WheelScrollLines;
        if (linesPerNotch <= 0)
        {
            linesPerNotch = 3;
        }

        double notches = delta / 120.0;
        double targetOffset = editor.VerticalOffset - notches * linesPerNotch * lineHeight;
        editor.ScrollToVerticalOffset(targetOffset);
    }

    /// <summary>
    /// 按滚轮增量滚动只读文本框：沿可视树查找内部 ScrollViewer 并逐行滚动。
    /// 增量按 120 的分数换算，任意非零增量至少滚动一行，兼容高分辨率触控板的小增量事件。
    /// </summary>
    /// <param name="textBox">目标文本框。</param>
    /// <param name="delta">滚轮增量，正数为向上滚动。</param>
    private static void ScrollTextBoxByWheel(TextBox textBox, int delta)
    {
        ScrollViewer? viewer = FindVisualChild<ScrollViewer>(textBox);
        if (viewer is null)
        {
            return;
        }

        int linesPerNotch = SystemParameters.WheelScrollLines;
        if (linesPerNotch <= 0)
        {
            linesPerNotch = 3;
        }

        int totalLines = (int)Math.Round(delta / 120.0 * linesPerNotch);

        // 非零增量但行数换算后为零时兜底滚动一行，防止高分辨率触控板小增量被忽略
        if (totalLines == 0 && delta != 0)
        {
            totalLines = Math.Sign(delta);
        }

        if (totalLines > 0)
        {
            for (int index = 0; index < totalLines; index++)
            {
                viewer.LineUp();
            }
        }
        else if (totalLines < 0)
        {
            for (int index = 0; index < -totalLines; index++)
            {
                viewer.LineDown();
            }
        }
    }

    /// <summary>
    /// 沿可视树深度优先查找指定类型的子元素，未找到返回 null。
    /// </summary>
    /// <typeparam name="T">目标元素类型。</typeparam>
    /// <param name="parent">查找起点。</param>
    /// <returns>命中的子元素；未命中返回 null。</returns>
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            T? nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>
    /// 首次布局完成后恢复上次保存的主窗口布局：按配置记忆回填三栏列宽与纵向行高，并对记忆值做下限钳制。
    /// </summary>
    private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnMainWindowLoaded;
        RestoreMainLayout();
    }

    /// <summary>
    /// 从配置恢复主窗口布局：①列宽、②列宽、上区行高与④日志面板高度，非法记忆值按下限钳制跳过。
    /// </summary>
    private void RestoreMainLayout()
    {
        MainLayoutState layout = _configService.Current.MainLayout ?? new MainLayoutState();

        // ①列宽按像素记忆恢复，低于下限时保持默认起始宽度
        if (layout.TableColumnWidth is double tableWidth && tableWidth >= MinColumnWidth)
        {
            TopColumnsGrid.ColumnDefinitions[0].Width = new GridLength(tableWidth);
        }

        // ②列宽按像素记忆恢复，低于下限时保持星号均分
        if (layout.TemplateColumnWidth is double templateWidth && templateWidth >= MinColumnWidth)
        {
            TopColumnsGrid.ColumnDefinitions[2].Width = new GridLength(templateWidth);
        }

        // 上区整体行高按像素记忆恢复，低于下限时保持默认比例
        if (layout.TopRowHeight is double topHeight && topHeight >= MinTopRowHeight)
        {
            MainGrid.RowDefinitions[0].Height = new GridLength(topHeight);
        }

        // ④日志面板行高按像素记忆恢复，低于下限时保持默认比例（日志面板为可拉伸行，行高即其实际高度）
        if (layout.LogPanelHeight is double logHeight && logHeight >= MinLogPanelHeight)
        {
            GenerationInnerGrid.RowDefinitions[4].Height = new GridLength(logHeight);
        }
    }

    /// <summary>
    /// 窗口关闭时回写主窗口布局到配置：三栏列宽、上区行高与④日志面板高度，写盘失败仅记日志不阻塞关闭。
    /// </summary>
    private void PersistMainLayout()
    {
        AppConfig config = _configService.Current;
        MainLayoutState layout = config.MainLayout ??= new MainLayoutState();

        // 回写各维度的实际布局值，保证下次启动恢复用户调整后的布局
        layout.TableColumnWidth = TopColumnsGrid.ColumnDefinitions[0].ActualWidth;
        layout.TemplateColumnWidth = TopColumnsGrid.ColumnDefinitions[2].ActualWidth;
        layout.TopRowHeight = MainGrid.RowDefinitions[0].ActualHeight;
        layout.LogPanelHeight = GenerationInnerGrid.RowDefinitions[4].ActualHeight;

        try
        {
            _configService.Save();
        }
        catch (ConfigSaveException exception)
        {
            _logger.LogWarning(exception, "主窗口布局记忆回写失败。");
        }
    }

    /// <summary>
    /// 窗口关闭前检查未保存修改：存在脏文档时阻止关闭并经二次确认，确认后可关闭。
    /// 确认关闭前回写主窗口布局记忆，保证下次启动恢复用户调整后的布局。
    /// </summary>
    /// <param name="e">关闭事件参数。</param>
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_templateViewModel.HasDirtyDocument)
        {
            // 存在未保存修改时先取消本次关闭，异步确认后决定是否真正关闭
            e.Cancel = true;
            bool canClose = await _templateViewModel.ConfirmCloseAsync();
            if (canClose)
            {
                PersistMainLayout();
                Close();
            }

            return;
        }

        PersistMainLayout();
        base.OnClosing(e);
    }

    /// <summary>
    /// 窗口关闭时先回写④生成栏工作区根，随后解除编辑器事件订阅、当前连接变更订阅、
    /// 表列表视图模型订阅与模板视图模型配置变化订阅，避免悬挂引用。
    /// </summary>
    /// <param name="e">关闭事件参数。</param>
    protected override void OnClosed(EventArgs e)
    {
        PersistWorkspaceRoot();
        UnsubscribeEditorEvents();
        _previewViewModel.Detach();
        _currentDataSourceService.CurrentChanged -= OnCurrentChanged;
        _tableListViewModel.Detach();
        _generationViewModel.Detach();
        _templateViewModel.Detach();
        base.OnClosed(e);
    }

    /// <summary>
    /// 应用关闭时回写④生成栏工作区根到配置，覆盖"改了根目录但未生成即关闭"的场景。
    /// 与配置当前值一致时直接跳过，写盘失败仅记录日志，不阻塞关闭流程。
    /// </summary>
    private void PersistWorkspaceRoot()
    {
        if (string.Equals(
                _configService.Current.WorkspaceRoot,
                _generationViewModel.WorkspaceRoot,
                StringComparison.Ordinal))
        {
            return;
        }

        _configService.Current.WorkspaceRoot = _generationViewModel.WorkspaceRoot;
        try
        {
            _configService.Save();
        }
        catch (ConfigSaveException exception)
        {
            _logger.LogWarning(exception, "工作区根回写失败。");
        }
    }
}
