namespace DbCodeGen.App.Services;

/// <summary>
/// 目录选择服务，供工作区根、模板搜索目录等路径输入场景选择本地目录，跨窗口复用。
/// </summary>
public interface IFolderPickerService
{
    /// <summary>
    /// 弹出目录选择框并返回用户选中的目录。
    /// </summary>
    /// <param name="initialDirectory">初始定位目录；为空或不存在时由对话框决定起始位置。</param>
    /// <param name="title">选择框标题，默认“选择目录”。</param>
    /// <returns>选中的目录绝对路径；用户取消时返回 null。</returns>
    Task<string?> PickFolderAsync(string? initialDirectory = null, string title = "选择目录");
}
