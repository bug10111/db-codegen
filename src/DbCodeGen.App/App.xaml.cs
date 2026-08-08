using System.Windows;
using DbCodeGen.App.Services;
using DbCodeGen.App.ViewModels;
using DbCodeGen.App.Views;
using DbCodeGen.Core.Ai;
using DbCodeGen.Core.BackupRestore;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.DataSource;
using DbCodeGen.Core.Generation;
using DbCodeGen.Core.Security;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbCodeGen.App;

/// <summary>
/// WPF 应用入口，承载依赖注入容器构建与主窗口创建，是主窗口各功能模块的组合根。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 应用级依赖注入容器，生命周期与应用一致，退出时统一释放单例服务资源。
    /// </summary>
    private ServiceProvider? _serviceProvider;

    /// <summary>
    /// 应用启动时构建依赖注入容器并创建主窗口，替代 XAML StartupUri 的默认创建路径。
    /// </summary>
    /// <param name="e">启动事件参数。</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _serviceProvider = BuildServiceProvider();
        MainWindow mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    /// <summary>
    /// 应用退出时释放依赖注入容器，回收单例服务持有的托管资源。
    /// </summary>
    /// <param name="e">退出事件参数。</param>
    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 构建应用依赖注入容器：注册日志基础、核心服务、跨窗口对话框服务、当前连接服务与窗口视图模型。
    /// </summary>
    /// <returns>配置完成的服务提供器。</returns>
    private static ServiceProvider BuildServiceProvider()
    {
        ServiceCollection services = new();

        // 日志基础：当前阶段未接日志输出端，注册空日志工厂保证 ILogger 泛型可解析
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        // 核心服务：DPAPI 凭据保护、配置持久化与数据源连接
        services.AddSingleton<CredentialProtector>();
        services.AddSingleton<IConfigService, ConfigService>();
        services.AddSingleton<IDataSourceService, DataSourceService>();
        services.AddSingleton<SqlExecutor>();

        // 全局类型映射服务：承载"全局映射表>包typeMap>兜底"解析链与生成前未映射预检
        services.AddSingleton<ITypeMappingService, TypeMappingService>();

        // 元数据读取服务：方言工厂 + 表清单/表详情编排，供主窗口①表列表区消费
        services.AddSingleton<ISchemaReaderFactory, SchemaReaderFactory>();
        services.AddSingleton<TableCatalogService>();

        // 跨窗口对话框服务：多接口映射到同一实现实例，保证各窗口对话框行为一致
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(provider => provider.GetRequiredService<DialogService>());
        services.AddSingleton<IConfirmDialogService>(provider => provider.GetRequiredService<DialogService>());
        services.AddSingleton<IFolderPickerService>(provider => provider.GetRequiredService<DialogService>());
        services.AddSingleton<IFilePickerService>(provider => provider.GetRequiredService<DialogService>());
        services.AddSingleton<IPromptDialogService>(provider => provider.GetRequiredService<DialogService>());

        // 当前连接共享状态服务，供主窗口工具栏与各消费方联动
        services.AddSingleton<ICurrentDataSourceService, CurrentDataSourceService>();

        // AI 模板生成服务：LLM 客户端与模板生成器，供 AI 向导窗口消费
        services.AddSingleton<ILlmClient, LlmClient>();
        services.AddSingleton<ITemplateAiGenerator, TemplateAiGenerator>();

        // 主窗口①表列表区视图模型，单例与主窗口生命周期一致
        services.AddSingleton<TableListViewModel>();

        // 模板包管理服务与共享渲染管线：模板包服务承载列表与复制引导，文件读写与渲染供②③区消费
        services.AddSingleton<ITemplatePackageService, TemplatePackageService>();
        services.AddSingleton<TemplateFileWriter>();
        services.AddSingleton<TemplateEngine>();

        // 模板编辑器高亮服务，按目标语言构建缓存高亮定义
        services.AddSingleton<HighlightingService>();

        // ②模板区编辑器与③预览区视图模型，单例与主窗口生命周期一致
        services.AddSingleton<TemplateViewModel>();
        services.AddSingleton<PreviewViewModel>();

        // 批量生成服务与文件写盘服务，供主窗口④生成栏消费
        services.AddSingleton<IFileWriter, FileWriter>();
        services.AddSingleton<ICodeGenerator, CodeGenerator>();

        // ④生成栏视图模型，单例与主窗口生命周期一致
        services.AddSingleton<GenerationViewModel>();

        // 变量面板视图模型与窗口，供②区“变量面板”入口按需创建
        services.AddSingleton<VariablePanelViewModel>();
        services.AddTransient<VariablePanelWindow>();
        services.AddTransient<Func<VariablePanelWindow>>(provider =>
            () => provider.GetRequiredService<VariablePanelWindow>());

        // 数据源管理窗口与视图模型，供主窗口“管理…”入口按需创建
        services.AddTransient<DataSourceViewModel>();
        services.AddTransient<DataSourceManagerWindow>();
        services.AddTransient<Func<DataSourceManagerWindow>>(provider =>
            () => provider.GetRequiredService<DataSourceManagerWindow>());

        // 设置窗口与视图模型，供向导未配置 LLM 时跳转设置页按需创建
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();
        services.AddTransient<Func<SettingsWindow>>(provider =>
            () => provider.GetRequiredService<SettingsWindow>());

        // 类型映射窗口与视图模型，供工具菜单/设置窗口/未映射弹窗入口按需创建
        services.AddTransient<TypeMappingViewModel>();
        services.AddTransient<TypeMappingWindow>();
        services.AddTransient<Func<TypeMappingWindow>>(provider =>
            () => provider.GetRequiredService<TypeMappingWindow>());

        // 未映射类型提示窗口，供生成预检发现未映射类型时按需创建
        services.AddTransient<UnmappedTypesWindow>();
        services.AddTransient<Func<UnmappedTypesWindow>>(provider =>
            () => provider.GetRequiredService<UnmappedTypesWindow>());

        // 模板包管理窗口与视图模型，供向导生成成功跳转模板包管理按需创建
        services.AddTransient<TemplatePackageManagerViewModel>();
        services.AddTransient<TemplatePackageManagerWindow>();
        services.AddTransient<Func<TemplatePackageManagerWindow>>(provider =>
            () => provider.GetRequiredService<TemplatePackageManagerWindow>());

        // AI 生成模板向导窗口与视图模型，供主窗口菜单“AI 生成模板”入口按需创建
        services.AddTransient<AiTemplateWizardViewModel>();
        services.AddTransient<AiTemplateWizardWindow>();
        services.AddTransient<Func<AiTemplateWizardWindow>>(provider =>
            () => provider.GetRequiredService<AiTemplateWizardWindow>());

        // SQL 执行面板窗口与视图模型，供主窗口菜单“SQL 执行面板”入口按需创建
        services.AddTransient<SqlExecutorViewModel>();
        services.AddTransient<SqlExecutorWindow>();
        services.AddTransient<Func<SqlExecutorWindow>>(provider =>
            () => provider.GetRequiredService<SqlExecutorWindow>());

        // 备份/恢复服务与迁移窗口，供工具菜单“备份/恢复”入口按需创建
        services.AddSingleton<IBackupRestoreService, BackupRestoreService>();
        services.AddTransient<MigrationViewModel>();
        services.AddTransient<MigrationWindow>();
        services.AddTransient<Func<MigrationWindow>>(provider =>
            () => provider.GetRequiredService<MigrationWindow>());

        // 主窗口与应用同生命周期，作为组合根承载四区布局与工具栏
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
