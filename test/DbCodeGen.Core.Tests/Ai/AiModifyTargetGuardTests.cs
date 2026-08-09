using DbCodeGen.Core.Ai;

namespace DbCodeGen.Core.Tests.Ai;

/// <summary>
/// AiModifyTargetGuard 目标一致性守卫纯比对决策单元测试，覆盖守卫一目标文件比对（包名+相对路径 vs 发送快照）
/// 与守卫二内容一致性比对（当前编辑器内容 vs 发送快照），含 null/空值边界。
/// 本类只做比对决策，不涉及 WPF/视图模型/对话框；改模板 Tab VM 的守卫编排（弹窗/应用）不在此覆盖。
/// </summary>
public sealed class AiModifyTargetGuardTests
{
    /// <summary>
    /// 目标文件包名与发送快照不一致应判定目标已变化，拒绝应用。
    /// </summary>
    [Fact]
    public void IsTargetChanged_PackageNameDiffers_ReturnsTrue()
    {
        bool changed = AiModifyTargetGuard.IsTargetChanged(
            "user-mybatis", "entity.java.scriban", "java-mybatis-plus", "entity.java.scriban");

        Assert.True(changed);
    }

    /// <summary>
    /// 目标文件相对路径与发送快照不一致应判定目标已变化，拒绝应用。
    /// </summary>
    [Fact]
    public void IsTargetChanged_FilePathDiffers_ReturnsTrue()
    {
        bool changed = AiModifyTargetGuard.IsTargetChanged(
            "java-mybatis-plus", "service.java.scriban", "java-mybatis-plus", "entity.java.scriban");

        Assert.True(changed);
    }

    /// <summary>
    /// 目标文件包名与相对路径均与发送快照一致应判定目标未变化，直接通过。
    /// </summary>
    [Fact]
    public void IsTargetChanged_TargetUnchanged_ReturnsFalse()
    {
        bool changed = AiModifyTargetGuard.IsTargetChanged(
            "java-mybatis-plus", "entity.java.scriban", "java-mybatis-plus", "entity.java.scriban");

        Assert.False(changed);
    }

    /// <summary>
    /// 当前未打开文件（包名/路径为 null）而发送快照非空时应判定目标已变化，拒绝应用。
    /// </summary>
    [Fact]
    public void IsTargetChanged_CurrentNullWithNonEmptySnapshot_ReturnsTrue()
    {
        bool changed = AiModifyTargetGuard.IsTargetChanged(
            null, null, "java-mybatis-plus", "entity.java.scriban");

        Assert.True(changed);
    }

    /// <summary>
    /// 发送快照为空且当前目标也为空时为空值边界，应判定目标未变化。
    /// </summary>
    [Fact]
    public void IsTargetChanged_BothEmptySnapshot_ReturnsFalse()
    {
        bool changed = AiModifyTargetGuard.IsTargetChanged(
            null, null, string.Empty, string.Empty);

        Assert.False(changed);
    }

    /// <summary>
    /// 当前目标与发送快照仅路径分隔符大小写不同应按字符串精确比对判定已变化，防误导入不同文件。
    /// </summary>
    [Fact]
    public void IsTargetChanged_PathCaseDiffers_ReturnsTrue()
    {
        bool changed = AiModifyTargetGuard.IsTargetChanged(
            "java-mybatis-plus", "Entity.java.scriban", "java-mybatis-plus", "entity.java.scriban");

        Assert.True(changed);
    }

    /// <summary>
    /// 编辑器内容与发送时快照一致应判定内容未修改，直接通过无需确认。
    /// </summary>
    [Fact]
    public void IsContentChanged_ContentUnchanged_ReturnsFalse()
    {
        bool changed = AiModifyTargetGuard.IsContentChanged(
            "class {{table.className}} {}", "class {{table.className}} {}");

        Assert.False(changed);
    }

    /// <summary>
    /// 编辑器内容已在发送后被手动修改应判定需二次确认，确认才覆盖。
    /// </summary>
    [Fact]
    public void IsContentChanged_ContentEdited_ReturnsTrue()
    {
        bool changed = AiModifyTargetGuard.IsContentChanged(
            "class {{table.className}} {\n  private Long id;\n}", "class {{table.className}} {}");

        Assert.True(changed);
    }

    /// <summary>
    /// 当前内容为 null 而发送快照非空应按空文本参与比对，判定需二次确认。
    /// </summary>
    [Fact]
    public void IsContentChanged_CurrentNullWithNonEmptySnapshot_ReturnsTrue()
    {
        bool changed = AiModifyTargetGuard.IsContentChanged(null, "class {{table.className}} {}");

        Assert.True(changed);
    }

    /// <summary>
    /// 当前内容与发送快照均为 null 或空时应判定内容一致，避免误弹确认。
    /// </summary>
    [Fact]
    public void IsContentChanged_BothNullOrEmpty_ReturnsFalse()
    {
        Assert.False(AiModifyTargetGuard.IsContentChanged(null, null));
        Assert.False(AiModifyTargetGuard.IsContentChanged(string.Empty, string.Empty));
    }
}
