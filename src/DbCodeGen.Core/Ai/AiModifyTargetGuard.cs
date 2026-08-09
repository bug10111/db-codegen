namespace DbCodeGen.Core.Ai;

/// <summary>
/// AI 改模板「应用到编辑器」目标一致性守卫的纯比对决策（Core.Ai）：只做两个布尔比对决策，
/// 不依赖 WPF/视图模型/对话框等 App 层类型，供单元测试直接覆盖；
/// 改模板 Tab 视图模型在应用 AI 结果前调用本类，比对通过才整体替换编辑器文本。
/// 守卫一目标文件比对：发送时包名+相对路径与当前②区目标一致才可应用，防跨文件误替换；
/// 守卫二内容一致性确认：发送后②区编辑器内容被手动编辑时需二次确认，确认才覆盖。
/// </summary>
public static class AiModifyTargetGuard
{
    /// <summary>
    /// 判断目标文件是否已变化：发送时快照的包名+相对路径与当前②区目标不一致返回 true，
    /// 应用方应拒绝应用并提示；未打开文件按空值参与比对，与非空发送快照不一致即判定已变化。
    /// </summary>
    /// <param name="currentPackageName">当前②区目标包名，未打开文件可为 null。</param>
    /// <param name="currentFilePath">当前②区目标文件相对包根路径，未打开文件可为 null。</param>
    /// <param name="sentPackageName">发送时快照的包名。</param>
    /// <param name="sentFilePath">发送时快照的相对包根路径。</param>
    /// <returns>目标已变化返回 true，目标一致返回 false。</returns>
    public static bool IsTargetChanged(
        string? currentPackageName,
        string? currentFilePath,
        string sentPackageName,
        string sentFilePath)
    {
        return !string.Equals(currentPackageName ?? string.Empty, sentPackageName, StringComparison.Ordinal)
            || !string.Equals(currentFilePath ?? string.Empty, sentFilePath, StringComparison.Ordinal);
    }

    /// <summary>
    /// 判断编辑器内容是否已在发送后被修改：当前②区编辑器内容与发送时快照不一致返回 true，
    /// 应用方应弹二次确认，确认才覆盖；空值按空文本参与比对。
    /// </summary>
    /// <param name="currentEditorText">当前②区编辑器内容。</param>
    /// <param name="sentContentSnapshot">发送时编辑器内容快照。</param>
    /// <returns>内容已修改返回 true，内容一致返回 false。</returns>
    public static bool IsContentChanged(string? currentEditorText, string? sentContentSnapshot)
    {
        return !string.Equals(currentEditorText ?? string.Empty, sentContentSnapshot ?? string.Empty, StringComparison.Ordinal);
    }
}
