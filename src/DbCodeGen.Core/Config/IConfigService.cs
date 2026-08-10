namespace DbCodeGen.Core.Config;

/// <summary>
/// 配置持久化服务统一接口，配置读取统一从 Current 快照取值，任何功能写配置都必须经 Save。
/// 公共门面为同步方法，文件 IO 在实现内部以异步 API 执行。
/// </summary>
public interface IConfigService
{
    /// <summary>
    /// 加载配置并置为 Current；文件不存在时生成默认配置并原子落盘，损坏时备份后按默认重建。
    /// </summary>
    /// <returns>加载或重建后的 AppConfig 实例，即内存权威快照。</returns>
    AppConfig Load();

    /// <summary>
    /// 将 Current 全量字段序列化后原子写入配置文件。
    /// </summary>
    /// <exception cref="ConfigSaveException">写盘失败时抛出，内存中的原值保留不变。</exception>
    void Save();

    /// <summary>
    /// 配置保存完成事件，成功落盘后触发，供依赖配置的消费方（如预览区）感知配置变化并刷新。
    /// 事件在锁外触发，回调内可安全再次读写配置。
    /// </summary>
    event EventHandler? ConfigChanged;

    /// <summary>
    /// 内存中最新配置快照，首次访问时自动加载，可作为各功能的只读取值入口。
    /// </summary>
    AppConfig Current { get; }

    /// <summary>
    /// 配置文件绝对路径，默认 %AppData%\DbCodeGen\config.json；经数据目录切换后指向新目录下的 config.json。
    /// </summary>
    string ConfigFilePath { get; }

    /// <summary>
    /// 切换统一数据目录：校验目标目录可写后，将当前配置文件复制到新目录、迁移默认模板目录、
    /// 更新内存 DataDirectory 与配置路径、写定位文件并落盘，使 config.json 与 Templates 集中到新目录。
    /// </summary>
    /// <param name="dataDirectory">目标数据目录绝对路径。</param>
    /// <exception cref="ArgumentException">目录为空、非绝对路径、指向已存在文件或不可写时抛出。</exception>
    /// <exception cref="ConfigSaveException">切换后落盘失败时抛出。</exception>
    void ChangeDataDirectory(string dataDirectory);

    /// <summary>
    /// 获取批量代码生成使用的默认值快照，包含工作区根与最近相对输出根。
    /// </summary>
    /// <returns>工作区根与最近相对输出根。</returns>
    GenerationDefaults GetGenerationDefaults();

    /// <summary>
    /// 解密并返回 LLM apiKey 明文瞬态值，用后即弃；未配置时返回 null。
    /// </summary>
    /// <returns>解密后的 apiKey 明文；未配置返回 null。</returns>
    string? GetLlmApiKey();
}
