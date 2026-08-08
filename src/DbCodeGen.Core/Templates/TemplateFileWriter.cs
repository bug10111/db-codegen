using System.Text;
using DbCodeGen.Core.Templates.Packages;

namespace DbCodeGen.Core.Templates;

/// <summary>
/// 模板文件读写服务，承载模板文本读取、保存写回、防目录穿越、内置包只读拒绝与写盘后 Content 缓存失效。
/// 写盘成功会派发"文件已更新"事件，通知模板包刷新，保证批量生成直读磁盘时拾取已编辑版本。
/// </summary>
public sealed class TemplateFileWriter
{
    /// <summary>
    /// 文件已更新事件，写盘成功并失效 Content 缓存后派发；参数携带包名与相对路径。
    /// </summary>
    public event EventHandler<TemplateFileChangedEventArgs>? FileUpdated;

    /// <summary>
    /// 读取模板文件文本，相对路径经防目录穿越校验后解析到包根目录内，UTF-8 去 BOM 解码。
    /// </summary>
    /// <param name="package">模板包运行时信息，提供包根目录。</param>
    /// <param name="relativePath">模板文件相对包根的路径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>模板文本。</returns>
    /// <exception cref="TemplatePackageException">相对路径为空、含绝对路径、盘符前缀或 .. 段越出包根时抛出。</exception>
    public async Task<string> ReadAsync(TemplatePackageInfo package, string relativePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new TemplatePackageException("模板文件相对路径不能为空。");
        }

        // 防目录穿越：相对路径规范化并校验落在包根目录内
        string fullPath = ResolveSafePath(package, relativePath);
        return await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 保存模板文本到包内文件：内置包只读拒绝，相对路径越界拒绝，UTF-8 无 BOM 异步写盘并自动建目录；
    /// 写盘成功后失效该文件 Content 缓存并派发"文件已更新"事件。
    /// </summary>
    /// <param name="package">模板包运行时信息。</param>
    /// <param name="relativePath">模板文件相对包根的路径。</param>
    /// <param name="content">待写盘的模板文本。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>保存结果，成功 / 内置包只读拒绝 / 路径越界 / IO 失败。</returns>
    public async Task<TemplateSaveResult> WriteAsync(
        TemplatePackageInfo package,
        string relativePath,
        string content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return TemplateSaveResult.Failure("模板文件相对路径不能为空。");
        }

        // 内置包只读安全边界：保存写回一律拒绝，UI 引导先复制到用户库
        if (package.IsBuiltin)
        {
            return TemplateSaveResult.ReadOnlyBuiltin($"内置包 {package.Name} 只读，请先复制到用户库后再编辑保存。");
        }

        string fullPath;
        try
        {
            fullPath = ResolveSafePath(package, relativePath);
        }
        catch (TemplatePackageException exception)
        {
            return TemplateSaveResult.PathTraversal(exception.Message);
        }

        try
        {
            // 自动建父目录并以 UTF-8 无 BOM 编码异步写盘
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return TemplateSaveResult.Failure($"保存模板文件失败：{exception.Message}");
        }

        // 写盘成功：失效该文件 Content 缓存并派发"文件已更新"事件，杜绝后续读到旧模板
        InvalidateContentCache(package, relativePath);
        FileUpdated?.Invoke(this, new TemplateFileChangedEventArgs(package.Name, NormalizeRelative(relativePath)));
        return TemplateSaveResult.Success();
    }

    /// <summary>
    /// 将相对路径解析为包根目录内绝对路径，复用包加载器的防目录穿越校验。
    /// </summary>
    /// <param name="package">模板包运行时信息。</param>
    /// <param name="relativePath">相对包根的路径。</param>
    /// <returns>包根目录内的绝对路径。</returns>
    /// <exception cref="TemplatePackageException">路径越出包根目录或含非法字符时抛出。</exception>
    private static string ResolveSafePath(TemplatePackageInfo package, string relativePath)
    {
        try
        {
            return TemplatePackageLoader.ResolveWithinRoot(package.RootPath, relativePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            // 非法路径字符等异常统一转换为模板包异常，保证防穿越契约对外一致
            throw new TemplatePackageException($"模板文件相对路径不合法：{relativePath}", exception);
        }
    }

    /// <summary>
    /// 按规范化相对路径匹配并清空对应模板文件的内容缓存。
    /// </summary>
    private static void InvalidateContentCache(TemplatePackageInfo package, string relativePath)
    {
        string normalized = NormalizeRelative(relativePath);
        foreach (TemplateFileInfo file in package.Files)
        {
            if (string.Equals(NormalizeRelative(file.RelativeTemplatePath), normalized, StringComparison.OrdinalIgnoreCase))
            {
                file.Content = null;
                return;
            }
        }
    }

    /// <summary>
    /// 规范化相对路径为正斜杠形式，供缓存匹配与事件参数统一。
    /// </summary>
    private static string NormalizeRelative(string relativePath)
    {
        return TemplatePackageLoader.NormalizeRelativePath(relativePath);
    }

    /// <summary>
    /// 文件已更新事件参数，携带模板包名与规范化相对路径，订阅方据此刷新对应模板内容。
    /// </summary>
    public sealed class TemplateFileChangedEventArgs : EventArgs
    {
        /// <summary>
        /// 所属模板包名。
        /// </summary>
        public string PackageName { get; }

        /// <summary>
        /// 更新的模板文件相对包根路径（正斜杠规范化）。
        /// </summary>
        public string RelativePath { get; }

        /// <summary>
        /// 使用包名与相对路径构造事件参数。
        /// </summary>
        /// <param name="packageName">所属模板包名。</param>
        /// <param name="relativePath">更新的模板文件相对路径。</param>
        public TemplateFileChangedEventArgs(string packageName, string relativePath)
        {
            PackageName = packageName;
            RelativePath = relativePath;
        }
    }
}
