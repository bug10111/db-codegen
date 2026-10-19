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
    options.ServerInstructions = """
===== DbCodeGen MCP 服务端 =====
模板驱动的批量代码生成工具面：后端 View/DTO/Repository + 前端页面/路由/菜单/权限点。服务端零 LLM 调用，只暴露 25 个工具。

【1. 能力总览】
- 包类型（manifest.role）：backend / scaffold / web / fullstack / utility；技术栈 stack：java / net / web / mixed。
- 两形态：第一形态 = 业务 web 只承载业务页面，平台管理页由授权中心控制台承载；第二形态 = 业务 web 自治承载用户/角色/菜单/部门/租户/字典管理页，全部 web 直连授权中心，业务后端零平台类/零平台端点。
- 生成面：后端 View/DTO/Repository（view = 后端 Java View，只做数据处理，不含前端页面）；前端页面/路由/菜单/权限点（role=webApi/webDto/webPage/webRoute/webMenu）。
- 工具分组：只读枚举（list_template_packages/list_templates/read_template/list_datasources/list_tables/get_table_detail/get_type_map/check_type_mapping）、模板编辑（save_template_content/save_template_edit/create_template_file/rename_template_file/delete_template_file）、模板包管理（create_template_package/copy_template_package/rename_template_package/update_template_package_metadata/delete_template_package）、渲染预览（render_template/preview_generation）、代码生成（generate）、数据源管理（add_datasource/update_datasource/delete_datasource/test_connection）。

【2. 标准工作流】
1) 选包：list_template_packages（按 role/stack/hasBackendView/hasWebPage 与 presets 判断用途）。
2) 查契约/模板：list_templates / read_template（manifest.contractPath 为包传参契约唯一权威；presets 摘要同时可见）。
3) 查表：在线 list_tables → get_table_detail；离线合成直接内联 tables[].columns（无需连接数据库）。
4) 类型预检：check_type_mapping（与生成同 datasource/tables/package）。
5) 预览：preview_generation（核对 entries + namingReport + pathWarnings + coverageWarnings + platformTableExcluded；坏路径/命名类告警必须先修正参数或模板再生成）。
6) 生成：generate（内部重算 dry-run 后写盘，无确认弹窗）。
7) 验证：回执 generated/failed + logs；生成物逐文件核对设计契约，禁止把生成物直接当设计达成。

【3. 核心参数表】（preview_generation / generate 共用）
| 参数 | 类型 | 必填 | 默认 | 含义与约束 |
| package | string | 是 | — | 模板包包名 |
| workspaceRoot | string | 是 | — | 输出根前缀绝对路径；最终路径必须落在其内 |
| codeDirectory | string | 是 | — | 项目内完整相对路径含包名（如 src/main/java/com/example/demo）；派生相对输出根与基础包名 |
| tables | array<object> | 条件 | null | 所选模板含 table 作用域条目时必填非空；元素 name 必填、columns[] 可选；可原样透传 get_table_detail 的列 |
| files | array<string> | 否 | null | 模板过滤清单；与 preset 互斥；省略时取包内全部 enabled 条目 |
| preset | string | 否 | null | manifest.presets 命名预设（如 simple-java-sso 的 remote/embedded）；与 files 互斥；preset 默认 variables 与调用方 variables 逐键合并（调用方优先） |
| basePackageOverride | string | 否 | null | 基础包名覆盖值 |
| duplicateStrategy | string | 否 | skip | overwrite / skip；skip 对含手写代码目录不覆盖 |
| variables | object | 否 | null | 注入 vars.*；命名契约键 tablePrefixes/stripTablePrefix/singularize/entitySuffix/packageSegmentCase/layerPrefix/permissionModule 透传，优先级高于 manifest.naming |
| modules | array<object> | 否 | null | module 作用域模板逐个渲染；元素 name 必填且不得重复；省略但选中 module 模板时报错 |
| webDirectory | string | 否 | web | root=web 模板基准；相对且不含 .. |
| includePlatformTables | boolean | 否 | false | true 时平台表参与生成但回执仍标注命中；排除表规则不受影响 |
形态参数经 variables 传递：deployMode（standalone 缺省 / cloud）、platformUi（remote 缺省 / embedded；simple-java-sso 的 preset=embedded 会自动注入）。

【4. 返回结构总览】
- preview_generation：entries[]（table 来源上下文/relativePath 渲染后相对路径/absolutePath 目标绝对路径/action=new|overwrite|skip）、newCount/overwriteCount/skipCount、unmappedTypes[]（dbType/table/column/occurrences）、pathWarnings[]（kind/template/samplePath/occurrences/message）、namingReport[]（rawName/entityName/variableName/packagePath/tableNameInCode/permissionKey）、coverageWarnings[]（kind=NoBackendView|NoWebPage|OnlyBackendGenerated/message）、platformTableExcluded[]（tableName/rule/excluded，excluded=false 表示经 includePlatformTables 放行）。
- generate：generated/overwritten/skipped/failed（计数）、isCancelled、logs[]（timestamp/level/message）、namingReport/pathWarnings/coverageWarnings/platformTableExcluded（同 preview）。
- list_template_packages：packages[]（name/description/engine/basePackage/isBuiltin/fileCount/modifiedTime/role/stack/scopes/hasBackendView/hasWebPage/contractPath/deployModes/hasContract/warnings/presets[]）。
- list_templates / read_template：templates[]（path/output/enabled/language/scope/root/role/isContract）或正文 content+contentHash；两者均带 presets[]（name/description/fileCount）。
- 失败应答：可预期领域错误以正常结果内的 JSON {isError:true,errorType,message} 返回（errorType=InvalidArgument/TemplatePackageError/Timeout/Cancelled/InternalError/NameConflict/OriginalMissing/NotFound/SaveFailed）；声明 outputSchema 的 9 个结构化输出工具错误走协议 isError 通道。

【5. 硬约束】
- 平台表（manifest.platformTables 声明的 sys_* 等）禁止生成 CRUD：默认排除并列入 platformTableExcluded；确需生成须显式 includePlatformTables=true（回执仍标注命中）；排除表规则一律生效。
- web 平台能力直连授权中心（Bearer）；业务后端零平台端点/零平台类；view 层 = 后端 Java View（只做数据处理，不含前端页面）。
- 手写代码目录默认 duplicateStrategy=skip，禁止覆盖手写文件。
- 两形态差异：第一形态不生成平台管理页与平台 CRUD；第二形态平台管理页必须复用授权中心 web 现成代码并直连授权中心，不进入代码生成面。
- 命名契约：table.rawName = 数据库真实物理表名（永不变形，SQL/DDL/@TableName 必须用它）；table.entityName = 前缀剥离→单复数归一→PascalCase→拼 entitySuffix；table.variableName = camelCase(entityName)；permissionKey = {package.permissionModule}:{op}。
- 输出根：files[].root = output（工作区根+相对输出根）/ workspace（工作区根）/ web（工作区根+webDirectory，缺省 web）；workspace/web 禁止 .. 段。
- 参数全显式零记忆态：不读不写桌面端勾选态/最近目录；generate 写盘无确认弹窗，必须先 preview_generation。
""";
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
