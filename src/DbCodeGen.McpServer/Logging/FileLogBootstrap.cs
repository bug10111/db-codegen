using Serilog;
using Serilog.Events;

namespace DbCodeGen.McpServer.Logging;

/// <summary>
/// 文件日志引导：stdio 形态下 stdout 承载 MCP 协议帧，进程日志必须与协议通道隔离、统一落文件。
/// 目录固定锚定 C:\simple\DbCodeGen.McpServer\logs\，跨构建/发布/拷贝位置一致；
/// 保留策略为滚动天文件最多 7 份；初始化失败不阻塞服务启动，回落静默日志并落 exe 旁 boot-error.log 兜底。
/// </summary>
internal static class FileLogBootstrap
{
    /// <summary>
    /// 日志固定根目录：C:\simple\DbCodeGen.McpServer\logs\。
    /// </summary>
    private const string LogRoot = @"C:\simple\DbCodeGen.McpServer\logs";

    /// <summary>
    /// 创建文件日志器：按天滚动、保留 7 天、UTF-8 落盘；目录创建或日志器构建失败时写 boot-error.log 并回落空日志器。
    /// </summary>
    /// <returns>可注册到 Microsoft.Extensions.Logging 的 Serilog 日志器。</returns>
    public static ILogger CreateLogger()
    {
        try
        {
            // 目录预创建失败（权限/磁盘满）时提前进入兜底路径，避免文件 sink 在后台静默丢日志
            Directory.CreateDirectory(LogRoot);

            return new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    Path.Combine(LogRoot, "dbcodegen-mcp-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    fileSizeLimitBytes: 20 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    // 多个 MCP 服务端实例可能同时运行（AI 客户端并发拉起），共享写保证后启动
                    // 进程也能写入主日志文件，审计迹不分裂到 _001 滚动文件
                    shared: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1),
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }
        catch (Exception exception)
        {
            // 兜底：启动期日志初始化失败不得阻断 MCP 服务，降级为静默日志并在 exe 旁留痕
            WriteBootError(exception);
            return new LoggerConfiguration().CreateLogger();
        }
    }

    /// <summary>
    /// 启动期日志初始化失败兜底：向 exe 同目录 boot-error.log 追加一条带时间戳的失败记录；
    /// 文件兜底通道自身不可写时回落 stderr 输出可见诊断（stderr 不在 MCP stdio 协议通道上，可安全使用）。
    /// </summary>
    /// <param name="exception">初始化失败异常。</param>
    private static void WriteBootError(Exception exception)
    {
        string message = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss zzz} 日志初始化失败，已降级为静默日志：{exception.Message}{Environment.NewLine}";
        try
        {
            string bootErrorPath = Path.Combine(AppContext.BaseDirectory, "boot-error.log");
            File.AppendAllText(bootErrorPath, message);
        }
        catch (Exception fallbackException)
        {
            // boot-error.log 均不可写（如 exe 目录只读）时经 stderr 保底暴露，禁止无痕吞掉初始化失败
            Console.Error.WriteLine(message + $"boot-error.log 写入失败：{fallbackException.Message}");
        }
    }
}
