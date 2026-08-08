using System.Windows;
using DbCodeGen.App.ViewModels;

namespace DbCodeGen.App.Views;

/// <summary>
/// 设置窗口，承载四项应用配置的编辑表单，通过 SettingsViewModel 读写配置。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly Func<TypeMappingWindow> _typeMappingWindowFactory;

    /// <summary>
    /// 使用指定视图模型与类型映射窗口工厂构造设置窗口，并建立密码框输入与视图模型的回填关联。
    /// </summary>
    /// <param name="viewModel">设置视图模型，承载四项配置的加载、校验与保存逻辑。</param>
    /// <param name="typeMappingWindowFactory">类型映射窗口工厂，供底部“类型映射…”入口按需创建。</param>
    /// <exception cref="ArgumentNullException">viewModel 或 typeMappingWindowFactory 为 null 时抛出。</exception>
    public SettingsWindow(SettingsViewModel viewModel, Func<TypeMappingWindow> typeMappingWindowFactory)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(typeMappingWindowFactory);

        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _typeMappingWindowFactory = typeMappingWindowFactory;
        _viewModel.SaveCompleted += OnViewModelSaveCompleted;
        ApiKeyPasswordBox.PasswordChanged += OnApiKeyPasswordChanged;
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 点击底部“类型映射…”打开类型映射窗口，配置数据库类型到 Java 类型的全局映射。
    /// </summary>
    private void OnOpenTypeMappingClick(object sender, RoutedEventArgs e)
    {
        TypeMappingWindow window = _typeMappingWindowFactory();
        window.Owner = this;
        window.ShowDialog();
    }

    /// <summary>
    /// 窗口呈现完成后提示用户配置曾损坏恢复，避免提示框早于窗口出现。
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.NotifyConfigurationRecoveryIfNeeded();
    }

    /// <summary>
    /// 密码变化时同步到视图模型，保存时据此判断保持原密文或重加密覆盖。
    /// </summary>
    private void OnApiKeyPasswordChanged(object sender, RoutedEventArgs e)
    {
        _viewModel.ApiKeyInput = ApiKeyPasswordBox.Password;
    }

    /// <summary>
    /// 保存成功后由视图模型通知关闭窗口。
    /// </summary>
    private void OnViewModelSaveCompleted(object? sender, EventArgs e)
    {
        Close();
    }

    /// <summary>
    /// 点击取消直接关闭窗口，不写入任何配置。
    /// </summary>
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// 窗口关闭时解除事件订阅，避免悬挂引用。
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _viewModel.SaveCompleted -= OnViewModelSaveCompleted;
        ApiKeyPasswordBox.PasswordChanged -= OnApiKeyPasswordChanged;
        Loaded -= OnLoaded;
        base.OnClosed(e);
    }
}
