using System.Windows;
using DbCodeGen.Core.Model;

namespace DbCodeGen.App.Views;

/// <summary>
/// 用户对未映射类型弹窗的选择结果。
/// </summary>
public enum UnmappedChoice
{
    /// <summary>
    /// 去配置映射：打开类型映射窗口补全后重新生成。
    /// </summary>
    Configure,

    /// <summary>
    /// 使用默认继续：以默认 String 兜底继续本次预览/生成。
    /// </summary>
    ContinueWithDefault,

    /// <summary>
    /// 取消：放弃本次预览/生成。
    /// </summary>
    Cancel
}

/// <summary>
/// 未映射类型提示窗口，展示生成预检发现的缺少映射的数据库类型清单，
/// 供用户选择去配置映射、使用默认继续或取消。
/// </summary>
public partial class UnmappedTypesWindow : Window
{
    /// <summary>
    /// 用户选择结果，默认取消；窗口关闭后由调用方读取。
    /// </summary>
    public UnmappedChoice Result { get; private set; } = UnmappedChoice.Cancel;

    /// <summary>
    /// 构造未映射类型提示窗口。
    /// </summary>
    public UnmappedTypesWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 填充未映射类型清单并更新提示计数，供打开前由调用方调用。
    /// </summary>
    /// <param name="types">生成预检发现的未映射类型清单。</param>
    /// <exception cref="ArgumentNullException">types 为 null 时抛出。</exception>
    public void SetTypes(IReadOnlyList<UnmappedTypeInfo> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        TypesDataGrid.ItemsSource = types;
        CountText.Text = $"以下 {types.Count} 个数据库类型缺少映射：";
    }

    /// <summary>
    /// 点击“去配置映射”：记录选择并关闭窗口。
    /// </summary>
    private void OnConfigureClick(object sender, RoutedEventArgs e)
    {
        Result = UnmappedChoice.Configure;
        Close();
    }

    /// <summary>
    /// 点击“使用默认继续”：记录选择并关闭窗口。
    /// </summary>
    private void OnContinueClick(object sender, RoutedEventArgs e)
    {
        Result = UnmappedChoice.ContinueWithDefault;
        Close();
    }

    /// <summary>
    /// 点击“取消”：保持默认取消结果并关闭窗口。
    /// </summary>
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Result = UnmappedChoice.Cancel;
        Close();
    }
}
