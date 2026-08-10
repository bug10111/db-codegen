using CommunityToolkit.Mvvm.ComponentModel;
using DbCodeGen.Core.Templates.Packages;

namespace DbCodeGen.App.ViewModels;

/// <summary>
/// 改模板 Tab「选择要修改的模板」多选面板中的勾选项：包装当前模板包内的单个文件，
/// 暴露相对路径展示文本与勾选状态，勾选状态由视图层 CheckBox 双向绑定。
/// </summary>
public sealed partial class ModifyFileSelectionItem : ObservableObject
{
    /// <summary>
    /// 被包装的包内模板文件运行时信息，作为相对路径等展示字段的事实源。
    /// </summary>
    private readonly TemplateFileInfo _file;

    /// <summary>
    /// 勾选状态，驱动是否参与批量修改；默认由调用方按是否为②区当前文件决定。
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// 使用包内模板文件与初始勾选状态创建选择项。
    /// </summary>
    /// <param name="file">包内模板文件运行时信息，不得为 null。</param>
    /// <param name="isSelected">初始勾选状态，默认未勾选。</param>
    /// <exception cref="ArgumentNullException">file 为 null 时抛出。</exception>
    public ModifyFileSelectionItem(TemplateFileInfo file, bool isSelected = false)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file;
        _isSelected = isSelected;
    }

    /// <summary>
    /// 文件相对包根路径（正斜杠规范化），列表展示与批量请求组装使用。
    /// </summary>
    public string RelativePath => _file.RelativeTemplatePath;
}
