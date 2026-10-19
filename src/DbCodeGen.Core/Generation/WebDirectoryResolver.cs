namespace DbCodeGen.Core.Generation;

/// <summary>
/// web 目录规范化与校验器：webDirectory 必须为相对 workspaceRoot 的相对路径，禁止绝对路径、盘符前缀与 .. 段，
/// 规范化后用于解析 root=web 的模板输出路径。缺省值为 web。
/// </summary>
public static class WebDirectoryResolver
{
    /// <summary>
    /// 缺省 web 目录名（相对 workspaceRoot）。
    /// </summary>
    public const string DefaultWebDirectory = "web";

    /// <summary>
    /// 规范化 web 目录：空白取缺省 web；非空白去空白、反斜杠转正斜杠、折叠重复分隔符与首尾分隔符，
    /// 并拒绝绝对路径、盘符前缀与 .. 段。
    /// </summary>
    /// <param name="webDirectory">调用方传入的 web 目录，可空。</param>
    /// <returns>规范化后的相对目录（正斜杠分隔）。</returns>
    /// <exception cref="ArgumentException">目录为绝对路径、含盘符前缀、含 .. 段或归一后为空时抛出。</exception>
    public static string Normalize(string? webDirectory)
    {
        if (string.IsNullOrWhiteSpace(webDirectory))
        {
            return DefaultWebDirectory;
        }

        string trimmed = webDirectory.Trim();
        if (Path.IsPathRooted(trimmed) || trimmed.Contains(':'))
        {
            throw new ArgumentException(
                $"webDirectory 必须为相对 workspaceRoot 的相对路径，禁止绝对路径与盘符前缀：{webDirectory}", nameof(webDirectory));
        }

        string normalized = trimmed.Replace('\\', '/').Trim('/');
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        foreach (string segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                throw new ArgumentException(
                    $"webDirectory 禁止包含 .. 段：{webDirectory}", nameof(webDirectory));
            }
        }

        if (normalized.Length == 0)
        {
            return DefaultWebDirectory;
        }

        return normalized;
    }
}
