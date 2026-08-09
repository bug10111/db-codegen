namespace DbCodeGen.Core.Model;

/// <summary>
/// 包内模板文件勾选态的持久化条目，存于全局配置（AppConfig.TemplateFileStates），
/// 由②模板区"勾选到层" checkbox 变化时按包名写入，下次加载该包时覆盖 manifest 声明的默认勾选态。
/// </summary>
public sealed class TemplateFileState
{
    /// <summary>
    /// 模板文件相对包根的路径，正斜杠规范化，与 manifest files[].template 对应。
    /// </summary>
    public string TemplatePath { get; set; } = string.Empty;

    /// <summary>
    /// 记忆的勾选态，覆盖该文件 manifest 声明的默认值，供下次加载该包时还原。
    /// </summary>
    public bool Enabled { get; set; }
}
