using System.ComponentModel;
using System.Windows;
using DbCodeGen.App.ViewModels;

namespace DbCodeGen.App.Views;

/// <summary>
/// 「AI 模板助手」窗口宿主（App.Ai）：非模态、单开复用的双 Tab 容器，承载「写模板」「改模板」两个 Tab。
/// 写模板 Tab 内容区由写模板功能填充，改模板 Tab 内容区由改模板功能填充；本窗口只提供容器与 Tab 切换能力。
/// 窗口级共享参考文件栏位于 Tab 内容上方、TabControl 之外，两个 Tab 共读共改。
/// 窗口关闭时取消在途任务（写模板生成/改模板发送），保证取消经取消令牌贯穿调用链。
/// </summary>
public partial class AiTemplateAssistantWindow : Window
{
    private readonly AiTemplateAssistantViewModel _viewModel;

    /// <summary>
    /// 使用指定宿主视图模型构造 AI 模板助手窗口，并在呈现完成后触发初始化。
    /// </summary>
    /// <param name="viewModel">AI 模板助手宿主视图模型，承载共享参考文件上下文与写模板生成闭环骨架。</param>
    /// <exception cref="ArgumentNullException">viewModel 为 null 时抛出。</exception>
    public AiTemplateAssistantWindow(AiTemplateAssistantViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 激活指定 Tab（Write=0，Modify=1），供窗口宿主服务 ShowAsync(tab) 在打开/激活后统一切换页签。
    /// </summary>
    /// <param name="tab">要激活的 Tab。</param>
    public void ActivateTab(AiAssistantTab tab)
    {
        MainTabControl.SelectedIndex = (int)tab;
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
    /// 窗口关闭前取消在途任务与样例表读取，防止后台调用继续运行。
    /// </summary>
    /// <param name="e">关闭事件参数。</param>
    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.CancelPendingWork();
        base.OnClosing(e);
    }
}
