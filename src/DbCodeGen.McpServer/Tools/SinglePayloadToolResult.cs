using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace DbCodeGen.McpServer.Tools;

/// <summary>
/// 单载荷工具应答共享组装器：声明 outputSchema 的工具（read_template / preview_generation / generate）返回
/// CallToolResult 走 SDK passthrough 分支；若工具以普通对象返回，SDK 缺省 fallback 分支会同时输出
/// 序列化 JSON 文本块与 structuredContent 双份等价载荷，是大回执被宿主结果预算截断的根因。
/// structuredContent 序列化口径 = AIJsonUtilities.DefaultOptions（SDK 结构化应答实际序列化口径），
/// 字段命名与 outputSchema 校验逐字一致；text 块降级为载荷口径与续读指引级摘要说明，不重复输出载荷正文。
/// </summary>
internal static class SinglePayloadToolResult
{
    /// <summary>
    /// 组装单载荷工具应答：structuredContent 承载唯一完整回执，text 块承载调用方提供的摘要说明。
    /// </summary>
    /// <typeparam name="T">回执 DTO 类型。</typeparam>
    /// <param name="payload">强类型回执，仅承载于 structuredContent 一处。</param>
    /// <param name="summaryText">text 块摘要说明：载荷口径与续读指引级说明，不重复载荷正文。</param>
    /// <returns>单载荷 CallToolResult。</returns>
    public static CallToolResult Create<T>(T payload, string summaryText)
    {
        // AIJsonUtilities.DefaultOptions = SDK 结构化应答的实际序列化口径（AIFunction 缺省 JsonSerializerOptions 即该单例），
        // 字段命名与 outputSchema 校验逐字一致
        JsonElement structured = JsonSerializer.SerializeToElement(
            payload, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(object)));

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = summaryText }],
            StructuredContent = structured
        };
    }

    /// <summary>
    /// 组装"缺省形态保持"工具应答：structuredContent 与 text 块同口径承载回执 DTO，
    /// 逐字复刻 SDK 对强类型对象返回的缺省应答形态（text = DTO 全量 JSON + structuredContent = DTO），
    /// 供未声明 outputSchema 的既有工具改为显式 CallToolResult 组装后回执保持逐字不变。
    /// </summary>
    /// <typeparam name="T">回执 DTO 类型。</typeparam>
    /// <param name="payload">强类型回执。</param>
    /// <returns>与 SDK typed-object 缺省应答形态一致的 CallToolResult。</returns>
    public static CallToolResult CreateDefaultShaped<T>(T payload)
    {
        // structuredContent 同用 AIJsonUtilities.DefaultOptions 序列化（SDK 结构化应答口径，非 ASCII 原样）；
        // text 块复刻 SDK typed-object 缺省 text 形态 = 紧凑 JSON + JavaScriptEncoder.Default 转义（非 ASCII 转 \uXXXX），
        // AIJsonUtilities.DefaultOptions 本身为缩进形态，克隆后关闭缩进并换用缺省编码器承载 text
        var defaultTextOptions = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions)
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.Default
        };
        string fullJson = JsonSerializer.Serialize(payload, defaultTextOptions.GetTypeInfo(typeof(object)));
        JsonElement structured = JsonSerializer.SerializeToElement(
            payload, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(object)));

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = fullJson }],
            StructuredContent = structured
        };
    }
}
