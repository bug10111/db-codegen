namespace DbCodeGen.App.Services;

/// <summary>
/// 跨窗口消息提示服务，统一展示信息与错误提示框，供各窗口与视图模型复用。
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// 展示信息提示框。
    /// </summary>
    /// <param name="message">提示正文。</param>
    /// <param name="title">提示框标题，默认“提示”。</param>
    void ShowInfo(string message, string title = "提示");

    /// <summary>
    /// 展示错误提示框。
    /// </summary>
    /// <param name="message">错误正文，不得包含明文密钥等敏感信息。</param>
    /// <param name="title">错误框标题，默认“错误”。</param>
    void ShowError(string message, string title = "错误");
}
