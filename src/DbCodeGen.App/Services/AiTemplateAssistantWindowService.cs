using System.Windows;
using DbCodeGen.App.ViewModels;
using DbCodeGen.App.Views;

namespace DbCodeGen.App.Services;

/// <summary>
/// 「AI 模板助手」窗口宿主服务实现（App.Ai）：承载单开复用生命周期。
/// 以字段缓存当前窗口实例，未打开或已关闭时经窗口工厂懒创建新实例并以 Show() 非模态展示，
/// 已打开且 IsVisible 时 Activate() 置顶复用，随后统一 ActivateTab(tab) 切换页签；
/// 窗口 Closed 事件置空缓存，保证下次入口调用重建全新实例（关闭即弃，状态不保留）。
/// 单开复用模式对齐 TemplateViewModel.OpenVariablePanel 的项目内成熟范本。
/// </summary>
public sealed class AiTemplateAssistantWindowService : IAiTemplateAssistantWindowService
{
    private readonly Func<AiTemplateAssistantWindow> _windowFactory;

    /// <summary>
    /// 当前「AI 模板助手」窗口实例缓存，关闭或不可见时置空允许下次重建。
    /// </summary>
    private AiTemplateAssistantWindow? _window;

    /// <summary>
    /// 使用窗口工厂构造窗口宿主服务。
    /// </summary>
    /// <param name="windowFactory">AI 模板助手窗口工厂，供服务按需懒创建窗口实例。</param>
    /// <exception cref="ArgumentNullException">windowFactory 为 null 时抛出。</exception>
    public AiTemplateAssistantWindowService(Func<AiTemplateAssistantWindow> windowFactory)
    {
        ArgumentNullException.ThrowIfNull(windowFactory);
        _windowFactory = windowFactory;
    }

    /// <inheritdoc />
    public Task ShowAsync(AiAssistantTab tab)
    {
        // 未打开或已关闭时懒创建新窗口实例并以非模态方式展示，Owner 取主窗口保证同屏置顶层级
        if (_window is null || !_window.IsVisible)
        {
            AiTemplateAssistantWindow window = _windowFactory();
            window.Owner = Application.Current?.MainWindow;
            window.Closed += (_, _) => _window = null;
            _window = window;
            window.Show();
        }

        // 已打开窗口置顶激活并切换对应 Tab，保证单开复用不重复创建实例
        _window.Activate();
        _window.ActivateTab(tab);
        return Task.CompletedTask;
    }
}
