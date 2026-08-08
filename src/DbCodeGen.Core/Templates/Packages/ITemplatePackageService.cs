namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板包管理服务接口，覆盖模板包列表、单包加载、zip 导入、文件夹导入、复制、导出与删除。
/// 变更操作（导入/复制/删除）在实现内部经串行门互斥，读操作不加锁。
/// </summary>
public interface ITemplatePackageService
{
    /// <summary>
    /// 列出全部模板包：内置包优先、组内按包名字符串升序排序；单个包加载异常不中断整体列表。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>模板包运行时信息列表。</returns>
    Task<IReadOnlyList<TemplatePackageInfo>> ListPackagesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 按包名加载并完整校验单个模板包，内置包与用户包均可加载。
    /// </summary>
    /// <param name="packageName">模板包包名。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>校验通过的模板包运行时信息。</returns>
    /// <exception cref="TemplatePackageException">包不存在或校验失败时抛出。</exception>
    Task<TemplatePackageInfo> LoadPackageAsync(string packageName, CancellationToken cancellationToken);

    /// <summary>
    /// 从 zip 文件导入模板包：校验 zip 条目防穿越与解压上限，先落临时目录再安装到用户模板库。
    /// 与内置包同名一律只读拒绝；与用户包同名需 overwrite=true 覆盖确认。
    /// </summary>
    /// <param name="zipPath">zip 文件绝对路径。</param>
    /// <param name="overwrite">是否允许覆盖同名用户包。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>导入操作结果。</returns>
    Task<TemplatePackageOperationResult> ImportFromZipAsync(string zipPath, bool overwrite, CancellationToken cancellationToken);

    /// <summary>
    /// 从文件夹导入模板包：目录须含 template.json 且校验通过，复制到用户模板库。
    /// 同名覆盖规则与 zip 导入一致。
    /// </summary>
    /// <param name="folderPath">模板包文件夹绝对路径。</param>
    /// <param name="overwrite">是否允许覆盖同名用户包。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>导入操作结果。</returns>
    Task<TemplatePackageOperationResult> ImportFromFolderAsync(string folderPath, bool overwrite, CancellationToken cancellationToken);

    /// <summary>
    /// 复制模板包到用户库的新包名，内置包可复制转可读写用户包；复制后重写清单包名并重新校验。
    /// 新包名与内置包同名一律只读拒绝；与用户包同名需 overwrite=true 覆盖确认。
    /// </summary>
    /// <param name="sourceName">源模板包包名。</param>
    /// <param name="newName">新模板包包名，须符合目录名规则。</param>
    /// <param name="overwrite">是否允许覆盖同名用户包。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>复制操作结果。</returns>
    Task<TemplatePackageOperationResult> CopyPackageAsync(string sourceName, string newName, bool overwrite, CancellationToken cancellationToken);

    /// <summary>
    /// 将模板包导出为 zip 文件：打包 template.json（UTF-8）与包内全部模板文件，内置包与用户包均可导出。
    /// </summary>
    /// <param name="packageName">模板包包名。</param>
    /// <param name="targetZipPath">目标 zip 文件路径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>导出后的 zip 绝对路径。</returns>
    /// <exception cref="TemplatePackageException">包不存在、加载失败或目标路径为空时抛出。</exception>
    Task<string> ExportToZipAsync(string packageName, string targetZipPath, CancellationToken cancellationToken);

    /// <summary>
    /// 删除用户模板包；内置包只读禁止删除，包不存在时抛出异常。删除前的用户确认由调用方负责。
    /// </summary>
    /// <param name="packageName">模板包包名。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <exception cref="TemplatePackageException">内置包只读或包不存在时抛出。</exception>
    Task DeletePackageAsync(string packageName, CancellationToken cancellationToken);
}
