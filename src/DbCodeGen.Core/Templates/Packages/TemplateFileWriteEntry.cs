namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 待追加到用户模板包的模板文件写入条目：声明模板相对路径、输出相对路径、模板内容与默认勾选态。
/// 由 AI 生成服务在追加模式下映射产生，批量追加时逐条目做路径安全与已存在预检后统一落盘。
/// </summary>
/// <param name="RelativePath">模板文件相对包根路径，可含分组目录，禁止绝对路径与 .. 段。</param>
/// <param name="OutputPath">输出相对路径，支持 {{变量}} 占位，同样禁止目录穿越。</param>
/// <param name="Content">模板文件内容，Scriban 语法文本。</param>
/// <param name="Enabled">是否默认勾选参与生成，缺省为 true。</param>
public sealed record TemplateFileWriteEntry(string RelativePath, string OutputPath, string Content, bool Enabled = true);
