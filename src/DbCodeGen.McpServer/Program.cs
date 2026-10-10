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
    options.ServerInstructions = "DbCodeGen 代码生成器 MCP 服务端（只读枚举工具面）："
        + "先 list_template_packages / list_templates 选择模板，read_template 查看正文；"
        + "list_datasources / list_tables / get_table_detail 查询连接与表结构；"
        + "生成前用 check_type_mapping 预检未映射类型。内置模板包只读；"
        + "模板包管理走 create_template_package / delete_template_package / rename_template_package / "
        + "update_template_package_metadata / copy_template_package（删除需 confirm=true，内置包复制转可写用户包用 copy_template_package）。";
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
