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
    /// 内存中最新配置快照，首次访问时自动加载，可作为各功能的只读取值入口。
    /// </summary>
    AppConfig Current { get; }

    /// <summary>
    /// 配置文件绝对路径，默认 %AppData%\DbCodeGen\config.json，供诊断与单元测试使用。
    /// </summary>
    string ConfigFilePath { get; }

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
