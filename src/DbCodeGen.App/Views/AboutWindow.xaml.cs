using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace DbCodeGen.App.Views;

/// <summary>
/// 关于窗口，展示应用名称、程序集版本、项目功能简介、开源地址与作者；开源链接点击后经系统默认浏览器打开。
/// </summary>
public partial class AboutWindow : Window
{
    /// <summary>
    /// 初始化关于窗口并回填程序集版本号，避免硬编码版本与实际发布版本漂移。
    /// 开源地址与作者以 XAML 字面量展示，与 README 保持一致。
    /// </summary>
    public AboutWindow()
    {
        InitializeComponent();

        // 版本号由程序集版本派生，展示三位主版本号
        VersionText.Text = "v" + (typeof(AboutWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0");
    }

    /// <summary>
    /// 点击开源链接时经系统默认浏览器打开目标地址，不改变关于窗口内容；打开失败静默忽略不阻断窗口使用。
    /// </summary>
    /// <param name="sender">事件源，为开源地址超链接。</param>
    /// <param name="e">导航请求事件参数。</param>
    private void OnRepositoryLinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 系统默认浏览器打开失败时不打扰用户，仅结束本次导航
        }

        e.Handled = true;
    }
}
