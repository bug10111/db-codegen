namespace DbCodeGen.Core.Config;

/// <summary>
/// 主窗口布局记忆模型，记录上区三栏列宽与纵向行高，供启动恢复与关闭回写。
/// 各维度可为 null，表示使用默认布局未作调整。
/// </summary>
public sealed class MainLayoutState
{
    /// <summary>
    /// ①表列表区列宽像素值，null 表示使用默认宽度。
    /// </summary>
    public double? TableColumnWidth { get; set; }

    /// <summary>
    /// ②模板区列宽像素值，null 表示使用默认宽度。
    /// </summary>
    public double? TemplateColumnWidth { get; set; }

    /// <summary>
    /// 上区三栏整体行高像素值，null 表示使用默认高度。
    /// </summary>
    public double? TopRowHeight { get; set; }

    /// <summary>
    /// ④生成栏底部日志面板高度像素值，null 表示使用默认高度。
    /// </summary>
    public double? LogPanelHeight { get; set; }
}
