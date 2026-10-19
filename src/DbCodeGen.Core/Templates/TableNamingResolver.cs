using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates.Packages;

namespace DbCodeGen.Core.Templates;

/// <summary>
/// 表命名应用器：把生效命名策略写入表元数据的实体名/类名/变量名，物理表名 rawName 原样保留。
/// 批量生成与单模板渲染共用本方法，保证「所见即所得」的命名结果一致；返回克隆对象避免污染调用方复用的表实例。
/// </summary>
public static class TableNamingResolver
{
    /// <summary>
    /// 按生效命名策略克隆表元数据并写入 entityName/className/variableName：
    /// className 与 entityName 同值（向后兼容保留 className），variableName 为 entityName 首字母小写。
    /// </summary>
    /// <param name="table">原表元数据。</param>
    /// <param name="policy">生效命名策略。</param>
    /// <returns>应用命名策略后的表元数据（列集合与派生子集按引用共享）。</returns>
    /// <exception cref="ArgumentNullException">table 或 policy 为 null 时抛出。</exception>
    public static TableInfo Apply(TableInfo table, ResolvedNamingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(policy);

        string entityName = policy.DeriveEntityName(table.RawName);
        return new TableInfo
        {
            RawName = table.RawName,
            SchemaName = table.SchemaName,
            EntityName = entityName,
            ClassName = entityName,
            VariableName = ToolFunctions.FirstLowerCase(entityName),
            Comment = table.Comment,
            CreatedTime = table.CreatedTime,
            CreationOrder = table.CreationOrder,
            DatabaseType = table.DatabaseType,
            Columns = table.Columns,
            PrimaryKeys = table.PrimaryKeys,
            FullColumn = table.FullColumn,
            OtherColumn = table.OtherColumn
        };
    }
}
