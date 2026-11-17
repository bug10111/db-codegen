using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace DbCodeGen.Core.Tests.Mcp;

/// <summary>
/// 结构化工具单载荷应答解包测试辅助：read_template 改为单载荷 CallToolResult 后，
/// 单元测试经 structuredContent 按强类型反序列化取回执做断言；序列化口径与 SDK 结构化应答一致（camelCase）。
/// </summary>
internal static class StructuredToolResponseReader
{
    /// <summary>
    /// 从单载荷工具应答解出结构化回执。
    /// </summary>
    /// <typeparam name="T">回执 DTO 类型。</typeparam>
    /// <param name="result">工具返回的单载荷应答。</param>
    /// <returns>反序列化后的强类型回执。</returns>
    internal static T ReadStructured<T>(CallToolResult result)
    {
        if (result.StructuredContent is not { } structured)
        {
            throw new InvalidOperationException($"工具应答缺少 structuredContent，无法解出 {typeof(T).Name}。");
        }

        object? payload = JsonSerializer.Deserialize(structured.GetRawText(), AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(T)));
        if (payload is not T typed)
        {
            throw new InvalidOperationException($"structuredContent 反序列化结果为 {payload?.GetType().Name ?? "null"}，期望 {typeof(T).Name}。");
        }

        return typed;
    }
}
