namespace DbCodeGen.Core.BackupRestore;

/// <summary>
/// 备份操作结果，携带备份文件绝对路径与打包的用户模板包统计信息。
/// </summary>
public sealed class BackupResult
{
    /// <summary>
    /// 备份文件绝对路径。
    /// </summary>
    public string BackupFilePath { get; init; } = string.Empty;

    /// <summary>
    /// 打包的用户模板包数量（不含内置包）。
    /// </summary>
    public int UserPackageCount { get; init; }

    /// <summary>
    /// 打包的用户模板包名清单。
    /// </summary>
    public IReadOnlyList<string> PackageNames { get; init; } = Array.Empty<string>();
}
