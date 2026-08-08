using System.Windows;
using DbCodeGen.App.ViewModels;

namespace DbCodeGen.App.Views;

/// <summary>
/// 类型映射窗口，以可编辑表格展示全局类型映射表，承载增删改、恢复默认、导入导出与保存。
/// 数据读写经 TypeMappingViewModel 完成，保存后写入配置即时生效。
/// </summary>
public partial class TypeMappingWindow : Window
{
    private readonly TypeMappingViewModel _viewModel;

    /// <summary>
    /// 使用指定视图模型构造类型映射窗口，并绑定为数据上下文。
    /// </summary>
    /// <param name="viewModel">类型映射视图模型，承载映射表加载、编辑与保存逻辑。</param>
    /// <exception cref="ArgumentNullException">viewModel 为 null 时抛出。</exception>
    public TypeMappingWindow(TypeMappingViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
    }

    /// <summary>
    /// 点击关闭直接关闭窗口，未保存的编辑不写入配置。
    /// </summary>
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
