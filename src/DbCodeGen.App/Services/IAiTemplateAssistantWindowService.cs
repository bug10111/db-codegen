using DbCodeGen.App.ViewModels;

namespace DbCodeGen.App.Services;

/// <summary>
/// 「AI 模板助手」窗口宿主服务（App.Ai）：封装窗口单开复用生命周期——未打开时懒创建新实例并非模态展示，
/// 已打开且可见时激活复用，随后统一切换对应 Tab；窗口关闭后置空缓存允许下次重建。
/// MainWindow 的「AI 写模板 / AI 改模板」入口注入本服务，分别以 Write / Modify 激活对应 Tab。
/// </summary>
public interface IAiTemplateAssistantWindowService
{
    /// <summary>
    /// 打开/激活「AI 模板助手」窗口并激活指定 Tab：未打开时懒创建并非模态 Show()（Owner=MainWindow），
    /// 已打开且 IsVisible 时 Activate() 置顶复用，随后统一 ActivateTab(tab) 切换页签。
    /// </summary>
    /// <param name="tab">要激活的 Tab（Write=写模板，Modify=改模板）。</param>
    /// <returns>窗口展示与 Tab 激活完成的异步操作。</returns>
    Task ShowAsync(AiAssistantTab tab);
}
