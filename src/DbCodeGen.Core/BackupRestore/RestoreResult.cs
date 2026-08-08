namespace DbCodeGen.Core.BackupRestore;

/// <summary>
/// 恢复操作结果，区分需覆盖确认与已完成两种状态，并携带恢复后需用户重新配置的密码与 apiKey 提示。
/// </summary>
public sealed class RestoreResult
{
    /// <summary>
    /// 恢复前检测到同名用户包且未允许覆盖时为 true，此时不执行任何写盘，等待调用方确认后重试。
    /// </summary>
    public bool NeedsConfirmation { get; init; }

    /// <summary>
    /// 检测到同名冲突的用户模板包名清单。
    /// </summary>
    public IReadOnlyList<string> ConflictingPackageNames { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 已完成还原的用户模板包名清单。
    /// </summary>
    public IReadOnlyList<string> RestoredPackageNames { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 恢复后需要重新输入密码的数据源连接名清单。
    /// </summary>
    public IReadOnlyList<string> PasswordRequiredDataSources { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 恢复后是否需要重新配置 LLM apiKey。
    /// </summary>
    public bool LlmNeedsReconfigure { get; init; }

    /// <summary>
    /// 构造需覆盖确认的结果，此时未执行任何写盘操作。
    /// </summary>
    /// <param name="conflictingPackages">存在同名冲突的用户模板包名清单。</param>
    /// <returns>需确认状态的恢复结果。</returns>
    public static RestoreResult ConfirmationRequired(IReadOnlyList<string> conflictingPackages)
    {
        return new RestoreResult
        {
            NeedsConfirmation = true,
            ConflictingPackageNames = conflictingPackages
        };
    }

    /// <summary>
    /// 构造恢复完成的结果。
    /// </summary>
    /// <param name="restoredPackages">已还原的用户模板包名清单。</param>
    /// <param name="passwordRequiredDataSources">需重输密码的数据源连接名清单。</param>
    /// <param name="llmNeedsReconfigure">是否需要重配 LLM apiKey。</param>
    /// <returns>已完成状态的恢复结果。</returns>
    public static RestoreResult Succeeded(
        IReadOnlyList<string> restoredPackages,
        IReadOnlyList<string> passwordRequiredDataSources,
        bool llmNeedsReconfigure)
    {
        return new RestoreResult
        {
            NeedsConfirmation = false,
            RestoredPackageNames = restoredPackages,
            PasswordRequiredDataSources = passwordRequiredDataSources,
            LlmNeedsReconfigure = llmNeedsReconfigure
        };
    }
}
