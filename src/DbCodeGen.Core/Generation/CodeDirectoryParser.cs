using System.Linq;

namespace DbCodeGen.Core.Generation;

/// <summary>
/// 代码目录解析器：从"代码目录"（项目内代码落盘完整路径，含包名）推导基础包名与相对输出根。
/// 合并 ④生成栏的"相对输出根 + 基础包名"两个输入为单一"代码目录"，兼容单体与微服务目录结构。
/// </summary>
public static class CodeDirectoryParser
{
    /// <summary>
    /// 源码根目录，代码实际落盘的固定前缀，由工作区根 + 本前缀 + 包名目录构成。
    /// </summary>
    public const string SourceRoot = "src/main/java";

    /// <summary>
    /// 源根标记段，最后一个标记段之后的部分视为包名段；无标记时整段视为包名。
    /// </summary>
    private static readonly string[] SourceRootMarkers = { "java", "kotlin", "groovy", "scala", "src" };

    /// <summary>
    /// 从代码目录推导基础包名（斜杠转点号，源根标记之前的部分剔除）。
    /// </summary>
    /// <param name="codeDirectory">代码目录，即 package，如 com.example.common。</param>
    /// <returns>基础包名，无包名部分时为空串。</returns>
    public static string DeriveBasePackage(string codeDirectory)
    {
        return Split(codeDirectory).BasePackage;
    }

    /// <summary>
    /// 从代码目录推导相对输出根：有源根标记时取标记之前的目录段，无标记时固定为 src/main/java。
    /// </summary>
    /// <param name="codeDirectory">代码目录，即 package，如 com.example.common。</param>
    /// <returns>相对输出根，如 src/main/java。</returns>
    public static string DeriveRelativeOutputRoot(string codeDirectory)
    {
        return Split(codeDirectory).RelativeOutputRoot;
    }

    /// <summary>
    /// 将代码目录拆分为基础包名与相对输出根。代码目录即 package（点号或斜杠分隔均可）；
    /// 输入为完整路径时取最后一个源根标记之后的段作为包名、之前作为输出根；纯 package 时输出根固定 src/main/java。
    /// </summary>
    /// <param name="codeDirectory">代码目录，正反斜杠均可。</param>
    /// <returns>基础包名与相对输出根的元组。</returns>
    public static (string BasePackage, string RelativeOutputRoot) Split(string codeDirectory)
    {
        // 统一正斜杠并按 / 分段，忽略首尾斜杠与空段
        string normalized = (codeDirectory ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // 从尾部找最后一个源根标记段，其后的段才是包名段
        int markerIndex = -1;
        for (int i = segments.Length - 1; i >= 0; i--)
        {
            if (SourceRootMarkers.Contains(segments[i], StringComparer.OrdinalIgnoreCase))
            {
                markerIndex = i;
                break;
            }
        }

        // 未识别到源根标记时按纯 package 处理：整段为包名，输出根固定为源码根
        if (markerIndex < 0)
        {
            return (string.Join(".", segments), SourceRoot);
        }

        int packageStart = markerIndex + 1;
        string basePackage = string.Join(".", segments.Skip(packageStart));
        string relativeOutputRoot = string.Join("/", segments.Take(packageStart));
        return (basePackage, relativeOutputRoot);
    }
}
