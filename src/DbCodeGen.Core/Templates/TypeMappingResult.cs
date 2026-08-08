namespace DbCodeGen.Core.Templates;

/// <summary>
/// 单次类型解析结果，携带是否命中、目标类型名与可选导包。
/// 命中来源可能是全局映射表或模板包 typeMap，均视为命中；仅全部未命中时 Found 为 false。
/// </summary>
public readonly record struct TypeMappingResult(bool Found, string TypeName, string? Import);
