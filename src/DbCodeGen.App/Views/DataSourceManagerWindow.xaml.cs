using System.Windows;
using DbCodeGen.App.ViewModels;

namespace DbCodeGen.App.Views;

/// <summary>
/// 数据源管理窗口，承载已保存连接列表与连接表单，通过 DataSourceViewModel 完成增删改查。
/// </summary>
public partial class DataSourceManagerWindow : Window
{
    private readonly DataSourceViewModel _viewModel;

    /// <summary>
    /// 使用指定视图模型构造数据源管理窗口，并建立密码框输入与视图模型的回填关联。
    /// </summary>
    /// <param name="viewModel">数据源管理视图模型，承载连接列表与表单逻辑。</param>
    /// <exception cref="ArgumentNullException">viewModel 为 null 时抛出。</exception>
    public DataSourceManagerWindow(DataSourceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        PasswordBox.PasswordChanged += OnPasswordChanged;
        _viewModel.PasswordClearRequested += OnPasswordClearRequested;
    }

    /// <summary>
    /// 密码变化时同步到视图模型，保存与测试时据此判断使用明文或沿用已保存密文。
    /// </summary>
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        _viewModel.PasswordInput = PasswordBox.Password;
    }

    /// <summary>
    /// 视图模型请求清空密码框时执行，用于表单重置与切换编辑对象的场景。
    /// </summary>
    private void OnPasswordClearRequested(object? sender, EventArgs e)
    {
        PasswordBox.Clear();
    }

    /// <summary>
    /// 窗口关闭时解除事件订阅，避免悬挂引用。
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        PasswordBox.PasswordChanged -= OnPasswordChanged;
        _viewModel.PasswordClearRequested -= OnPasswordClearRequested;
        _viewModel.Detach();
        base.OnClosed(e);
    }
}
