using CommunityToolkit.Mvvm.ComponentModel;
using DbCodeGen.Core.Model;

namespace DbCodeGen.App.ViewModels;

/// <summary>
/// 表清单行视图模型，承载单张表的展示字段与勾选状态，供主窗口①区 DataGrid 绑定。
/// 勾选状态变化时经回调通知宿主视图模型刷新勾选集合计数，保证批量操作与单行勾选计数一致。
/// </summary>
public sealed partial class TableRowViewModel : ObservableObject
{
    /// <summary>
    /// 勾选状态变化时的宿主通知回调，批量选择与单行勾选共用。
    /// </summary>
    private readonly Action<TableRowViewModel>? _isSelectedChangedCallback;

    /// <summary>
    /// 以表元数据与勾选变化回调构造行视图模型。
    /// </summary>
    /// <param name="table">表元数据实体，表清单阶段不含列信息。</param>
    /// <param name="isSelectedChangedCallback">勾选状态变化时的宿主通知回调，可为 null。</param>
    /// <exception cref="ArgumentNullException">table 为 null 时抛出。</exception>
    public TableRowViewModel(TableInfo table, Action<TableRowViewModel>? isSelectedChangedCallback)
    {
        Table = table ?? throw new ArgumentNullException(nameof(table));
        _isSelectedChangedCallback = isSelectedChangedCallback;
    }

    /// <summary>
    /// 表元数据实体，下游消费方读取表名、类名等字段。
    /// </summary>
    public TableInfo Table { get; }

    /// <summary>
    /// 表名，DataGrid 表名列绑定源。
    /// </summary>
    public string RawName => Table.RawName;

    /// <summary>
    /// 表注释，DataGrid 注释列绑定源，无注释时为 null。
    /// </summary>
    public string? Comment => Table.Comment;

    /// <summary>
    /// 是否勾选参与生成，单行勾选与批量操作共用，作用于全部已加载表。
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// 勾选状态变更后通知宿主刷新勾选集合计数。
    /// </summary>
    /// <param name="value">变更后的勾选状态。</param>
    partial void OnIsSelectedChanged(bool value)
    {
        // 每次勾选变化都通知宿主，宿主维护增量计数保持状态栏勾选数量实时准确
        _isSelectedChangedCallback?.Invoke(this);
    }
}
