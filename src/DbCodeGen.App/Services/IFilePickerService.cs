namespace DbCodeGen.App.Services;

/// <summary>
/// 文件选择服务，供模板包 zip 导入导出、SQL 脚本打开保存与迁移备份恢复等场景选择本地文件路径，跨窗口复用。
/// 与目录选择服务（IFolderPickerService）分工：本服务只负责文件级别的打开与保存对话框。
/// </summary>
public interface IFilePickerService
{
    /// <summary>
    /// 弹出打开文件对话框并返回用户选中的模板包 zip 文件路径。
    /// </summary>
    /// <param name="initialDirectory">初始定位目录；为空或不存在时由对话框决定起始位置。</param>
    /// <param name="title">对话框标题，默认“选择模板包 zip 文件”。</param>
    /// <returns>选中的 zip 文件绝对路径；用户取消时返回 null。</returns>
    Task<string?> PickOpenZipAsync(string? initialDirectory = null, string title = "选择模板包 zip 文件");

    /// <summary>
    /// 弹出保存文件对话框并返回用户指定的模板包 zip 导出路径。
    /// </summary>
    /// <param name="defaultFileName">默认文件名，如“java-mybatis-plus.zip”。</param>
    /// <param name="initialDirectory">初始定位目录；为空或不存在时由对话框决定起始位置。</param>
    /// <param name="title">对话框标题，默认“导出模板包 zip 文件”。</param>
    /// <returns>用户指定的 zip 文件绝对路径；用户取消时返回 null。</returns>
    Task<string?> PickSaveZipAsync(string defaultFileName, string? initialDirectory = null, string title = "导出模板包 zip 文件");

    /// <summary>
    /// 弹出打开文件对话框并返回用户选中的 SQL 脚本文件路径，供 SQL 执行面板打开建表脚本。
    /// </summary>
    /// <param name="initialDirectory">初始定位目录；为空或不存在时由对话框决定起始位置。</param>
    /// <param name="title">对话框标题，默认“打开 SQL 文件”。</param>
    /// <returns>选中的 SQL 文件绝对路径；用户取消时返回 null。</returns>
    Task<string?> PickOpenSqlAsync(string? initialDirectory = null, string title = "打开 SQL 文件");

    /// <summary>
    /// 弹出保存文件对话框并返回用户指定的 SQL 脚本保存路径，供 SQL 执行面板保存当前编辑语句。
    /// </summary>
    /// <param name="defaultFileName">默认文件名，如“query.sql”。</param>
    /// <param name="initialDirectory">初始定位目录；为空或不存在时由对话框决定起始位置。</param>
    /// <param name="title">对话框标题，默认“保存 SQL 文件”。</param>
    /// <returns>用户指定的 SQL 文件绝对路径；用户取消时返回 null。</returns>
    Task<string?> PickSaveSqlAsync(string defaultFileName, string? initialDirectory = null, string title = "保存 SQL 文件");

    /// <summary>
    /// 弹出打开文件对话框并返回用户选中的 .dbcg 备份文件路径，供迁移窗口恢复页选择备份文件。
    /// </summary>
    /// <param name="initialDirectory">初始定位目录；为空或不存在时由对话框决定起始位置。</param>
    /// <param name="title">对话框标题，默认“选择备份文件”。</param>
    /// <returns>选中的 .dbcg 文件绝对路径；用户取消时返回 null。</returns>
    Task<string?> PickOpenBackupAsync(string? initialDirectory = null, string title = "选择备份文件");

    /// <summary>
    /// 弹出保存文件对话框并返回用户指定的 .dbcg 备份文件保存路径，供迁移窗口备份页选择目标位置。
    /// </summary>
    /// <param name="defaultFileName">默认文件名，如“DbCodeGen-backup.dbcg”。</param>
    /// <param name="initialDirectory">初始定位目录；为空或不存在时由对话框决定起始位置。</param>
    /// <param name="title">对话框标题，默认“保存备份文件”。</param>
    /// <returns>用户指定的 .dbcg 文件绝对路径；用户取消时返回 null。</returns>
    Task<string?> PickSaveBackupAsync(string defaultFileName, string? initialDirectory = null, string title = "保存备份文件");

    /// <summary>
    /// 弹出打开文件对话框并返回用户选中的 JSON 文件路径，供类型映射表导入。
    /// </summary>
    /// <param name="initialDirectory">初始定位目录；为空或不存在时由对话框决定起始位置。</param>
    /// <param name="title">对话框标题，默认“导入类型映射”。</param>
    /// <returns>选中的 JSON 文件绝对路径；用户取消时返回 null。</returns>
    Task<string?> PickOpenJsonAsync(string? initialDirectory = null, string title = "导入类型映射");

    /// <summary>
    /// 弹出保存文件对话框并返回用户指定的 JSON 文件保存路径，供类型映射表导出。
    /// </summary>
    /// <param name="defaultFileName">默认文件名，如“type-mappings.json”。</param>
    /// <param name="initialDirectory">初始定位目录；为空或不存在时由对话框决定起始位置。</param>
    /// <param name="title">对话框标题，默认“导出类型映射”。</param>
    /// <returns>用户指定的 JSON 文件绝对路径；用户取消时返回 null。</returns>
    Task<string?> PickSaveJsonAsync(string defaultFileName, string? initialDirectory = null, string title = "导出类型映射");
}
