namespace DbCodeGen.Core.BackupRestore;

/// <summary>
/// 备份文件（.dbcg）顶层清单模型，序列化为备份包根目录下的 manifest.json。
/// 记录备份格式版本、创建时间、应用版本、用户模板包名清单与脱敏配置快照，不含任何密码或密钥。
/// </summary>
public sealed class BackupManifest
{
    /// <summary>
    /// 备份文件格式版本，当前支持版本为 1，恢复前以此做版本兼容校验。
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// 备份创建时间，随备份文件落盘保留现场时间信息。
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 创建备份时的应用版本，供恢复侧判断备份是否来自兼容版本。
    /// </summary>
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>
    /// 备份中包含的用户模板包名清单，与备份包内 templates/&lt;包名&gt;/… 目录结构一一对应。
    /// </summary>
    public List<string> PackageNames { get; set; } = new();

    /// <summary>
    /// 脱敏后的配置快照，密码与 apiKey 密文一律不进入快照，仅以布尔标记替代。
    /// </summary>
    public BackupManifestConfig Config { get; set; } = new();
}
