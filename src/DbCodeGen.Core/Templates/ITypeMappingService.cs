using DbCodeGen.Core.Model;

namespace DbCodeGen.Core.Templates;

/// <summary>
/// 全局类型映射解析服务接口，承载"全局映射表 > 包 typeMap > 兜底"的解析链，
/// 与生成前未映射类型预检。全局映射表取自配置服务，由用户经"类型映射"窗口维护。
/// </summary>
public interface ITypeMappingService
{
    /// <summary>
    /// 解析数据库原始类型为目标语言类型：先查全局映射表，再查包 typeMap，均未命中返回兜底类型。
    /// </summary>
    /// <param name="rawDbType">数据库原始类型，可空。</param>
    /// <param name="packageTypeMap">当前模板包 manifest 的 typeMap，可空。</param>
    /// <param name="fallback">全部未命中时返回的默认目标类型。</param>
    /// <returns>解析结果，含命中与否、目标类型名与可选导包。</returns>
    TypeMappingResult Resolve(string? rawDbType, IReadOnlyDictionary<string, string>? packageTypeMap, string fallback = "String");

    /// <summary>
    /// 遍历指定表的全部列，汇总解析链未命中的原始类型，供界面弹窗提示用户补映射。
    /// 同类型跨表跨列归并为一条，记录首次出现位置与总出现次数。
    /// </summary>
    /// <param name="tables">参与生成的表集合。</param>
    /// <param name="packageTypeMap">当前模板包 manifest 的 typeMap，可空。</param>
    /// <returns>未映射类型清单，按类型名排序。</returns>
    IReadOnlyList<UnmappedTypeInfo> FindUnmappedTypes(IReadOnlyList<TableInfo> tables, IReadOnlyDictionary<string, string>? packageTypeMap);
}
