using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DbCodeGen.Core.Model;

namespace DbCodeGen.App.ViewModels;

/// <summary>
/// 变量面板树节点类型枚举，决定节点在树中的分组语义与图标承载。
/// </summary>
public enum VariableNodeKind
{
    /// <summary>
    /// 表级元数据节点，表达式直连 TableInfo 字段。
    /// </summary>
    Table,

    /// <summary>
    /// 列级元数据节点，表达式直连 ColumnInfo 字段，通常在列遍历循环内使用。
    /// </summary>
    Column,

    /// <summary>
    /// 包级上下文节点，表达式直连 manifest 注入的 package 变量。
    /// </summary>
    Package,

    /// <summary>
    /// 工具函数节点，表达式为 tool 函数调用。
    /// </summary>
    Tool
}

/// <summary>
/// 变量面板树节点，承载展示名、说明与可插入模板的 Scriban 表达式。
/// 叶子节点携带可插入的表达式，分组节点仅承载子节点；表达式严格对齐 02 字段契约。
/// </summary>
public sealed class VariableTreeNode
{
    /// <summary>
    /// 使用完整字段构造树节点。
    /// </summary>
    /// <param name="key">节点键，标识节点所属变量分组。</param>
    /// <param name="displayName">展示名，如“类名（PascalCase）”。</param>
    /// <param name="description">说明文本，用于悬浮提示。</param>
    /// <param name="expression">可插入模板的 Scriban 表达式，分组节点为 null。</param>
    /// <param name="nodeKind">节点类型，决定分组语义。</param>
    /// <param name="children">子节点集合，默认空集。</param>
    public VariableTreeNode(
        string key,
        string displayName,
        string description,
        string? expression,
        VariableNodeKind nodeKind,
        IReadOnlyList<VariableTreeNode>? children = null)
    {
        Key = key;
        DisplayName = displayName;
        Description = description;
        Expression = expression;
        NodeKind = nodeKind;
        Children = children ?? Array.Empty<VariableTreeNode>();
    }

    /// <summary>
    /// 节点键，标识节点所属变量分组。
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// 展示名，如“类名（PascalCase）”。
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// 说明文本，用于悬浮提示。
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// 可插入模板的 Scriban 表达式，分组节点为 null。
    /// </summary>
    public string? Expression { get; }

    /// <summary>
    /// 节点类型，决定分组语义。
    /// </summary>
    public VariableNodeKind NodeKind { get; }

    /// <summary>
    /// 子节点集合，叶子节点为空集。
    /// </summary>
    public IReadOnlyList<VariableTreeNode> Children { get; }
}

/// <summary>
/// 变量面板视图模型，承载当前表元数据树，与预览区共用同一当前表。
/// 当前表变化时按 02 字段契约重建变量表达式树，供用户双击插入到模板编辑器光标处。
/// </summary>
public sealed partial class VariablePanelViewModel : ObservableObject
{
    /// <summary>
    /// 预览视图模型，变量面板订阅其当前表变更事件，保证与预览渲染使用同一张表。
    /// </summary>
    private readonly PreviewViewModel _previewViewModel;

    /// <summary>
    /// 变量表达式树根节点集合，TreeView 绑定源。
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<VariableTreeNode> _rootNodes = new();

    /// <summary>
    /// 当前表展示名，表头展示表名与类名。
    /// </summary>
    [ObservableProperty]
    private string _tableDisplayName = "未选择表";

    /// <summary>
    /// 是否已存在当前表，驱动空态提示与树可用性。
    /// </summary>
    [ObservableProperty]
    private bool _hasTable;

    /// <summary>
    /// 使用预览视图模型构造变量面板视图模型，并订阅当前表变更。
    /// </summary>
    /// <param name="previewViewModel">预览视图模型，提供单一当前表来源。</param>
    /// <exception cref="ArgumentNullException">previewViewModel 为 null 时抛出。</exception>
    public VariablePanelViewModel(PreviewViewModel previewViewModel)
    {
        ArgumentNullException.ThrowIfNull(previewViewModel);

        _previewViewModel = previewViewModel;
        _previewViewModel.CurrentTableChanged += OnCurrentTableChanged;

        // 订阅后立即以当前已有表初始化变量树，窗口打开即有内容
        OnCurrentTableChanged(_previewViewModel.CurrentTable);
    }

    /// <summary>
    /// 当前表变更时重建变量表达式树，无当前表时清空并展示空态。
    /// </summary>
    /// <param name="table">变更后的当前表，未选择时为 null。</param>
    private void OnCurrentTableChanged(TableInfo? table)
    {
        RootNodes.Clear();
        if (table is null)
        {
            TableDisplayName = "未选择表";
            HasTable = false;
            return;
        }

        TableDisplayName = $"{table.RawName}（{table.ClassName}）";
        HasTable = true;
        BuildTree(table);
    }

    /// <summary>
    /// 按表元数据构建变量表达式树，分表变量、列变量、工具函数与包变量四个分组。
    /// 表达式严格对齐 02 TableInfo/ColumnInfo 字段契约与 manifest 注入变量。
    /// </summary>
    /// <param name="table">当前表元数据。</param>
    private void BuildTree(TableInfo table)
    {
        RootNodes.Add(BuildTableGroup());
        RootNodes.Add(BuildColumnGroup());
        RootNodes.Add(BuildToolGroup());
        RootNodes.Add(BuildPackageGroup());
    }

    /// <summary>
    /// 构建表变量分组节点，覆盖类名、变量名、原始表名、注释与常用工具函数调用。
    /// 展示名统一为「中文名（英文全变量）」，括号内为可插入模板的完整表达式，用户所见即所插。
    /// </summary>
    /// <returns>表变量分组节点。</returns>
    private static VariableTreeNode BuildTableGroup()
    {
        return new VariableTreeNode(
            "table", "表变量", "当前表元数据，直连 table 变量字段",
            null, VariableNodeKind.Table, new VariableTreeNode[]
            {
                new("className", "类名（table.className）", "TableInfo.className", "{{ table.className }}", VariableNodeKind.Table),
                new("variableName", "变量名（table.variableName）", "TableInfo.variableName", "{{ table.variableName }}", VariableNodeKind.Table),
                new("rawName", "原始表名（table.rawName）", "TableInfo.rawName", "{{ table.rawName }}", VariableNodeKind.Table),
                new("comment", "表注释（table.comment）", "TableInfo.comment", "{{ table.comment }}", VariableNodeKind.Table),
                new("firstLowerCase", "首字母小写类名（tool.firstLowerCase(table.className)）", "tool.firstLowerCase(table.className)", "{{ tool.firstLowerCase(table.className) }}", VariableNodeKind.Table),
                new("firstUpperCase", "首字母大写类名（tool.firstUpperCase(table.className)）", "tool.firstUpperCase(table.className)", "{{ tool.firstUpperCase(table.className) }}", VariableNodeKind.Table),
                new("hump2Underline", "下划线表名（tool.hump2Underline(table.className)）", "tool.hump2Underline(table.className)", "{{ tool.hump2Underline(table.className) }}", VariableNodeKind.Table),
                new("primaryKeys", "遍历主键列（table.primaryKeys）", "for pk in table.primaryKeys 遍历", "{{ for pk in table.primaryKeys }}...{{ end }}", VariableNodeKind.Table),
                new("fullColumn", "遍历全量列（table.fullColumn）", "for column in table.fullColumn 遍历", "{{ for column in table.fullColumn }}...{{ end }}", VariableNodeKind.Table),
                new("otherColumn", "遍历非主键列（table.otherColumn）", "for column in table.otherColumn 遍历", "{{ for column in table.otherColumn }}...{{ end }}", VariableNodeKind.Table)
            });
    }

    /// <summary>
    /// 构建列变量分组节点，覆盖属性名、原始列名、注释、类型与标记字段。
    /// 展示名统一为「中文名（英文全变量）」，列表达式通常在表变量的列遍历循环内使用。
    /// </summary>
    /// <returns>列变量分组节点。</returns>
    private static VariableTreeNode BuildColumnGroup()
    {
        return new VariableTreeNode(
            "column", "列变量", "列字段，在列遍历循环内使用，直连 column 变量字段",
            null, VariableNodeKind.Column, new VariableTreeNode[]
            {
                new("propertyName", "属性名（column.propertyName）", "ColumnInfo.propertyName", "{{ column.propertyName }}", VariableNodeKind.Column),
                new("rawName", "原始列名（column.rawName）", "ColumnInfo.rawName", "{{ column.rawName }}", VariableNodeKind.Column),
                new("comment", "列注释（column.comment）", "ColumnInfo.comment", "{{ column.comment }}", VariableNodeKind.Column),
                new("rawDbType", "原始 DB 类型（column.rawDbType）", "ColumnInfo.rawDbType", "{{ column.rawDbType }}", VariableNodeKind.Column),
                new("mappedType", "映射后类型（tool.type(column.rawDbType)）", "tool.type(column.rawDbType)", "{{ tool.type(column.rawDbType) }}", VariableNodeKind.Column),
                new("isPrimaryKey", "是否主键（column.isPrimaryKey）", "ColumnInfo.isPrimaryKey", "{{ column.isPrimaryKey }}", VariableNodeKind.Column),
                new("autoIncrement", "是否自增（column.autoIncrement）", "ColumnInfo.autoIncrement", "{{ column.autoIncrement }}", VariableNodeKind.Column)
            });
    }

    /// <summary>
    /// 构建工具函数分组节点，覆盖字符串处理与类型映射函数。
    /// 展示名按「中文名（表达式）」对齐，hump3Underline 名称与说明随其 kebab-case 行为同步更新。
    /// </summary>
    /// <returns>工具函数分组节点。</returns>
    private static VariableTreeNode BuildToolGroup()
    {
        return new VariableTreeNode(
            "tool", "工具函数", "模板渲染侧 tool 函数集",
            null, VariableNodeKind.Tool, new VariableTreeNode[]
            {
                new("firstLowerCase", "首字母小写（tool.firstLowerCase(值)）", "tool.firstLowerCase(值)", "{{ tool.firstLowerCase(table.className) }}", VariableNodeKind.Tool),
                new("firstUpperCase", "首字母大写（tool.firstUpperCase(值)）", "tool.firstUpperCase(值)", "{{ tool.firstUpperCase(table.className) }}", VariableNodeKind.Tool),
                new("hump2Underline", "驼峰转下划线（tool.hump2Underline(值)）", "tool.hump2Underline(值)", "{{ tool.hump2Underline(table.className) }}", VariableNodeKind.Tool),
                new("hump3Underline", "驼峰转短横线（tool.hump3Underline(值)）", "kebab-case（如 sys-project-client）", "{{ tool.hump3Underline(table.className) }}", VariableNodeKind.Tool),
                new("type", "类型映射（tool.type(column.rawDbType)）", "tool.type(column.rawDbType)", "{{ tool.type(column.rawDbType) }}", VariableNodeKind.Tool)
            });
    }

    /// <summary>
    /// 构建包变量分组节点，覆盖 manifest 注入的基础包名与包名。
    /// 展示名统一为「中文名（英文全变量）」。
    /// </summary>
    /// <returns>包变量分组节点。</returns>
    private static VariableTreeNode BuildPackageGroup()
    {
        return new VariableTreeNode(
            "package", "包变量", "模板包上下文，manifest 注入",
            null, VariableNodeKind.Package, new VariableTreeNode[]
            {
                new("basePackage", "基础包名（package.basePackage）", "manifest basePackage（可含完整包路径，如 com.example.common）", "{{ package.basePackage }}", VariableNodeKind.Package),
                new("name", "包名（package.name）", "manifest name", "{{ package.name }}", VariableNodeKind.Package)
            });
    }
}
