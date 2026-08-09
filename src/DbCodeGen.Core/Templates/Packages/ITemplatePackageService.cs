namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板包管理服务接口，覆盖模板包列表、单包加载、zip 导入、文件夹导入、复制、导出、删除，
/// 以及新建包、新增模板文件、批量追加模板文件与删除模板文件。
/// 变更操作（导入/复制/删除/新建/增删文件/批量追加）在实现内部经串行门互斥，读操作不加锁。
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

    /// <summary>
    /// 新建用户模板包：校验包名后创建包目录并写入 template.json 清单；首模板文件路径可空，
    /// 为空时创建空包（files 为空清单、不创建物理模板文件），非空时顺带创建首个空模板文件。
    /// 与内置包同名一律只读拒绝；与用户包同名返回 NameConflict（新建不走覆盖，由调用方提示改名）。
    /// </summary>
    /// <param name="packageName">新模板包包名，须符合目录名规则。</param>
    /// <param name="description">包说明，可为空。</param>
    /// <param name="firstTemplatePath">首模板文件相对包根路径，可含分组目录；为空时创建空包，非空时须防目录穿越安全。</param>
    /// <param name="firstOutputPath">首模板文件输出相对路径；首模板路径非空时须防目录穿越校验。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>创建操作结果，成功时 Package 携带新包信息。</returns>
    Task<TemplatePackageOperationResult> CreatePackageAsync(
        string packageName, string description, string? firstTemplatePath, string? firstOutputPath, CancellationToken cancellationToken);

    /// <summary>
    /// 向用户模板包新增一个空模板文件：建空文件并同步追加 manifest files 条目（enabled=true）后重写清单。
    /// 内置包只读拒绝；目标文件已存在返回失败。
    /// </summary>
    /// <param name="packageName">目标用户模板包包名。</param>
    /// <param name="templateRelativePath">新增模板文件相对包根路径，可含分组目录，须防目录穿越安全。</param>
    /// <param name="outputPath">新增模板文件的输出相对路径，同样防目录穿越校验。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>新增操作结果，成功时 Package 携带更新后的包信息。</returns>
    Task<TemplatePackageOperationResult> AddTemplateFileAsync(
        string packageName, string templateRelativePath, string outputPath, CancellationToken cancellationToken);

    /// <summary>
    /// 从用户模板包删除一个模板文件：删除文件并同步移除 manifest files 对应条目后重写清单。
    /// 内置包只读拒绝；文件不存在返回失败；允许删除最后一个模板，删除后包变为空包（空 files 清单合法）。
    /// </summary>
    /// <param name="packageName">目标用户模板包包名。</param>
    /// <param name="templateRelativePath">待删除模板文件相对包根路径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>删除操作结果，成功时 Package 携带更新后的包信息。</returns>
    Task<TemplatePackageOperationResult> DeleteTemplateFileAsync(
        string packageName, string templateRelativePath, CancellationToken cancellationToken);

    /// <summary>
    /// 向用户模板包批量追加模板文件（带内容）：内置只读拒绝、逐条目路径安全与已存在预检，
    /// 全部通过后写文件并追加 manifest 条目重写清单，失败回滚已写文件，返回更新后包。
    /// 模板相对路径可含分组目录，输出路径支持 {{变量}} 占位，两者均须防目录穿越。
    /// </summary>
    /// <param name="packageName">目标用户模板包包名。</param>
    /// <param name="files">待追加的模板文件写入条目列表，至少一个条目。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>追加操作结果，成功时 Package 携带更新后的包信息。</returns>
    Task<TemplatePackageOperationResult> AppendTemplateFilesAsync(
        string packageName, IReadOnlyList<TemplateFileWriteEntry> files, CancellationToken cancellationToken);
}
