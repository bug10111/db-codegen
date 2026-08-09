namespace DbCodeGen.App.ViewModels;

/// <summary>
/// 「AI 模板助手」窗口的双 Tab 标识：写模板 / 改模板。
/// 取值与宿主窗口 TabControl.SelectedIndex 一一对应（Write=0，Modify=1），
/// 由窗口宿主服务 ShowAsync(tab) 与窗口 ActivateTab(tab) 契约统一使用。
/// </summary>
public enum AiAssistantTab
{
    /// <summary>
    /// 写模板 Tab，对应宿主窗口 TabControl 第一个页签。
    /// </summary>
    Write = 0,

    /// <summary>
    /// 改模板 Tab，对应宿主窗口 TabControl 第二个页签。
    /// </summary>
    Modify = 1
}
