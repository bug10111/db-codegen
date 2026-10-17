using DbCodeGen.Core.Config;
using DbCodeGen.Core.DataSource;
using DbCodeGen.Core.Generation;
using DbCodeGen.Core.Security;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;
using DbCodeGen.McpServer.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Serilog;

// DbCodeGen MCP 服务端入口：本地 stdio 传输（MCP 客户端以子进程方式拉起），
// DI 组合镜像桌面端 App.xaml.cs 减去 UI 项与 AI 项，与桌面端共享同一份 config.json 与模板库。
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// stdout 承载 MCP 协议帧，任何控制台日志提供器都必须移除；日志统一落文件（固定锚定 C:\simple\DbCodeGen.McpServer\logs\）。
// MCP SDK 的逐请求信息级日志保留为工具调用审计轨迹（文件侧有 7 天/20MB 滚动上界）。
builder.Logging.ClearProviders();
builder.Logging.AddSerilog(FileLogBootstrap.CreateLogger(), dispose: true);

// ===== Core 服务组合（镜像 App.xaml.cs 减去 UI/AI 项；SqlExecutor 与备份恢复无 MCP 工具消费面不注册）=====
// 凭据保护与配置持久化：与桌面端共享同一份 config.json，读写经跨进程命名互斥锁串行化
builder.Services.AddSingleton<CredentialProtector>();
builder.Services.AddSingleton<IConfigService, ConfigService>();

// 数据源连接：01 连接服务（连接串构建/测试/打开）与连接管理服务（增删改查唯一 Core 通道）
builder.Services.AddSingleton<IDataSourceService, DataSourceService>();
builder.Services.AddSingleton<IDataSourceManagementService, DataSourceManagementService>();

// 全局类型映射服务：承载"包typeMap>全局映射表>兜底"解析链与生成前未映射预检
builder.Services.AddSingleton<ITypeMappingService, TypeMappingService>();

// 元数据读取：方言工厂 + 表清单/表详情编排
builder.Services.AddSingleton<ISchemaReaderFactory, SchemaReaderFactory>();
builder.Services.AddSingleton<TableCatalogService>();

// 模板包管理服务与共享渲染管线：模板包列表/加载/编辑，文件读写与渲染供工具面消费
builder.Services.AddSingleton<ITemplatePackageService, TemplatePackageService>();
builder.Services.AddSingleton<TemplateFileWriter>();
builder.Services.AddSingleton<TemplateEngine>();

// 批量生成服务与文件写盘服务：供生成与 dry-run 预览工具消费
builder.Services.AddSingleton<IFileWriter, FileWriter>();
builder.Services.AddSingleton<ICodeGenerator, CodeGenerator>();

// ===== MCP 服务端装配 =====
builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new()
    {
        Name = "DbCodeGen",
        Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"
    };
    options.ServerInstructions = "DbCodeGen 代码生成器 MCP 服务端：模板驱动的批量代码生成工具面，六组能力——"
        + "只读枚举：list_template_packages/list_templates/read_template 查包与模板正文，list_datasources/list_tables/get_table_detail 查连接与表结构，get_type_map/check_type_mapping 查看与预检类型映射；"
        + "模板编辑：save_template_content/save_template_edit/create_template_file/rename_template_file/delete_template_file（内置包只读拒绝）；"
        + "模板包管理：create_template_package/copy_template_package/rename_template_package/update_template_package_metadata/delete_template_package（删除需 confirm=true，内置包复制为可写用户包用 copy_template_package）；"
        + "渲染预览：render_template 单模板渲染（免连库，variables 注入 vars.*，modules 渲染 module 作用域片段——手写域/无表场景 table 可省略）、preview_generation 批量 dry-run 零写盘；"
        + "代码生成：generate 批量生成并写盘（内部先重算 dry-run）；"
        + "数据源管理：add_datasource/update_datasource/delete_datasource/test_connection。"
        + "推荐工作流：list_template_packages 选包 → list_templates/read_template 查模板 → 查表两条路（在线 list_tables/get_table_detail；离线合成直接传 tables[].columns，无需连接数据库）→ check_type_mapping 预检未映射类型 → preview_generation 确认文件计划 → generate 写盘。"
        + "关键语义：generate 与 preview_generation 的 tables 按所选模板分型——所选模板（files 过滤后）含 table 作用域条目时必填非空（至少一张表），纯 module/project 作用域集合可省略；模板 scope 三作用域（table 按表集逐表 / module 按 modules 清单逐个 / project 整次一次，同次可混跑）；"
        + "files 为模板过滤清单，传则仅生成清单内模板，省略时选包内全部启用条目；"
        + "duplicateStrategy 仅 overwrite/skip 两值——对含手写代码的输出目录必须用 skip 防覆盖手写文件。"
        + "脚手架包生成协议（包内 README.md.scriban 为该包传参契约的唯一权威，经 read_template 可读；以下为工具面通用契约）："
        + "①业务模块清单经 modules 参数传递且为唯一事实源（如 [{\"name\":\"asset\",\"desc\":\"资料\"}]，元素 name 必填、desc 等其余字段原样注入 module.* 与全部聚合段）；project 作用域模板以根变量 modules 消费，module 作用域仅注入 module.* 单模块上下文、不注入 modules 根变量（多模块聚合段属 project 作用域模板）；显式传 variables 同名键时以 modules 参数为准（该 vars 键已退役，仅作普通透传）。"
        + "②variables 纪律：按包 README 全量传键；值支持任意 JSON 类型；布尔键必须传布尔值（勿传 \"true\" 字符串——非空字符串按真值判定）；业务模块清单一律经 modules 参数传数组（同名 vars 键已退役，仅普通透传不参与消费）；模板内 vars.* 集合循环遇字符串值将被结构化拒绝。"
        + "③路径语义：workspaceRoot 传工程父目录（生成根锚点）；codeDirectory 必须传 src/main/java/<完整包路径>（如 src/main/java/com/simple/demosso），package.dir 由此派生，禁止传 \".\"（包段丢失，Java 文件散落源根）。"
        + "④输出根解析：project 作用域输出根以 workspaceRoot 根锚定；module 作用域 outputPath 相对输出根解析，模板内 ../../../ 可自输出根逃逸到工作区根；table 作用域按 codeDirectory 相对解析。"
        + "⑤pathWarnings（EmptyPackageDir 包段为空 / ConsecutiveSeparators 连续分隔符 / EmptyIteration 模板空迭代 / InvalidJavaPackageSegment 非法 Java 包段）为预检告警：坏路径类（EmptyPackageDir/ConsecutiveSeparators）收到后必须先修正 codeDirectory 或输出路径参数并重新生成，再行写盘；InvalidJavaPackageSegment 表示 src/main/java 之后的包段不是合法 Java 标识符（含连字符/非法字符/数字开头/Java 关键字），会致 package 声明编译失败，模板侧应用 tool.javaIdent 归一化 kebab 模块名（如 user-center→usercenter），数字开头或关键字须改模块名；EmptyIteration 为模板内 for 循环迭代源空集合/null 的零迭代信号（非阻断），收到后核对产物关键段是否缺失，若模板为守卫文案设计（空集合时产物含说明文字属预期）按告警文案指引忽略。"
        + "⑥缺失守卫：缺 modules 或缺关键 vars 键时，部分包模板产出「配置缺失」守卫注释而不是报错；交付前对生成产物全文检索「配置缺失」确认零残留（EmptyIteration 告警同向核对）。"
        + "⑦模板编辑：save_template_content 整篇覆写（行尾保真——宿主文件非空时传入内容自动对齐宿主行尾形态，消除 CRLF→LF 非预期全文件 diff；空宿主文件按传入形态落盘）；save_template_edit 子串级单处精确替换（oldText 须恰好命中一次，0 次报未找到、多次歧义保护并报命中次数；行尾形态差异不阻断匹配；newText 空串=删除、行尾落地遵循宿主；expectedContentHash 可选条件写——传 read_template 回执 contentHash，不匹配拒绝并回显当前值）；create_template_file 的 outputPath 支持管道等模板表达式（渲染期合法化，字面量非法字符由生成侧产物路径校验拦截），可选 scope 声明作用域（table/module/project，缺省 table）——module/project 模板删除重建时必须显式携带 scope 恢复作用域声明，条目作用域经 list_templates 的 scope 字段可见。";
})
    .WithStdioServerTransport()
    // 加性注册：程序集内一切带 [McpServerToolType] 的工具类自动发现并注册（工具类按需经构造注入上述 Core 服务）。
    // 后续新增工具 = 只新增独立工具类文件，无需修改本文件（并行波次物理前提）。
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
return 0;

/// <summary>
/// 程序占位类型：仅用于取入口程序集版本号（顶级语句场景下由编译器生成本类的其余部分）。
/// </summary>
internal partial class Program
{
}
