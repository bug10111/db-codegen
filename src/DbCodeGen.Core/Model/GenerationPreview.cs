namespace DbCodeGen.Core.Model;

/// <summary>
/// dry-run 清单结果，承载全部待写条目与按动作分类的计数。
/// 覆盖计数大于 0 时，生成阶段必经用户确认后才可写盘。
/// </summary>
public sealed class GenerationPreview
{
    /// <summary>
    /// 使用完整字段构造 dry-run 清单结果，未映射类型清单默认为空。
    /// </summary>
    /// <param name="entries">全部待写条目。</param>
    /// <param name="newCount">新增条目数。</param>
    /// <param name="overwriteCount">覆盖条目数，大于 0 时生成须确认。</param>
    /// <param name="skipCount">跳过条目数。</param>
    /// <exception cref="ArgumentNullException">entries 为 null 时抛出。</exception>
    public GenerationPreview(
        IReadOnlyList<GenerationFileEntry> entries,
        int newCount,
        int overwriteCount,
        int skipCount)
        : this(entries, newCount, overwriteCount, skipCount, Array.Empty<UnmappedTypeInfo>())
    {
    }

    /// <summary>
    /// 使用完整字段与未映射类型清单构造 dry-run 清单结果。
    /// </summary>
    /// <param name="entries">全部待写条目。</param>
    /// <param name="newCount">新增条目数。</param>
    /// <param name="overwriteCount">覆盖条目数，大于 0 时生成须确认。</param>
    /// <param name="skipCount">跳过条目数。</param>
    /// <param name="unmappedTypes">生成预检发现的未映射类型清单，可为空。</param>
    /// <exception cref="ArgumentNullException">entries 或 unmappedTypes 为 null 时抛出。</exception>
    public GenerationPreview(
        IReadOnlyList<GenerationFileEntry> entries,
        int newCount,
        int overwriteCount,
        int skipCount,
        IReadOnlyList<UnmappedTypeInfo> unmappedTypes)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(unmappedTypes);
        Entries = entries;
        NewCount = newCount;
        OverwriteCount = overwriteCount;
        SkipCount = skipCount;
        UnmappedTypes = unmappedTypes;
    }

    /// <summary>
    /// 全部待写条目，按表与模板文件的笛卡尔积顺序排列。
    /// </summary>
    public IReadOnlyList<GenerationFileEntry> Entries { get; }

    /// <summary>
    /// 新增条目数。
    /// </summary>
    public int NewCount { get; }

    /// <summary>
    /// 覆盖条目数，大于 0 时生成阶段必经用户确认。
    /// </summary>
    public int OverwriteCount { get; }

    /// <summary>
    /// 跳过条目数，内容相同未写盘。
    /// </summary>
    public int SkipCount { get; }

    /// <summary>
    /// 生成预检发现的未映射类型清单，供界面弹窗提示用户补映射；全部命中时为空列表。
    /// </summary>
    public IReadOnlyList<UnmappedTypeInfo> UnmappedTypes { get; }
}
