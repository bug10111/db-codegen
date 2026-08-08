namespace DbCodeGen.App.Services;

/// <summary>
/// 跨窗口二次确认服务，统一为是/否确认框的异步契约，供覆盖写盘确认、脏文档切换等场景复用。
/// </summary>
public interface IConfirmDialogService
{
    /// <summary>
    /// 弹出是/否确认框并返回用户选择。
    /// </summary>
    /// <param name="title">确认框标题。</param>
    /// <param name="message">确认正文。</param>
    /// <returns>用户确认返回 true，否则返回 false。</returns>
    Task<bool> ConfirmAsync(string title, string message);
}
