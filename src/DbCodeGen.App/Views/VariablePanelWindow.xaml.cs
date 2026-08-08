using System.Windows;
using System.Windows.Input;
using DbCodeGen.App.ViewModels;

namespace DbCodeGen.App.Views;

/// <summary>
/// 变量面板窗口，展示当前表元数据变量树，双击叶子节点将表达式插入到模板编辑器光标处。
/// 变量树与预览渲染共用同一当前表，表达式严格对齐 02 字段契约。
/// </summary>
public partial class VariablePanelWindow : Window
{
    /// <summary>
    /// 模板编辑器视图模型，变量插入经其请求入口桥接到编辑器光标。
    /// </summary>
    private readonly TemplateViewModel _templateViewModel;

    /// <summary>
    /// 使用变量面板视图模型与模板编辑器视图模型构造窗口。
    /// </summary>
    /// <param name="viewModel">变量面板视图模型，承载当前表变量表达式树。</param>
    /// <param name="templateViewModel">模板编辑器视图模型，接收变量插入请求。</param>
    /// <exception cref="ArgumentNullException">任一依赖参数为 null 时抛出。</exception>
    public VariablePanelWindow(VariablePanelViewModel viewModel, TemplateViewModel templateViewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(templateViewModel);

        InitializeComponent();
        DataContext = viewModel;
        _templateViewModel = templateViewModel;
    }

    /// <summary>
    /// 双击变量树节点：选中叶子节点且携带表达式时请求插入到模板编辑器光标处，分组节点无表达式忽略。
    /// </summary>
    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (VariableTree.SelectedItem is VariableTreeNode node && node.Expression is not null)
        {
            _templateViewModel.RequestInsertVariable(node.Expression);
        }
    }
}
