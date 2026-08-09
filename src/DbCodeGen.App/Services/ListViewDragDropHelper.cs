using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DbCodeGen.App.Services;

/// <summary>
/// WPF ListView 拖拽排序辅助，为绑定列表的 ListView 挂接"按下记录起点 → 移动启动拖拽 →
/// 悬停落位预览 → 释放回调落位索引"四段原生拖拽流程。
/// 采用 WPF 原生拖拽能力（<see cref="DragDrop.DoDragDrop"/>），不引入第三方库；
/// 与 ListView 既有右键菜单、行内勾选等点击事件互不冲突；同一 ListView 只应挂接一次。
/// 落位回调携带源索引与目标最终索引，由调用方执行集合重排与顺序持久化。
/// </summary>
public static class ListViewDragDropHelper
{
    /// <summary>
    /// 拖拽载荷自定义数据格式名，用于识别本辅助发起的拖拽，避免响应外部来源拖拽。
    /// </summary>
    private const string ItemDataFormat = "DbCodeGen.ListViewDragDrop.Item";

    /// <summary>
    /// 落位预览高亮画刷，拖拽悬停在目标行时临时覆盖行背景，释放或离开后恢复。
    /// </summary>
    private static readonly Brush DropTargetBrush = CreateDropTargetBrush();

    /// <summary>
    /// 为指定 ListView 挂接拖拽排序事件链，落位索引计算后经回调交由调用方执行重排与持久化。
    /// </summary>
    /// <param name="listView">待挂接拖拽排序的 ListView，须绑定可写 ObservableCollection。</param>
    /// <param name="onDrop">落位回调，参数依次为源索引与目标最终索引；原位或无效落位时不触发。</param>
    /// <exception cref="ArgumentNullException">listView 为 null 时抛出。</exception>
    public static void Attach(ListView listView, Action<int, int>? onDrop)
    {
        ArgumentNullException.ThrowIfNull(listView);

        // 将列表设为可落位目标，否则 DragOver/Drop 事件不会向列表派发，拖拽排序静默失效
        listView.AllowDrop = true;

        // 拖拽起点与预览目标状态按本次挂接闭包捕获，各 ListView 互不共享，
        // 主窗口与模板包管理窗口同时打开时各自独立，互不干扰
        object? dragItem = null;
        Point dragStartPoint = default;
        bool isDragArmed = false;
        bool isDragActive = false;
        ListViewItem? previewTarget = null;

        // 清除落位预览高亮并复位预览目标，恢复目标行默认背景
        void ClearPreviewTarget()
        {
            if (previewTarget is null)
            {
                return;
            }

            previewTarget.ClearValue(Control.BackgroundProperty);
            previewTarget = null;
        }

        // 更新落位预览高亮到指定目标行，先清除上一次高亮
        void UpdatePreviewTarget(ListViewItem target)
        {
            if (ReferenceEquals(previewTarget, target))
            {
                return;
            }

            ClearPreviewTarget();
            previewTarget = target;
            target.Background = DropTargetBrush;
        }

        listView.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // 每次按下先复位上一轮起点状态，避免残留标记在后续移动中误启拖拽
            isDragArmed = false;
            dragItem = null;

            if (e.OriginalSource is not DependencyObject sourceElement)
            {
                return;
            }

            // 按下点位于行内勾选框时仅走勾选切换，不记录拖拽起点，保证勾选与拖拽互不干扰
            if (HasVisualParent<CheckBox>(sourceElement))
            {
                return;
            }

            ListViewItem? row = FindItemContainer(listView, sourceElement);
            if (row is null)
            {
                return;
            }

            object? item = listView.ItemContainerGenerator.ItemFromContainer(row);
            if (item is null || item == DependencyProperty.UnsetValue)
            {
                return;
            }

            dragItem = item;
            dragStartPoint = e.GetPosition(listView);
            isDragArmed = true;
        };

        listView.MouseMove += (_, e) =>
        {
            // 拖拽进行中直接忽略后续鼠标移动，避免误清拖拽起点导致落位索引失效
            if (isDragActive)
            {
                return;
            }

            // 未按住左键或未记录起点时复位并直接返回，保持 ListView 既有点击与框选行为
            if (!isDragArmed || e.LeftButton != MouseButtonState.Pressed)
            {
                isDragArmed = false;
                dragItem = null;
                return;
            }

            Vector offset = e.GetPosition(listView) - dragStartPoint;
            if (Math.Abs(offset.X) <= SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(offset.Y) <= SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            // 超过拖拽阈值启动原生拖拽，载荷以自定义格式携带被拖项，供落位时校验与定位
            isDragArmed = false;
            isDragActive = true;
            try
            {
                DataObject data = new();
                data.SetData(ItemDataFormat, dragItem);
                DragDrop.DoDragDrop(listView, data, DragDropEffects.Move);
            }
            catch (Exception exception)
            {
                // 原生拖拽会话偶发失败（多由系统级拖放异常引起）按拖拽取消处理，
                // 排序为最佳努力交互，不因一次失败拖拽中断应用运行，仅记录上下文供排查
                System.Diagnostics.Trace.WriteLine($"拖拽会话失败：{exception.Message}");
            }
            finally
            {
                isDragActive = false;
                dragItem = null;
                ClearPreviewTarget();
            }
        };

        listView.DragOver += (_, e) =>
        {
            // 非本辅助发起的拖拽直接拒绝，避免响应外部来源数据
            if (!IsOurDrag(e.Data))
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            ListViewItem? target = FindDropTarget(listView, e);
            if (target is null)
            {
                // 悬停位置不在行内时禁止释放并清除预览
                e.Effects = DragDropEffects.None;
                ClearPreviewTarget();
                return;
            }

            // 悬停在被拖项自身行时允许释放但无落位预览，避免误导"原位移动"
            if (ReferenceEquals(listView.ItemContainerGenerator.ItemFromContainer(target), dragItem))
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                ClearPreviewTarget();
                return;
            }

            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            UpdatePreviewTarget(target);
        };

        listView.DragLeave += (_, _) =>
        {
            // 拖拽离开列表区域时清除预览，避免残留高亮
            ClearPreviewTarget();
        };

        listView.Drop += (_, e) =>
        {
            if (!IsOurDrag(e.Data))
            {
                return;
            }

            e.Handled = true;

            ListViewItem? target = FindDropTarget(listView, e);
            if (target is null)
            {
                ClearPreviewTarget();
                return;
            }

            // 源索引按被拖项在列表中的实时位置定位，目标索引取落点行容器索引；
            // 两者相同或任一无效时不触发回调，避免无效落位触发重排与持久化
            int sourceIndex = listView.Items.IndexOf(dragItem);
            int targetIndex = listView.ItemContainerGenerator.IndexFromContainer(target);
            ClearPreviewTarget();

            if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
            {
                return;
            }

            onDrop?.Invoke(sourceIndex, targetIndex);
        };
    }

    /// <summary>
    /// 判断拖拽数据是否由本辅助发起，未携带自定义格式或数据为空时视为外部拖拽。
    /// </summary>
    /// <param name="data">拖拽数据。</param>
    /// <returns>本辅助发起返回 true，否则返回 false。</returns>
    private static bool IsOurDrag(IDataObject? data)
    {
        return data is not null && data.GetDataPresent(ItemDataFormat);
    }

    /// <summary>
    /// 从拖拽事件原始命中元素定位落点行容器，未命中行内返回 null。
    /// </summary>
    /// <param name="listView">承载拖拽的 ListView。</param>
    /// <param name="e">拖拽事件参数。</param>
    /// <returns>落点行容器，未命中行内返回 null。</returns>
    private static ListViewItem? FindDropTarget(ListView listView, DragEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject sourceElement)
        {
            return null;
        }

        return FindItemContainer(listView, sourceElement);
    }

    /// <summary>
    /// 从指定元素沿可视树向上查找 ListView 行容器，越过 ListView 自身仍未命中返回 null。
    /// </summary>
    /// <param name="listView">承载行的 ListView。</param>
    /// <param name="element">命中起点元素。</param>
    /// <returns>命中的行容器，未命中返回 null。</returns>
    private static ListViewItem? FindItemContainer(ListView listView, DependencyObject element)
    {
        DependencyObject? current = element;
        while (current is not null && !ReferenceEquals(current, listView))
        {
            if (current is ListViewItem item)
            {
                return item;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>
    /// 判断指定元素是否位于目标类型可视祖先之下，用于识别行内勾选框区域。
    /// </summary>
    /// <typeparam name="T">目标祖先类型。</typeparam>
    /// <param name="element">命中起点元素。</param>
    /// <returns>位于目标类型祖先之下返回 true，否则返回 false。</returns>
    private static bool HasVisualParent<T>(DependencyObject element) where T : DependencyObject
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is T)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    /// <summary>
    /// 构建并冻结落位预览高亮画刷，冻结后跨线程安全复用。
    /// </summary>
    /// <returns>冻结的浅蓝高亮画刷。</returns>
    private static Brush CreateDropTargetBrush()
    {
        SolidColorBrush brush = new(Color.FromRgb(0xD6, 0xE9, 0xFF));
        brush.Freeze();
        return brush;
    }
}
