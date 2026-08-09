using System.Collections.ObjectModel;
using DbCodeGen.Core.Ai;

namespace DbCodeGen.App.ViewModels;

/// <summary>
/// 「AI 模板助手」窗口级共享参考文件上下文（App.Ai）：写模板与改模板两个 Tab 共读共改的窗口级内存状态，
/// 由宿主视图模型持有，两个 Tab 的 UI 均绑定同一 ReferenceFiles 集合。
/// 内容快照为不可信输入的文本快照，仅内存短周期、注入本次对话提示词，不写盘、不进日志、窗口关闭即弃不持久化。
/// </summary>
public sealed class AiAssistantSharedContext
{
    /// <summary>
    /// 当前参考文件清单，两个 Tab 的 UI 均绑定此集合；集合变更由宿主视图模型驱动摘要与命令可用性刷新。
    /// </summary>
    public ObservableCollection<AiReferenceFileItem> ReferenceFiles { get; } = new();

    /// <summary>
    /// 清单总字节数，随添加/移除/清空实时更新，供限制校验与共享栏摘要展示。
    /// </summary>
    public long TotalSizeBytes { get; private set; }

    /// <summary>
    /// 校验通过后整体加入参考文件清单：逐文件追加到集合并累加总大小，保证一次调用内集合与总大小一致。
    /// </summary>
    /// <param name="items">已通过校验的参考文件项，写/改两 Tab 均按此入口加入。</param>
    public void AddItems(IReadOnlyList<AiReferenceFileItem> items)
    {
        if (items is null || items.Count == 0)
        {
            return;
        }

        foreach (AiReferenceFileItem item in items)
        {
            ReferenceFiles.Add(item);
        }

        TotalSizeBytes += items.Sum(item => item.SizeBytes);
    }

    /// <summary>
    /// 移除单个参考文件项并同步扣减总大小；项不在清单中时静默忽略。
    /// </summary>
    /// <param name="item">待移除的参考文件项。</param>
    public void RemoveItem(AiReferenceFileItem item)
    {
        if (item is null || !ReferenceFiles.Remove(item))
        {
            return;
        }

        TotalSizeBytes -= item.SizeBytes;
    }

    /// <summary>
    /// 清空全部参考文件项并复位总大小，供用户快速重建参考文件集合。
    /// </summary>
    public void Clear()
    {
        ReferenceFiles.Clear();
        TotalSizeBytes = 0;
    }

    /// <summary>
    /// 返回参考文件内容快照，供发送写模板/改模板请求时构造请求与按 F04 限制发送时复核。
    /// </summary>
    /// <returns>当前清单的不可变快照，不持有对共享集合的引用。</returns>
    public IReadOnlyList<AiReferenceFileItem> Snapshot()
    {
        return ReferenceFiles.ToList();
    }
}
