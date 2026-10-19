using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板包 manifest.presets 的单条预设定义：把「常用模板组合 + 默认变量」命名固化，
/// 调用方在 preview_generation/generate 只需传 preset 名称，由服务端解析为模板文件清单。
/// 字段语义：include 二选一 enabled（缺省，全部启用条目）/ all（全部条目，含 enabled=false）；
/// files 为显式模板相对路径清单（必须存在于 manifest.files[]，可含未启用条目）；variables 为预设默认变量，
/// 调用方显式 variables 同名键优先（逐键覆盖）。
/// </summary>
public sealed class TemplatePresetDefinition
{
    /// <summary>
    /// 预设说明，列表与契约展示用，必填。
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 条目选择口径：enabled（缺省）= 包内全部启用条目；all = 全部条目（含 enabled=false）；
    /// 与 files 二选一，同时声明即判清单非法。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Include { get; set; }

    /// <summary>
    /// 显式模板相对路径清单（正斜杠形态，对应 manifest.files[].template）；必须全部存在于清单 files[]，
    /// 可包含 enabled=false 的条目；与 include 二选一。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Files { get; set; }

    /// <summary>
    /// 预设默认变量，逐键注入渲染上下文 vars.*；值为任意 JSON 类型。
    /// 调用方显式传入的同名键优先（逐键覆盖预设值）。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? Variables { get; set; }
}

/// <summary>
/// 预设摘要：模板包列表/模板列表/模板读取回执统一透出，供调用方在生成前发现可用预设。
/// </summary>
/// <param name="Name">预设名称（合法标识符）。</param>
/// <param name="Description">预设说明。</param>
/// <param name="FileCount">该预设解析后的模板文件数（enabled=启用条目数；all=全部条目数；files=清单条目数）。</param>
public sealed record TemplatePresetSummary(string Name, string Description, int FileCount);
