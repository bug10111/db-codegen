using System.Windows;
using DbCodeGen.App.Services;
using DbCodeGen.App.ViewModels;

namespace DbCodeGen.App.Views;

/// <summary>
/// 模板包管理窗口，承载模板包列表与导入/复制/导出/删除操作，通过 TemplatePackageManagerViewModel 完成业务闭环。
/// </summary>
public partial class TemplatePackageManagerWindow : Window
{
    private readonly TemplatePackageManagerViewModel _viewModel;

    /// <summary>
    /// 使用指定视图模型构造模板包管理窗口，并在呈现完成后触发列表加载。
    /// </summary>
    /// <param name="viewModel">模板包管理视图模型，承载列表与全部资产操作逻辑。</param>
    /// <exception cref="ArgumentNullException">viewModel 为 null 时抛出。</exception>
    public TemplatePackageManagerWindow(TemplatePackageManagerViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;

        // 包列表挂接拖拽排序：落位索引经拖拽辅助计算后交由视图模型重排并持久化
        ListViewDragDropHelper.Attach(PackageListView, OnPackageDrop);
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 包列表拖拽落位回调，把源索引与目标索引交给视图模型执行重排与顺序持久化。
    /// </summary>
    /// <param name="sourceIndex">被拖拽包当前索引。</param>
    /// <param name="targetIndex">落位目标索引。</param>
    private void OnPackageDrop(int sourceIndex, int targetIndex)
    {
        _viewModel.MovePackage(sourceIndex, targetIndex);
    }

    /// <summary>
    /// 窗口呈现完成后异步加载模板包列表，仅触发一次后即解除订阅，避免重复加载。
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync();
    }
}
