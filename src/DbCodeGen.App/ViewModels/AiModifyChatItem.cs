using CommunityToolkit.Mvvm.ComponentModel;

namespace DbCodeGen.App.ViewModels;

/// <summary>
/// 改模板对话会话中单条消息的角色：用户指令 / AI 结果，驱动气泡样式与操作按钮可用性。
/// </summary>
public enum AiModifyChatRole
{
    /// <summary>
    /// 用户发送的修改指令。
    /// </summary>
    User,

    /// <summary>
    /// AI 返回的结果（成功为完整新文件，失败为错误清单）。
    /// </summary>
    Ai
}

/// <summary>
/// 改模板 Tab 对话消息展示项：角色驱动气泡样式，AI 消息携带可应用结果、完整内容与展开折叠状态。
/// 内容仅用于会话气泡展示，不写盘、不进日志。
/// </summary>
public sealed partial class AiModifyChatItem : ObservableObject
{
    /// <summary>
    /// 创建改模板对话消息项。
    /// </summary>
    /// <param name="role">消息角色，驱动气泡样式与操作按钮。</param>
    /// <param name="content">展示文本（用户指令原文 / AI 结果摘要或错误清单）。</param>
    /// <param name="isResultReady">AI 消息是否可应用（成功返回完整新文件时为 true，驱动「应用到编辑器」按钮可用）。</param>
    /// <param name="newContent">AI 返回的完整新文件，供「查看完整内容」展开与「应用到编辑器」提交；用户消息为 null。</param>
    public AiModifyChatItem(AiModifyChatRole role, string content, bool isResultReady, string? newContent)
    {
        Role = role;
        Content = content;
        IsResultReady = isResultReady;
        NewContent = newContent;
        Timestamp = DateTimeOffset.Now;
    }

    /// <summary>
    /// 消息角色，驱动气泡样式。
    /// </summary>
    public AiModifyChatRole Role { get; }

    /// <summary>
    /// 展示文本（用户指令原文 / AI 结果摘要或错误清单）。
    /// </summary>
    public string Content { get; }

    /// <summary>
    /// AI 消息是否可应用：成功返回完整新文件时为 true，驱动「应用到编辑器」按钮可见与可用。
    /// </summary>
    public bool IsResultReady { get; }

    /// <summary>
    /// AI 返回的完整新文件，供「查看完整内容」展开与「应用到编辑器」提交；用户消息为 null。
    /// </summary>
    public string? NewContent { get; }

    /// <summary>
    /// 消息时间戳。
    /// </summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>
    /// 完整内容展开状态，由「查看完整内容」命令切换，驱动气泡内全文展开/折叠。
    /// </summary>
    [ObservableProperty]
    private bool _isFullContentVisible;

    /// <summary>
    /// 是否 AI 消息，驱动气泡样式切换（AI 靠右浅绿、用户靠左浅蓝）。
    /// </summary>
    public bool IsAiMessage => Role == AiModifyChatRole.Ai;

    /// <summary>
    /// 摘要文本：AI 成功消息展示行数统计摘要，其余展示原文截断预览，便于气泡内快速核对。
    /// </summary>
    public string SummaryText
    {
        get
        {
            if (IsAiMessage && IsResultReady && NewContent is not null)
            {
                // 按换行符统计行数，末尾换行不额外计入，避免结果文本以换行结尾时行数虚增一行
                int lineCount = NewContent.Count(character => character == '\n') + 1;
                return $"已按指令修改，共 {lineCount} 行";
            }

            return BuildPreview(Content, 300);
        }
    }

    /// <summary>
    /// 完整内容：AI 成功消息为完整新文件，其余为展示文本原文，供「查看完整内容」展开全文。
    /// </summary>
    public string FullContent => NewContent ?? Content;

    /// <summary>
    /// 截断长文本为指定字符数，尾部追加省略号，供气泡内默认预览。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <param name="maxChars">截断上限字符数。</param>
    /// <returns>截断后的预览文本。</returns>
    private static string BuildPreview(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text ?? string.Empty;
        }

        return $"{text[..maxChars]}…";
    }
}
