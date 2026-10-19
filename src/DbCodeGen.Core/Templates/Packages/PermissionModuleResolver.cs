using DbCodeGen.Core.Generation;

namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 权限模块派生器：统一 Controller 权限注解与脚手架权限点清单的权限模块来源，
/// 避免「包名末段」与「模块 kebab 名」两套派生口径并存导致运行期 403。
/// 优先级：请求 variables.permissionModule &gt; manifest.permissionModule
/// &gt; modules 非空时 Hump3Underline(modules[0].name) &gt; Hump3Underline(基础包名末段)。
/// 调用方传了 modules 即自动与脚手架权限点清单的模块 kebab 口径一致，无需手工传 permissionModule。
/// </summary>
public static class PermissionModuleResolver
{
    /// <summary>
    /// 变量名：请求侧显式指定权限模块。
    /// </summary>
    public const string VariableKey = "permissionModule";

    /// <summary>
    /// 解析生效权限模块。
    /// </summary>
    /// <param name="basePackage">生效基础包名（可能为空）。</param>
    /// <param name="manifestPermissionModule">manifest 声明的权限模块，可空。</param>
    /// <param name="variables">本次请求的调用方变量，可空。</param>
    /// <param name="modules">本次请求的模块清单，可空；非空时取首模块名 kebab 形态作为兜底派生源。</param>
    /// <returns>生效权限模块；无法派生时返回空串。</returns>
    public static string Resolve(
        string? basePackage,
        string? manifestPermissionModule,
        IReadOnlyDictionary<string, object?>? variables,
        IReadOnlyList<ModuleContext>? modules = null)
    {
        string? variableValue = PolicyValueReader.GetString(variables, VariableKey);
        if (variableValue is not null)
        {
            return variableValue;
        }

        if (!string.IsNullOrWhiteSpace(manifestPermissionModule))
        {
            return manifestPermissionModule.Trim();
        }

        // 调用方传了 modules（业务模块清单唯一事实源）时，自动与脚手架权限点清单的模块 kebab 口径一致；
        // 多模块按模块分别生成时，每次调用传该模块的 modules 即可
        if (modules is { Count: > 0 } && !string.IsNullOrWhiteSpace(modules[0].Name))
        {
            return ToolFunctions.Hump3Underline(modules[0].Name);
        }

        return DeriveFromBasePackage(basePackage);
    }

    /// <summary>
    /// 由基础包名末段派生权限模块：基础包名点号分隔取末段，再按 kebab 口径转换（如 UserCenter → user-center）。
    /// </summary>
    /// <param name="basePackage">基础包名，可空。</param>
    /// <returns>权限模块；无法派生时返回空串。</returns>
    public static string DeriveFromBasePackage(string? basePackage)
    {
        if (string.IsNullOrWhiteSpace(basePackage))
        {
            return string.Empty;
        }

        string[] segments = basePackage.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return string.Empty;
        }

        return ToolFunctions.Hump3Underline(segments[^1]);
    }

    /// <summary>
    /// 组装权限键：{permissionModule}:{op}；op 为空时仅返回权限模块。
    /// </summary>
    /// <param name="permissionModule">生效权限模块。</param>
    /// <param name="operation">操作名（view/edit/export/delete 等），可空。</param>
    /// <returns>权限键。</returns>
    /// <exception cref="ArgumentNullException">permissionModule 为 null 时抛出。</exception>
    public static string BuildKey(string permissionModule, string? operation)
    {
        ArgumentNullException.ThrowIfNull(permissionModule);
        if (string.IsNullOrWhiteSpace(operation))
        {
            return permissionModule;
        }

        return $"{permissionModule}:{operation.Trim()}";
    }
}
