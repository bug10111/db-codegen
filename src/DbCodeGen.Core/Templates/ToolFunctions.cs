using System.Text;
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
    /// 并提供可选全局映射服务下的导包能力。本工程 Scriban 不接受裸委托注册，函数全部经 IScriptCustomFunction 包装器暴露。
    /// </summary>
    /// <param name="packageContext">package 侧渲染上下文，提供当前包类型映射表。</param>
    /// <param name="typeMappingService">全局类型映射解析服务；为空时 type 退化为按包 typeMap 的旧行为，导包函数返回空串。</param>
    /// <returns>含 firstLowerCase/firstUpperCase/hump2Underline/hump3Underline/type/typeImport/imports 的 tool 脚本对象。</returns>
    /// <exception cref="ArgumentNullException">packageContext 为 null 时抛出。</exception>
    public static ScriptObject Build(TemplatePackageContext packageContext, ITypeMappingService? typeMappingService = null)
    {
        ArgumentNullException.ThrowIfNull(packageContext);

        var tool = new ScriptObject();
        tool.SetValue("firstLowerCase", new ScriptToolFunction(args => FirstLowerCase(args.FirstOrDefault()?.ToString())), true);
        tool.SetValue("firstUpperCase", new ScriptToolFunction(args => FirstUpperCase(args.FirstOrDefault()?.ToString())), true);
        tool.SetValue("hump2Underline", new ScriptToolFunction(args => Hump2Underline(args.FirstOrDefault()?.ToString())), true);
        tool.SetValue("hump3Underline", new ScriptToolFunction(args => Hump3Underline(args.FirstOrDefault()?.ToString())), true);

        // 类型映射闭包当前包类型映射表与全局映射服务：有服务时走"全局表>包typeMap>兜底"解析链，否则按旧包行为
        tool.SetValue("type", new ScriptToolFunction(args => ResolveType(args.FirstOrDefault()?.ToString(), packageContext.TypeMap, typeMappingService)), true);

        // 单类型导包：返回映射条目声明的导包（如 java.math.BigDecimal），无导包需求时返回空串
        tool.SetValue("typeImport", new ScriptToolFunction(args => ResolveImport(args.FirstOrDefault()?.ToString(), packageContext.TypeMap, typeMappingService)), true);

        // 列集合导包块：对传入的列集合/表去重后生成 import 语句块，供实体模板自动导包，无导包时返回空串
        tool.SetValue("imports", new ScriptToolFunction(args => BuildImportsBlock(args, packageContext.TypeMap, typeMappingService)), true);
        return tool;
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
    /// 将驼峰命名转为下划线全大写命名（常量风格），空串或 null 返回空串。
    /// </summary>
    /// <param name="value">驼峰命名字符串，如 SysUser。</param>
    /// <returns>下划线全大写命名，如 SYS_USER。</returns>
    public static string Hump3Underline(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return Hump2Underline(value).ToUpperInvariant();
    }

    /// <summary>
    /// 解析数据库原始类型为目标语言类型：有全局映射服务时走完整解析链，否则按包 typeMap 旧行为。
    /// </summary>
    /// <param name="dbType">数据库原始类型，可为空。</param>
    /// <param name="packageTypeMap">当前包 manifest 类型映射表。</param>
    /// <param name="service">全局类型映射服务，可为空。</param>
    /// <returns>目标语言类型名。</returns>
    private static string ResolveType(string? dbType, IReadOnlyDictionary<string, string> packageTypeMap, ITypeMappingService? service)
    {
        return service is null
            ? TypeMapper.MapType(dbType, packageTypeMap)
            : service.Resolve(dbType, packageTypeMap).TypeName;
    }

    /// <summary>
    /// 解析数据库原始类型的导包：无全局映射服务或映射条目未声明导包时返回空串。
    /// </summary>
    /// <param name="dbType">数据库原始类型，可为空。</param>
    /// <param name="packageTypeMap">当前包 manifest 类型映射表。</param>
    /// <param name="service">全局类型映射服务，可为空。</param>
    /// <returns>映射条目声明的导包路径；无导包需求时为空串。</returns>
    private static string ResolveImport(string? dbType, IReadOnlyDictionary<string, string> packageTypeMap, ITypeMappingService? service)
    {
        if (service is null || string.IsNullOrWhiteSpace(dbType))
        {
            return string.Empty;
        }

        TypeMappingResult result = service.Resolve(dbType, packageTypeMap);
        return result.Import ?? string.Empty;
    }

    /// <summary>
    /// 对传入参数提取全部数据库原始类型，逐个解析导包并去重排序，生成导包语句块。
    /// 参数可为列脚本对象、表脚本对象（取全量列）、列集合脚本数组或单类型字符串。
    /// </summary>
    /// <param name="args">tool.imports 的调用参数数组。</param>
    /// <param name="packageTypeMap">当前包 manifest 类型映射表。</param>
    /// <param name="service">全局类型映射服务，可为空。</param>
    /// <returns>去重排序后的导包语句块，无导包时返回空串。</returns>
    private static string BuildImportsBlock(object?[] args, IReadOnlyDictionary<string, string> packageTypeMap, ITypeMappingService? service)
    {
        var dbTypes = new List<string>();
        foreach (object? arg in args)
        {
            CollectDbTypes(arg, dbTypes);
        }

        var imports = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string dbType in dbTypes)
        {
            string import = ResolveImport(dbType, packageTypeMap, service);
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
