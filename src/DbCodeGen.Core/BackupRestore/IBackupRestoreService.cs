namespace DbCodeGen.Core.BackupRestore;

/// <summary>
/// 备份/恢复服务统一接口，承载换电脑一键迁移模板与配置的核心能力。
/// 备份文件格式为 .dbcg（本质 zip），只打包用户模板包与脱敏配置快照，不含任何密码或密钥。
/// </summary>
public interface IBackupRestoreService
{
    /// <summary>
    /// 创建备份文件：只打包用户模板包（IsBuiltin=false）为 templates/&lt;包名&gt;/… 结构，
    /// 写入 manifest.json（版本/时间戳/包清单/脱敏配置快照），以 System.IO.Compression 生成 .dbcg 文件。
    /// </summary>
    /// <param name="targetFilePath">备份文件目标绝对路径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>备份操作结果，含备份文件路径与打包的用户包统计。</returns>
    /// <exception cref="BackupValidationException">打包过程中包目录异常时抛出。</exception>
    Task<BackupResult> CreateBackupAsync(string targetFilePath, CancellationToken cancellationToken);

    /// <summary>
    /// 恢复备份文件：校验 .dbcg（版本/格式/防目录穿越/zip bomb 上限）→ 还原用户包到默认模板库目录
    /// （确保该目录在模板搜索目录中；同名用户包未允许覆盖时返回需确认结果）→ 还原配置非密文字段
    /// （经 IConfigService.Current + Save，清空密码与 apiKey 密文）→ 返回需重输密码的数据源名与需重配 LLM 标记。
    /// </summary>
    /// <param name="backupFilePath">备份文件绝对路径。</param>
    /// <param name="overwriteUserPackages">是否允许覆盖同名用户模板包。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>恢复操作结果；同名冲突未允许覆盖时返回需确认结果且不执行任何写盘。</returns>
    /// <exception cref="BackupValidationException">备份文件校验失败或恢复过程 IO 错误时抛出。</exception>
    Task<RestoreResult> RestoreBackupAsync(string backupFilePath, bool overwriteUserPackages, CancellationToken cancellationToken);
}
