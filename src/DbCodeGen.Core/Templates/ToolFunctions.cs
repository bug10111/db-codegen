using System.Text;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates.Packages;
using Scriban;
using Scriban.Runtime;
using Scriban.Syntax;

namespace DbCodeGen.Core.Templates;

/// <summary>
/// 模板 tool 函数集工厂，构建可注入渲染上下文的 tool 脚本对象。
/// 提供首字母大小写、驼峰转下划线等字符串函数与按当前包类型映射表的类型映射函数。
/// </summary>
public static class ToolFunctions
{
    /// <summary>
    /// 构建 tool 脚本对象；type 函数按当前包 manifest 类型映射表实时计算映射结果，
    /// 并提供可选全局映射服务下的按数据库类型解析与导包能力。本工程 Scriban 不接受裸委托注册，函数全部经 IScriptCustomFunction 包装器暴露。
    /// </summary>
    /// <param name="packageContext">package 侧渲染上下文，提供当前包类型映射表。</param>
    /// <param name="typeMappingService">全局类型映射解析服务；为空时 type 退化为按包 typeMap 的旧行为，导包函数返回空串。</param>
    /// <param name="databaseType">当前表所属数据库类型，供全局映射按库分桶匹配；为空时仅命中通用条目。</param>
    /// <returns>含 firstLowerCase/firstUpperCase/hump2Underline/hump3Underline/javaIdent/type/typeImport/imports/
    /// packageSegments/packagePath/permissionKey 的 tool 脚本对象（permissionKey 支持单参 op 与双参 moduleName+op 两形态）。</returns>
    /// <exception cref="ArgumentNullException">packageContext 为 null 时抛出。</exception>
    public static ScriptObject Build(TemplatePackageContext packageContext, ITypeMappingService? typeMappingService = null, DataSourceType? databaseType = null)
    {
        ArgumentNullException.ThrowIfNull(packageContext);

        var tool = new ScriptObject();
        tool.SetValue("firstLowerCase", new ScriptToolFunction(args => FirstLowerCase(args.FirstOrDefault()?.ToString())), true);
        tool.SetValue("firstUpperCase", new ScriptToolFunction(args => FirstUpperCase(args.FirstOrDefault()?.ToString())), true);
        tool.SetValue("hump2Underline", new ScriptToolFunction(args => Hump2Underline(args.FirstOrDefault()?.ToString())), true);
        tool.SetValue("hump3Underline", new ScriptToolFunction(args => Hump3Underline(args.FirstOrDefault()?.ToString())), true);
        tool.SetValue("javaIdent", new ScriptToolFunction(args => JavaIdent(args.FirstOrDefault()?.ToString())), true);

        // 类型映射闭包当前包类型映射表、全局映射服务与数据库类型：有服务时走"包typeMap>库专属>通用>兜底"解析链，否则按旧包行为
        tool.SetValue("type", new ScriptToolFunction(args => ResolveType(args.FirstOrDefault()?.ToString(), packageContext.TypeMap, typeMappingService, databaseType)), true);

        // 单类型导包：返回映射条目声明的导包（如 java.math.BigDecimal），无导包需求时返回空串
        tool.SetValue("typeImport", new ScriptToolFunction(args => ResolveImport(args.FirstOrDefault()?.ToString(), packageContext.TypeMap, typeMappingService, databaseType)), true);

        // 列集合导包块：对传入的列集合/表去重后生成 import 语句块，供实体模板自动导包，无导包时返回空串
        tool.SetValue("imports", new ScriptToolFunction(args => BuildImportsBlock(args, packageContext.TypeMap, typeMappingService, databaseType)), true);

        // 包路径组装：把基础包段与追加层段归一（小写、kebab 归一、连续重复段去重）后组装，
        // 供输出路径模板用 {{tool.packagePath("common","entity",table.variableName)}} 生成合法包目录，杜绝无条件下硬拼 common
        tool.SetValue("packageSegments", new ScriptToolFunction(args => BuildPackageSegments(args, packageContext)), true);
        tool.SetValue("packagePath", new ScriptToolFunction(args => PackagePathBuilder.ToDirectoryPath(BuildPackageSegments(args, packageContext))), true);

        // 权限键：tool.permissionKey(op) = {package.permissionModule}:{op}（单参恒按 op 处理，不做模块名猜测）；
        // tool.permissionKey(moduleName, op) = {Hump3Underline(moduleName)}:{op}（脚手架权限点清单逐模块使用，
        // 与 Controller 的单参形态同源；调用方传了 modules 时 package.permissionModule 已自动等于该模块 kebab 形态）
        tool.SetValue("permissionKey", new ScriptToolFunction(args =>
        {
            if (args.Length >= 2)
            {
                return PermissionModuleResolver.BuildKey(
                    Hump3Underline(args[0]?.ToString()),
                    args[1]?.ToString());
            }

            return PermissionModuleResolver.BuildKey(packageContext.PermissionModule, args.FirstOrDefault()?.ToString());
        }), true);
        return tool;
    }

    /// <summary>
    /// 组装包段清单：在基础包段之后追加调用方传入的层段（支持点号/斜杠分隔的多级），
    /// 逐段按 packageSegmentCase 口径归一并做连续重复段去重。
    /// </summary>
    /// <param name="args">tool.packageSegments / tool.packagePath 的调用参数数组，元素可为空。</param>
    /// <param name="packageContext">package 侧渲染上下文，提供基础包段与包段口径。</param>
    /// <returns>归一后的完整包段清单。</returns>
    private static IReadOnlyList<string> BuildPackageSegments(object?[] args, TemplatePackageContext packageContext)
    {
        var extras = new string?[args.Length];
        for (int index = 0; index < args.Length; index++)
        {
            extras[index] = args[index]?.ToString();
        }

        return PackagePathBuilder.Compose(packageContext.Segments, packageContext.Naming.PackageSegmentCase, extras);
    }

    /// <summary>
    /// 将字符串首字母转为小写，空串或 null 返回空串。
    /// </summary>
    /// <param name="value">输入字符串。</param>
    /// <returns>首字母小写后的字符串。</returns>
    public static string FirstLowerCase(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return char.ToLowerInvariant(value[0]) + value[1..];
    }

    /// <summary>
    /// 将字符串首字母转为大写，空串或 null 返回空串。
    /// </summary>
    /// <param name="value">输入字符串。</param>
    /// <returns>首字母大写后的字符串。</returns>
    public static string FirstUpperCase(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    /// <summary>
    /// 将驼峰命名转为下划线小写命名，按大小写边界断词并在缩写边界处插入下划线。
    /// </summary>
    /// <param name="value">驼峰命名字符串，如 SysUser、SysURLConfig。</param>
    /// <returns>下划线小写命名，如 sys_user、sys_url_config。</returns>
    public static string Hump2Underline(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length + 8);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (!char.IsUpper(character))
            {
                builder.Append(character);
                continue;
            }

            // 大写字母前插入下划线：前一个字符为小写/数字，或前一个是连续大写且后一个是小写（缩写边界）
            bool previousLowerOrDigit = index > 0 && (char.IsLower(value[index - 1]) || char.IsDigit(value[index - 1]));
            bool acronymBoundary = index > 0
                && char.IsUpper(value[index - 1])
                && index + 1 < value.Length
                && char.IsLower(value[index + 1]);
            if (previousLowerOrDigit || acronymBoundary)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    /// <summary>
    /// 将驼峰命名转为短横线小写命名（kebab-case），空串或 null 返回空串。
    /// 先经下划线小写转换再替换分隔符为短横线，与 hump2Underline 共享同一断词规则。
    /// </summary>
    /// <param name="value">驼峰命名字符串，如 SysUser。</param>
    /// <returns>短横线小写命名，如 sys-user。</returns>
    public static string Hump3Underline(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return Hump2Underline(value).Replace('_', '-');
    }

    /// <summary>
    /// 将命名归一化为合法 Java 标识符形态：把驼峰命名大小写归一为全小写并删除 kebab 连字符，
    /// 得到小写无连字符结果（如 user-center→usercenter、UserCenter→usercenter）。
    /// 驼峰词边界分隔符在删除连字符后不产生任何字符，故实现等价于"大写降小写 + 删除连字符"；下划线保留（合法标识符字符）。
    /// 本函数只管 kebab 归一化，不静默吞其它非法字符（空格/点等保留原样，交由路径预检告警）。
    /// </summary>
    /// <param name="value">模块名等命名来源，如 user-center、org-member、asset。</param>
    /// <returns>归一化后的 Java 标识符形态字符串，如 usercenter、orgmember、asset。</returns>
    public static string JavaIdent(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            // 连字符是 kebab 分隔符，归一化后删除；其余字符统一降小写（保留下划线与其它字符）
            if (character == '-')
            {
                continue;
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    /// <summary>
    /// 解析数据库原始类型为目标语言类型：有全局映射服务时走完整解析链，否则按包 typeMap 旧行为。
    /// </summary>
    /// <param name="dbType">数据库原始类型，可为空。</param>
    /// <param name="packageTypeMap">当前包 manifest 类型映射表。</param>
    /// <param name="service">全局类型映射服务，可为空。</param>
    /// <param name="databaseType">当前表所属数据库类型，可为空。</param>
    /// <returns>目标语言类型名。</returns>
    private static string ResolveType(string? dbType, IReadOnlyDictionary<string, string> packageTypeMap, ITypeMappingService? service, DataSourceType? databaseType)
    {
        return service is null
            ? TypeMapper.MapType(dbType, packageTypeMap)
            : service.Resolve(dbType, databaseType, packageTypeMap).TypeName;
    }

    /// <summary>
    /// 解析数据库原始类型的导包：无全局映射服务或映射条目未声明导包时返回空串。
    /// </summary>
    /// <param name="dbType">数据库原始类型，可为空。</param>
    /// <param name="packageTypeMap">当前包 manifest 类型映射表。</param>
    /// <param name="service">全局类型映射服务，可为空。</param>
    /// <param name="databaseType">当前表所属数据库类型，可为空。</param>
    /// <returns>映射条目声明的导包路径；无导包需求时为空串。</returns>
    private static string ResolveImport(string? dbType, IReadOnlyDictionary<string, string> packageTypeMap, ITypeMappingService? service, DataSourceType? databaseType)
    {
        if (service is null || string.IsNullOrWhiteSpace(dbType))
        {
            return string.Empty;
        }

        TypeMappingResult result = service.Resolve(dbType, databaseType, packageTypeMap);
        return result.Import ?? string.Empty;
    }

    /// <summary>
    /// 对传入参数提取全部数据库原始类型，逐个解析导包并去重排序，生成导包语句块。
    /// 参数可为列脚本对象、表脚本对象（取全量列）、列集合脚本数组或单类型字符串。
    /// </summary>
    /// <param name="args">tool.imports 的调用参数数组。</param>
    /// <param name="packageTypeMap">当前包 manifest 类型映射表。</param>
    /// <param name="service">全局类型映射服务，可为空。</param>
    /// <param name="databaseType">当前表所属数据库类型，可为空。</param>
    /// <returns>去重排序后的导包语句块，无导包时返回空串。</returns>
    private static string BuildImportsBlock(object?[] args, IReadOnlyDictionary<string, string> packageTypeMap, ITypeMappingService? service, DataSourceType? databaseType)
    {
        var dbTypes = new List<string>();
        foreach (object? arg in args)
        {
            CollectDbTypes(arg, dbTypes);
        }

        var imports = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string dbType in dbTypes)
        {
            string import = ResolveImport(dbType, packageTypeMap, service, databaseType);
            if (!string.IsNullOrWhiteSpace(import))
            {
                imports.Add(import);
            }
        }

        if (imports.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (string import in imports)
        {
            builder.Append("import ");
            builder.Append(import);
            builder.AppendLine(";");
        }

        return builder.ToString();
    }

    /// <summary>
    /// 递归收集参数中的数据库原始类型：列对象取 rawDbType 字段，表对象取其全量列集合，集合逐项递归，字符串直接收集。
    /// </summary>
    /// <param name="value">待解析的脚本对象、集合或字符串。</param>
    /// <param name="target">收集到的原始类型列表。</param>
    private static void CollectDbTypes(object? value, List<string> target)
    {
        switch (value)
        {
            case ScriptArray array:
                foreach (object? item in array)
                {
                    CollectDbTypes(item, target);
                }

                break;
            case ScriptObject scriptObject:
                // 列脚本对象直接提取 rawDbType 字段
                if (scriptObject.ContainsKey("rawDbType") && scriptObject["rawDbType"] is not null)
                {
                    string? dbType = scriptObject["rawDbType"]!.ToString();
                    if (!string.IsNullOrWhiteSpace(dbType))
                    {
                        target.Add(dbType);
                    }

                    break;
                }

                // 表脚本对象遍历全量列集合
                if (scriptObject.ContainsKey("fullColumn") && scriptObject["fullColumn"] is ScriptArray columnArray)
                {
                    foreach (object? item in columnArray)
                    {
                        CollectDbTypes(item, target);
                    }
                }

                break;
            case string text when !string.IsNullOrWhiteSpace(text):
                target.Add(text);
                break;
        }
    }

    /// <summary>
    /// Scriban 脚本函数包装器，将参数数组委托包装为可被模板调用的脚本函数。
    /// 本工程 Scriban 7.2.6 不直接接受 Func 委托，须经本接口包装后方可在模板中调用。
    /// </summary>
    private sealed class ScriptToolFunction : IScriptCustomFunction
    {
        private readonly Func<object?[], object?> _implementation;

        /// <summary>
        /// 使用参数数组委托创建脚本函数包装器。
        /// </summary>
        /// <param name="implementation">接收参数数组并返回结果的委托。</param>
        public ScriptToolFunction(Func<object?[], object?> implementation)
        {
            _implementation = implementation;
        }

        /// <inheritdoc />
        public int RequiredParameterCount => 0;

        /// <inheritdoc />
        public int ParameterCount => 0;

        /// <inheritdoc />
        public ScriptVarParamKind VarParamKind => ScriptVarParamKind.Direct;

        /// <inheritdoc />
        public Type ReturnType => typeof(object);

        /// <inheritdoc />
        public ScriptParameterInfo GetParameterInfo(int index)
        {
            return new ScriptParameterInfo(typeof(object), $"arg{index}");
        }

        /// <inheritdoc />
        public object? Invoke(TemplateContext context, ScriptNode? callerContext, ScriptArray arguments, ScriptBlockStatement? blockStatement)
        {
            return _implementation(arguments.ToArray());
        }

        /// <inheritdoc />
        public ValueTask<object?> InvokeAsync(TemplateContext context, ScriptNode? callerContext, ScriptArray arguments, ScriptBlockStatement? blockStatement)
        {
            return new ValueTask<object?>(_implementation(arguments.ToArray()));
        }
    }
}
