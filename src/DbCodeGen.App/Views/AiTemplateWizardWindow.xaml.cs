using System.ComponentModel;
using System.Windows;
using DbCodeGen.App.ViewModels;

namespace DbCodeGen.App.Views;

/// <summary>
/// AI 模板生成向导窗口，承载技术栈描述与样例表输入、生成进度与结果展示，通过 AiTemplateWizardViewModel 完成业务闭环。
/// 窗口关闭时取消在途生成，保证取消经取消令牌贯穿生成调用链。
/// </summary>
public partial class AiTemplateWizardWindow : Window
{
    private readonly AiTemplateWizardViewModel _viewModel;

    /// <summary>
    /// 使用指定视图模型构造 AI 向导窗口，并在呈现完成后触发初始化。
    /// </summary>
    /// <param name="viewModel">AI 向导视图模型，承载配置检查、样例表选择与生成闭环。</param>
    /// <exception cref="ArgumentNullException">viewModel 为 null 时抛出。</exception>
    public AiTemplateWizardWindow(AiTemplateWizardViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 窗口呈现完成后异步初始化：快照样例表候选集并执行 LLM 配置检查，仅触发一次后解除订阅。
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync();
    }

    /// <summary>
    /// 点击取消直接关闭窗口，关闭路径会取消在途生成。
    /// </summary>
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// 窗口关闭前取消在途生成与样例表读取，防止后台调用继续运行。
    /// </summary>
    /// <param name="e">关闭事件参数。</param>
    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.CancelPendingGeneration();
        base.OnClosing(e);
    }
}
