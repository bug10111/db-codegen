namespace DbCodeGen.App.Services;

/// <summary>
/// 跨窗口文本输入提示服务，统一承载带预填默认值的输入对话框，供新建模板包、新建模板文件等需要用户输入文本的场景复用。
/// </summary>
public interface IPromptDialogService
{
    /// <summary>
    /// 弹出带输入框的模态对话框并返回用户输入的文本。
    /// </summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="prompt">输入引导文案，说明期望输入的内容。</param>
    /// <param name="defaultValue">输入框预填默认值，默认空串。</param>
    /// <returns>用户点击确定返回输入文本（可能为空串）；点击取消或关闭窗口返回 null。</returns>
    Task<string?> PromptAsync(string title, string prompt, string defaultValue = "");
}
