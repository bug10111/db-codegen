using System.Text.Json;

namespace DbCodeGen.Core.Templates;

/// <summary>
/// 调用方变量取值读取器：把 variables 字典中的值按 bool / string / string 集合语义读取，
/// 兼容 JSON 元素（MCP 绑定）、CLR 基本类型与数组（桌面端/探针直构）两种来源形态。
/// </summary>
internal static class PolicyValueReader
{
    /// <summary>
    /// 读取布尔变量：支持 CLR bool、JSON 布尔与字符串 "true"/"false" 三种形态；缺失或不可解析返回 null。
    /// </summary>
    /// <param name="variables">调用方变量字典，可为空。</param>
    /// <param name="key">变量键，大小写不敏感。</param>
    /// <returns>解析出的布尔值；缺失或不可解析时为 null。</returns>
    public static bool? GetBool(IReadOnlyDictionary<string, object?>? variables, string key)
    {
        object? value = FindValue(variables, key);
        switch (value)
        {
            case bool boolean:
                return boolean;
            case JsonElement { ValueKind: JsonValueKind.True }:
                return true;
            case JsonElement { ValueKind: JsonValueKind.False }:
                return false;
            case JsonElement { ValueKind: JsonValueKind.String } element:
                return bool.TryParse(element.GetString(), out bool parsedFromJson) ? parsedFromJson : null;
            case string text:
                return bool.TryParse(text, out bool parsed) ? parsed : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// 读取字符串变量：支持 CLR 字符串与 JSON 字符串；缺失或非字符串返回 null。
    /// </summary>
    /// <param name="variables">调用方变量字典，可为空。</param>
    /// <param name="key">变量键，大小写不敏感。</param>
    /// <returns>去空白后的字符串；缺失或空串时为 null。</returns>
    public static string? GetString(IReadOnlyDictionary<string, object?>? variables, string key)
    {
        object? value = FindValue(variables, key);
        string? text = value switch
        {
            string direct => direct,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null
        };

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>
    /// 读取字符串集合变量：支持逗号分隔字符串、JSON 字符串数组与 CLR 字符串集合；
    /// 缺失或空集合返回 null，元素去空白并剔除空项。
    /// </summary>
    /// <param name="variables">调用方变量字典，可为空。</param>
    /// <param name="key">变量键，大小写不敏感。</param>
    /// <returns>字符串清单；缺失或空集合时为 null。</returns>
    public static IReadOnlyList<string>? GetStringList(IReadOnlyDictionary<string, object?>? variables, string key)
    {
        object? value = FindValue(variables, key);
        var items = new List<string>();

        switch (value)
        {
            case null:
                return null;
            case string text:
                items.AddRange(text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                break;
            case JsonElement { ValueKind: JsonValueKind.String } element:
                items.AddRange(element.GetString()?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    ?? Array.Empty<string>());
                break;
            case JsonElement { ValueKind: JsonValueKind.Array } array:
                foreach (JsonElement item in array.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        items.Add(item.GetString()!.Trim());
                    }
                }

                break;
            case IEnumerable<string> stringSequence:
                foreach (string item in stringSequence)
                {
                    if (!string.IsNullOrWhiteSpace(item))
                    {
                        items.Add(item.Trim());
                    }
                }

                break;
            case IEnumerable<object?> objectSequence:
                foreach (object? item in objectSequence)
                {
                    string? textItem = item switch
                    {
                        string direct => direct,
                        JsonElement { ValueKind: JsonValueKind.String } elementItem => elementItem.GetString(),
                        _ => null
                    };
                    if (!string.IsNullOrWhiteSpace(textItem))
                    {
                        items.Add(textItem.Trim());
                    }
                }

                break;
        }

        return items.Count == 0 ? null : items;
    }

    /// <summary>
    /// 按大小写不敏感键查找变量值；字典本身为空时返回 null。
    /// </summary>
    /// <param name="variables">调用方变量字典，可为空。</param>
    /// <param name="key">变量键。</param>
    /// <returns>命中的原始值；未命中为 null。</returns>
    private static object? FindValue(IReadOnlyDictionary<string, object?>? variables, string key)
    {
        if (variables is null || variables.Count == 0)
        {
            return null;
        }

        if (variables.TryGetValue(key, out object? value))
        {
            return value;
        }

        foreach (KeyValuePair<string, object?> pair in variables)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }
}
