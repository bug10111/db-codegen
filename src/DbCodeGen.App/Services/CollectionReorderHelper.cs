using System.Collections.ObjectModel;

namespace DbCodeGen.App.Services;

/// <summary>
/// ObservableCollection 双向重排辅助，统一承载上移、下移与拖拽落位三类排序操作。
/// 全部操作基于 <see cref="ObservableCollection{T}.Move(int,int)"/> 完成双向移动，
/// 触发绑定 ListView 的增量刷新并保持被移动项实例不变，绑定 SelectedItem 的选中项随移动自动跟随。
/// 供②区文件树与模板包管理窗口包列表两处排序界面共用。
/// </summary>
public static class CollectionReorderHelper
{
    /// <summary>
    /// 将指定项上移一位，已位于首位时不移动。
    /// </summary>
    /// <typeparam name="T">集合项类型。</typeparam>
    /// <param name="items">待重排的集合，须为绑定 ListView 的可写 ObservableCollection。</param>
    /// <param name="index">待移动项当前索引。</param>
    /// <returns>移动后该项的新索引；位于首位未移动时返回原索引。</returns>
    /// <exception cref="ArgumentNullException">items 为 null 时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">index 超出集合有效范围时抛出。</exception>
    public static int MoveUp<T>(ObservableCollection<T> items, int index)
    {
        ArgumentNullException.ThrowIfNull(items);
        return MoveTo(items, index, index - 1);
    }

    /// <summary>
    /// 将指定项下移一位，已位于末位时不移动。
    /// </summary>
    /// <typeparam name="T">集合项类型。</typeparam>
    /// <param name="items">待重排的集合，须为绑定 ListView 的可写 ObservableCollection。</param>
    /// <param name="index">待移动项当前索引。</param>
    /// <returns>移动后该项的新索引；位于末位未移动时返回原索引。</returns>
    /// <exception cref="ArgumentNullException">items 为 null 时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">index 超出集合有效范围时抛出。</exception>
    public static int MoveDown<T>(ObservableCollection<T> items, int index)
    {
        ArgumentNullException.ThrowIfNull(items);
        return MoveTo(items, index, index + 1);
    }

    /// <summary>
    /// 将指定项移动到目标位置，目标位置为移动完成后该项的最终索引（0 起）。
    /// 目标索引超界时收敛到最近边界（首位或末位）；集合项不足两项或源目标相同时不移动。
    /// </summary>
    /// <typeparam name="T">集合项类型。</typeparam>
    /// <param name="items">待重排的集合，须为绑定 ListView 的可写 ObservableCollection。</param>
    /// <param name="sourceIndex">被移动项当前索引。</param>
    /// <param name="targetIndex">目标最终索引。</param>
    /// <returns>移动后该项的新索引，未移动时返回源索引。</returns>
    /// <exception cref="ArgumentNullException">items 为 null 时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">sourceIndex 超出集合有效范围时抛出。</exception>
    public static int MoveTo<T>(ObservableCollection<T> items, int sourceIndex, int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(items);
        ThrowIfSourceOutOfRange(items, sourceIndex);

        // 单项或空集合不存在可移动空间，直接返回源索引
        if (items.Count <= 1)
        {
            return sourceIndex;
        }

        // 目标索引收敛到有效范围后与源索引相同则无需移动，避免触发无意义的 Move 事件
        int clampedTarget = Math.Clamp(targetIndex, 0, items.Count - 1);
        if (clampedTarget == sourceIndex)
        {
            return sourceIndex;
        }

        items.Move(sourceIndex, clampedTarget);
        return clampedTarget;
    }

    /// <summary>
    /// 判定指定索引的项是否可上移：非首位即可上移。
    /// </summary>
    /// <param name="index">项当前索引，无选中项时传负数。</param>
    /// <returns>可上移返回 true，位于首位或索引无效返回 false。</returns>
    public static bool CanMoveUp(int index) => index > 0;

    /// <summary>
    /// 判定指定索引的项是否可下移：非末位即可下移。
    /// </summary>
    /// <param name="index">项当前索引，无选中项时传负数。</param>
    /// <param name="count">集合元素总数。</param>
    /// <returns>可下移返回 true，位于末位或索引无效返回 false。</returns>
    public static bool CanMoveDown(int index, int count) => index >= 0 && index < count - 1;

    /// <summary>
    /// 校验源索引在集合范围内，越界时抛参数异常。
    /// </summary>
    /// <typeparam name="T">集合项类型。</typeparam>
    /// <param name="items">待重排的集合。</param>
    /// <param name="sourceIndex">被移动项当前索引。</param>
    /// <exception cref="ArgumentOutOfRangeException">sourceIndex 超出集合有效范围时抛出。</exception>
    private static void ThrowIfSourceOutOfRange<T>(ObservableCollection<T> items, int sourceIndex)
    {
        if (sourceIndex < 0 || sourceIndex >= items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceIndex), sourceIndex, "源索引超出集合范围。");
        }
    }
}
