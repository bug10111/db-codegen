using DbCodeGen.Core.Templates.Packages;

namespace DbCodeGen.McpServer;

/// <summary>
/// 工具调用失败的结构化错误载体：MCP 工具内可预期的领域异常统一映射为本结果返回给调用方，
/// 保证单次调用失败不终止 stdio 会话，错误信息可诊断且不携带敏感字段。
/// </summary>
/// <param name="IsError">恒为 true，供 MCP 客户端程序化识别失败应答。</param>
/// <param name="ErrorType">错误分类标识，取值与 ToolError.From 映射一致：InvalidArgument / TemplatePackageError / Cancelled / InternalError。</param>
/// <param name="Message">人类可读错误信息，来自 Core 服务的脱敏异常消息或参数校验消息。</param>
internal sealed record ToolErrorResult(bool IsError, string ErrorType, string Message)
{
    /// <summary>
    /// 构造失败应答的固定错误标记值。
    /// </summary>
    public const bool ErrorMarker = true;
}

/// <summary>
/// 工具异常到结构化错误结果的映射器：按异常类型归类错误标识；
/// 归类基于异常类型而非消息文本，未覆盖类型统一归 InternalError。
/// </summary>
internal static class ToolError
{
/// <summary>
/// 将工具执行异常映射为结构化错误应答；requestToken 为本次请求关联的取消令牌，
/// 用于区分「调用方主动取消」与「Core 连接 10 秒超时兜底触发的链式取消」两类同形态异常。
/// </summary>
/// <param name="exception">工具执行中抛出的异常。</param>
/// <param name="requestToken">请求关联取消令牌；未置位时收到取消异常即判内部超时。</param>
/// <returns>携带错误分类与消息的结构化错误结果。</returns>
public static ToolErrorResult From(Exception exception, CancellationToken? requestToken = null) => exception switch
{
    // 参数非法：路径穿越、空参数、非法枚举值等入参问题
    ArgumentException => new ToolErrorResult(ToolErrorResult.ErrorMarker, "InvalidArgument", exception.Message),
    // 模板包域错误：包不存在、清单校验失败、路径越界等（TemplatePackageException 继承自 Exception）
    TemplatePackageException => new ToolErrorResult(ToolErrorResult.ErrorMarker, "TemplatePackageError", exception.Message),
    // 调用方未取消却收到取消异常：来源为 Core 连接的 10 秒超时兜底（内部链式取消令牌触发），
    // 归类超时并给出可诊断文案，与桌面端测试连接的超时反馈语义对齐
    OperationCanceledException when requestToken is null || !requestToken.Value.IsCancellationRequested =>
        new ToolErrorResult(ToolErrorResult.ErrorMarker, "Timeout", "连接超时（10 秒兜底触发），请检查主机地址、端口与网络后重试。"),
    // 调用方主动取消：请求关联取消令牌已置位，结构化告知取消
    OperationCanceledException => new ToolErrorResult(ToolErrorResult.ErrorMarker, "Cancelled", "操作已取消。"),
    // 其余异常统一归类内部错误，消息经 Core 层脱敏后透出
    _ => new ToolErrorResult(ToolErrorResult.ErrorMarker, "InternalError", exception.Message)
};
}
