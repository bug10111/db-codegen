using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DbCodeGen.App.Services;

/// <summary>
/// 跨窗口对话框服务实现，统一承载消息提示、二次确认、目录选择、文件选择与文本输入五类对话框能力，
/// 供全项目各窗口复用。所有对话框均保证在 UI 线程展示，非 UI 线程调用时经 Dispatcher 切换到 UI 线程执行。
/// </summary>
public sealed class DialogService : IDialogService, IConfirmDialogService, IFolderPickerService, IFilePickerService, IPromptDialogService
{
    /// <inheritdoc />
    public void ShowInfo(string message, string title = "提示")
    {
        RunOnUiThread(() =>
        {
            MessageBox.Show(GetOwner(), message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    /// <inheritdoc />
    public void ShowError(string message, string title = "错误")
    {
        RunOnUiThread(() =>
        {
            MessageBox.Show(GetOwner(), message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        });
    }

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(string title, string message)
    {
        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            MessageBoxResult result = MessageBox.Show(GetOwner(), message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            completion.SetResult(result == MessageBoxResult.Yes);
        });
        return completion.Task;
    }

    /// <inheritdoc />
    public Task<string?> PickFolderAsync(string? initialDirectory = null, string title = "选择目录")
    {
        TaskCompletionSource<string?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            OpenFolderDialog dialog = new()
            {
                Title = title,
                Multiselect = false
            };

            // 仅当初始目录真实存在时才预填，避免对话框打开时指向无效路径
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }

            bool? accepted = dialog.ShowDialog(GetOwner());
            completion.SetResult(accepted == true ? dialog.FolderName : null);
        });
        return completion.Task;
    }

    /// <inheritdoc />
    public Task<string?> PickOpenZipAsync(string? initialDirectory = null, string title = "选择模板包 zip 文件")
    {
        TaskCompletionSource<string?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            OpenFileDialog dialog = new()
            {
                Title = title,
                Filter = "zip 文件|*.zip|所有文件|*.*",
                CheckFileExists = true
            };

            // 仅当初始目录真实存在时才预填，避免对话框打开时指向无效路径
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }

            bool? accepted = dialog.ShowDialog(GetOwner());
            completion.SetResult(accepted == true ? dialog.FileName : null);
        });
        return completion.Task;
    }

    /// <inheritdoc />
    public Task<string?> PickSaveZipAsync(string defaultFileName, string? initialDirectory = null, string title = "导出模板包 zip 文件")
    {
        TaskCompletionSource<string?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            SaveFileDialog dialog = new()
            {
                Title = title,
                FileName = defaultFileName,
                Filter = "zip 文件|*.zip",
                DefaultExt = ".zip",
                AddExtension = true,
                OverwritePrompt = true
            };

            // 仅当初始目录真实存在时才预填，避免对话框打开时指向无效路径
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }

            bool? accepted = dialog.ShowDialog(GetOwner());
            completion.SetResult(accepted == true ? dialog.FileName : null);
        });
        return completion.Task;
    }

    /// <inheritdoc />
    public Task<string?> PickOpenSqlAsync(string? initialDirectory = null, string title = "打开 SQL 文件")
    {
        TaskCompletionSource<string?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            OpenFileDialog dialog = new()
            {
                Title = title,
                Filter = "SQL 文件|*.sql|所有文件|*.*",
                CheckFileExists = true
            };

            // 仅当初始目录真实存在时才预填，避免对话框打开时指向无效路径
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }

            bool? accepted = dialog.ShowDialog(GetOwner());
            completion.SetResult(accepted == true ? dialog.FileName : null);
        });
        return completion.Task;
    }

    /// <inheritdoc />
    public Task<string?> PickSaveSqlAsync(string defaultFileName, string? initialDirectory = null, string title = "保存 SQL 文件")
    {
        TaskCompletionSource<string?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            SaveFileDialog dialog = new()
            {
                Title = title,
                FileName = defaultFileName,
                Filter = "SQL 文件|*.sql",
                DefaultExt = ".sql",
                AddExtension = true,
                OverwritePrompt = true
            };

            // 仅当初始目录真实存在时才预填，避免对话框打开时指向无效路径
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }

            bool? accepted = dialog.ShowDialog(GetOwner());
            completion.SetResult(accepted == true ? dialog.FileName : null);
        });
        return completion.Task;
    }

    /// <inheritdoc />
    public Task<string?> PickOpenBackupAsync(string? initialDirectory = null, string title = "选择备份文件")
    {
        TaskCompletionSource<string?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            OpenFileDialog dialog = new()
            {
                Title = title,
                Filter = "dbcg 备份文件|*.dbcg|所有文件|*.*",
                CheckFileExists = true
            };

            // 仅当初始目录真实存在时才预填，避免对话框打开时指向无效路径
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }

            bool? accepted = dialog.ShowDialog(GetOwner());
            completion.SetResult(accepted == true ? dialog.FileName : null);
        });
        return completion.Task;
    }

    /// <inheritdoc />
    public Task<string?> PickSaveBackupAsync(string defaultFileName, string? initialDirectory = null, string title = "保存备份文件")
    {
        TaskCompletionSource<string?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            SaveFileDialog dialog = new()
            {
                Title = title,
                FileName = defaultFileName,
                Filter = "dbcg 备份文件|*.dbcg",
                DefaultExt = ".dbcg",
                AddExtension = true,
                OverwritePrompt = true
            };

            // 仅当初始目录真实存在时才预填，避免对话框打开时指向无效路径
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }

            bool? accepted = dialog.ShowDialog(GetOwner());
            completion.SetResult(accepted == true ? dialog.FileName : null);
        });
        return completion.Task;
    }

    /// <inheritdoc />
    public Task<string?> PickOpenJsonAsync(string? initialDirectory = null, string title = "导入类型映射")
    {
        TaskCompletionSource<string?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            OpenFileDialog dialog = new()
            {
                Title = title,
                Filter = "JSON 文件|*.json|所有文件|*.*",
                CheckFileExists = true
            };

            // 仅当初始目录真实存在时才预填，避免对话框打开时指向无效路径
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }

            bool? accepted = dialog.ShowDialog(GetOwner());
            completion.SetResult(accepted == true ? dialog.FileName : null);
        });
        return completion.Task;
    }

    /// <inheritdoc />
    public Task<string?> PickSaveJsonAsync(string defaultFileName, string? initialDirectory = null, string title = "导出类型映射")
    {
        TaskCompletionSource<string?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            SaveFileDialog dialog = new()
            {
                Title = title,
                FileName = defaultFileName,
                Filter = "JSON 文件|*.json",
                DefaultExt = ".json",
                AddExtension = true,
                OverwritePrompt = true
            };

            // 仅当初始目录真实存在时才预填，避免对话框打开时指向无效路径
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }

            bool? accepted = dialog.ShowDialog(GetOwner());
            completion.SetResult(accepted == true ? dialog.FileName : null);
        });
        return completion.Task;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> PickOpenReferenceFilesAsync(string? initialDirectory = null, string title = "选择参考文件")
    {
        TaskCompletionSource<IReadOnlyList<string>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            // 多选打开对话框：Multiselect 开启取 FileNames 路径清单，Filter 开放所有文件以适配
            // “其他平台生成器模板或现有项目代码”作为参考文件的场景
            OpenFileDialog dialog = new()
            {
                Title = title,
                Filter = "所有文件|*.*",
                Multiselect = true,
                CheckFileExists = true
            };

            // 仅当初始目录真实存在时才预填，避免对话框打开时指向无效路径
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }

            bool? accepted = dialog.ShowDialog(GetOwner());
            completion.SetResult(accepted == true ? dialog.FileNames : Array.Empty<string>());
        });
        return completion.Task;
    }

    /// <inheritdoc />
    public Task<string?> PromptAsync(string title, string prompt, string defaultValue = "")
    {
        TaskCompletionSource<string?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            Window? owner = GetOwner();

            // 构建轻量输入对话框：引导文案 + 输入框 + 确定/取消，回车默认确定、Esc 默认取消
            var dialog = new Window
            {
                Title = title,
                Width = 440,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = owner
            };

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                Text = prompt,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            TextBox inputBox = new()
            {
                Text = defaultValue ?? string.Empty,
                MinWidth = 380,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(inputBox);

            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var okButton = new Button
            {
                Content = "确定",
                IsDefault = true,
                Width = 76,
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 8, 0)
            };
            var cancelButton = new Button
            {
                Content = "取消",
                IsCancel = true,
                Width = 76,
                Padding = new Thickness(8, 3, 8, 3)
            };
            buttonRow.Children.Add(okButton);
            buttonRow.Children.Add(cancelButton);
            panel.Children.Add(buttonRow);
            dialog.Content = panel;

            string? input = null;
            okButton.Click += (_, _) =>
            {
                input = inputBox.Text;
                dialog.DialogResult = true;
            };
            cancelButton.Click += (_, _) => dialog.DialogResult = false;

            // 打开后聚焦输入框并全选预填值，便于直接键入覆盖
            dialog.Loaded += (_, _) =>
            {
                inputBox.Focus();
                inputBox.SelectAll();
            };
            dialog.Closed += (_, _) => completion.TrySetResult(input);

            dialog.ShowDialog();
        });
        return completion.Task;
    }

    /// <summary>
    /// 将指定动作调度到 UI 线程执行，已在 UI 线程时直接执行，保证对话框展示不跨线程。
    /// </summary>
    /// <param name="action">需要在 UI 线程执行的对话框动作。</param>
    private static void RunOnUiThread(Action action)
    {
        // 无 Application 上下文（如单元测试环境）时视为 UI 线程直接执行
        Application? app = Application.Current;
        if (app is null)
        {
            action();
            return;
        }

        Dispatcher dispatcher = app.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    /// <summary>
    /// 取当前应用主窗口作为对话框所有者，主窗口尚未建立时返回 null（无所有者弹窗）。
    /// </summary>
    private static Window? GetOwner()
    {
        return Application.Current?.MainWindow;
    }
}
