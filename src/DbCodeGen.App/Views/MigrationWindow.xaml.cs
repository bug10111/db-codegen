using System.ComponentModel;
using System.Windows;
using DbCodeGen.App.ViewModels;

namespace DbCodeGen.App.Views;

/// <summary>
/// 迁移窗口，承载备份与恢复两页：备份页选择目标路径并预览包清单，恢复页选择备份文件、
/// 校验预览与冲突确认，通过 MigrationViewModel 完成备份恢复闭环。
/// 窗口关闭时取消在途备份/恢复操作，保证取消经取消令牌贯穿调用链。
/// </summary>
public partial class MigrationWindow : Window
{
    private readonly MigrationViewModel _viewModel;

    /// <summary>
    /// 使用指定视图模型构造迁移窗口，并在呈现完成后触发初始化。
    /// </summary>
    /// <param name="viewModel">迁移窗口视图模型，承载备份恢复闭环。</param>
    /// <exception cref="ArgumentNullException">viewModel 为 null 时抛出。</exception>
    public MigrationWindow(MigrationViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 窗口呈现完成后异步初始化：自动刷新备份页预览，仅触发一次后解除订阅。
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync();
    }

    /// <summary>
    /// 点击取消操作取消在途备份/恢复，操作调用链经取消令牌感知取消。
    /// </summary>
    private void OnCancelOperationClick(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelPendingOperation();
    }

    /// <summary>
    /// 点击关闭直接关闭窗口，关闭路径会取消在途备份/恢复操作。
    /// </summary>
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// 窗口关闭前取消在途备份/恢复操作，防止后台调用继续运行。
    /// </summary>
    /// <param name="e">关闭事件参数。</param>
    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.CancelPendingOperation();
        base.OnClosing(e);
    }
}
