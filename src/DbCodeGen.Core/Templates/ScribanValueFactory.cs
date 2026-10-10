using System.Text.Json;
using Scriban.Runtime;

namespace DbCodeGen.Core.Templates;

/// <summary>
/// 调用方传入值到 Scriban 渲染值的转换器：承载 vars.* 与 module.* 注入值的统一转换口径。
/// JSON 元素、字典与集合递归转换为 Scriban 可遍历/可取成员的形态，基本类型原样透传。
/// </summary>
internal static class ScribanValueFactory
{
    /// <summary>
    /// 将调用方传入值转换为 Scriban 渲染值：JsonElement 按值类别递归转换；
    /// 字符串键字典转 ScriptObject；集合（非字符串）转 ScriptArray；基本类型与空值原样返回。
    /// </summary>
    /// <param name="value">调用方传入的原始值。</param>
    /// <returns>Scriban 渲染上下文可消费的值。</returns>
    public static object? ToScribanValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is JsonElement element)
        {
            return FromJsonElement(element);
        }

        // 字符串键字典按成员对象转换，模板内以 point.member 形式访问
        if (value is IDictionary<string, object?> dictionary)
        {
            return FromMemberDictionary(dictionary);
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            return FromMemberDictionary(readOnlyDictionary);
        }

        // 集合转 ScriptArray 供模板 for 遍历；string 不实现 IEnumerable<object>，天然走标量透传分支
        if (value is IEnumerable<object?> sequence)
        {
            var array = new ScriptArray();
            foreach (object? item in sequence)
            {
                array.Add(ToScribanValue(item)!);
            }

            return array;
        }

        return value;
    }

    /// <summary>
    /// 将 JsonElement 按值类别递归转换为 Scriban 值：对象转 ScriptObject、数组转 ScriptArray、
    /// 标量转对应 CLR 基本类型。
    /// </summary>
    /// <param name="element">JSON 元素。</param>
    /// <returns>Scriban 渲染上下文可消费的值。</returns>
    private static object? FromJsonElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var scriptObject = new ScriptObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    scriptObject.SetValue(property.Name, FromJsonElement(property.Value), true);
                }

                return scriptObject;
            case JsonValueKind.Array:
                var array = new ScriptArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    array.Add(FromJsonElement(item));
                }

                return array;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                // 数值优先按整数语义透传，含小数或超出整数范围时回退双精度
                return element.TryGetInt64(out long longValue) ? longValue : element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                // Null 与 Undefined 统一按空值渲染
                return null;
        }
    }

    /// <summary>
    /// 将字符串键字典转换为 ScriptObject，逐键递归转换值。
    /// </summary>
    /// <param name="dictionary">字符串键字典。</param>
    /// <returns>成员对象形态的 ScriptObject。</returns>
    private static ScriptObject FromMemberDictionary(IEnumerable<KeyValuePair<string, object?>> dictionary)
    {
        var scriptObject = new ScriptObject();
        foreach (KeyValuePair<string, object?> pair in dictionary)
        {
            scriptObject.SetValue(pair.Key, ToScribanValue(pair.Value), true);
        }

        return scriptObject;
    }
}
