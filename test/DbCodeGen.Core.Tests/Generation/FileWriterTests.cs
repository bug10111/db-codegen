using System.Text;
using DbCodeGen.Core.Generation;
using DbCodeGen.Core.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbCodeGen.Core.Tests.Generation;

/// <summary>
/// 文件写盘服务单元测试，覆盖新增/覆盖/跳过分类写盘、自动建目录、部分失败继续、取消与进度报告。
/// </summary>
public sealed class FileWriterTests : IDisposable
{
    private readonly List<string> _tempRoots = new();
    private readonly FileWriter _writer = new(NullLogger<FileWriter>.Instance);

    /// <summary>
    /// 清理全部测试临时目录。
    /// </summary>
    public void Dispose()
    {
        foreach (string root in _tempRoots)
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 新增条目应创建文件且内容为 UTF-8 无 BOM，计数为生成。
    /// </summary>
    [Fact]
    public async Task WriteFilesAsync_NewEntry_CreatesFileWithoutBom()
    {
        string tempRoot = CreateTempRoot();
        string targetPath = Path.Combine(tempRoot, "entity", "SysUser.java");
        var entry = new GenerationFileEntry("sys_user", "entity/SysUser.java", targetPath, GenerationAction.New, "public class SysUser {}");

        GenerationResult result = await _writer.WriteFilesAsync(new[] { entry }, null, CancellationToken.None);

        Assert.Equal(1, result.Generated);
        Assert.Equal(0, result.Failed);
        Assert.True(File.Exists(targetPath));
        byte[] bytes = await File.ReadAllBytesAsync(targetPath);
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Equal("public class SysUser {}", await File.ReadAllTextAsync(targetPath));
    }

    /// <summary>
    /// 覆盖条目应替换既有文件内容，计数为覆盖。
    /// </summary>
    [Fact]
    public async Task WriteFilesAsync_OverwriteEntry_ReplacesContent()
    {
        string tempRoot = CreateTempRoot();
        string targetPath = Path.Combine(tempRoot, "SysUser.java");
        await File.WriteAllTextAsync(targetPath, "OLD CONTENT");
        var entry = new GenerationFileEntry("sys_user", "SysUser.java", targetPath, GenerationAction.Overwrite, "NEW CONTENT");

        GenerationResult result = await _writer.WriteFilesAsync(new[] { entry }, null, CancellationToken.None);

        Assert.Equal(1, result.Overwritten);
        Assert.Equal("NEW CONTENT", await File.ReadAllTextAsync(targetPath));
    }

    /// <summary>
    /// 跳过条目应不写盘仅计数，目标文件保持原样。
    /// </summary>
    [Fact]
    public async Task WriteFilesAsync_SkipEntry_DoesNotTouchTarget()
    {
        string tempRoot = CreateTempRoot();
        string targetPath = Path.Combine(tempRoot, "SysUser.java");
        await File.WriteAllTextAsync(targetPath, "ORIGINAL");
        var entry = new GenerationFileEntry("sys_user", "SysUser.java", targetPath, GenerationAction.Skip, "ORIGINAL");

        GenerationResult result = await _writer.WriteFilesAsync(new[] { entry }, null, CancellationToken.None);

        Assert.Equal(1, result.Skipped);
        Assert.Equal("ORIGINAL", await File.ReadAllTextAsync(targetPath));
    }

    /// <summary>
    /// 写盘到嵌套目录应自动创建父目录。
    /// </summary>
    [Fact]
    public async Task WriteFilesAsync_NestedDirectory_CreatesParentDirectories()
    {
        string tempRoot = CreateTempRoot();
        string targetPath = Path.Combine(tempRoot, "a", "b", "c", "entity.java");
        var entry = new GenerationFileEntry("sys_user", "a/b/c/entity.java", targetPath, GenerationAction.New, "content");

        GenerationResult result = await _writer.WriteFilesAsync(new[] { entry }, null, CancellationToken.None);

        Assert.Equal(1, result.Generated);
        Assert.True(File.Exists(targetPath));
    }

    /// <summary>
    /// 单文件写盘失败应记录条目级错误并继续其余文件，形成部分失败终态。
    /// </summary>
    [Fact]
    public async Task WriteFilesAsync_PartialFailure_ContinuesOtherFiles()
    {
        string tempRoot = CreateTempRoot();
        string blockedPath = Path.Combine(tempRoot, "blocked");
        Directory.CreateDirectory(blockedPath);
        var failingEntry = new GenerationFileEntry("t1", "blocked", blockedPath, GenerationAction.New, "content");
        string goodPath = Path.Combine(tempRoot, "good.java");
        var goodEntry = new GenerationFileEntry("t1", "good.java", goodPath, GenerationAction.New, "GOOD");

        GenerationResult result = await _writer.WriteFilesAsync(new[] { failingEntry, goodEntry }, null, CancellationToken.None);

        Assert.Equal(1, result.Generated);
        Assert.Equal(1, result.Failed);
        Assert.NotNull(failingEntry.Error);
        Assert.True(File.Exists(goodPath));
        Assert.Single(result.Logs, log => log.Level == GenerationLogLevel.Error);
    }

    /// <summary>
    /// 取消标记应返回取消结果且携带已完成统计，不把取消当作失败。
    /// </summary>
    [Fact]
    public async Task WriteFilesAsync_CancelledToken_ReturnsCancelledResult()
    {
        string tempRoot = CreateTempRoot();
        string targetPath = Path.Combine(tempRoot, "SysUser.java");
        var entry = new GenerationFileEntry("sys_user", "SysUser.java", targetPath, GenerationAction.New, "content");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        GenerationResult result = await _writer.WriteFilesAsync(new[] { entry }, null, cts.Token);

        Assert.True(result.IsCancelled);
        Assert.Equal(0, result.Generated);
        Assert.False(File.Exists(targetPath));
    }

    /// <summary>
    /// 写盘全程应报告 Writing 阶段进度，总数等于条目数。
    /// </summary>
    [Fact]
    public async Task WriteFilesAsync_ReportsWritingProgress()
    {
        string tempRoot = CreateTempRoot();
        var progressValues = new List<GenerationProgress>();
        IProgress<GenerationProgress> progress = new SyncProgress<GenerationProgress>(progressValues.Add);
        string firstPath = Path.Combine(tempRoot, "a.java");
        string secondPath = Path.Combine(tempRoot, "b.java");
        var entries = new[]
        {
            new GenerationFileEntry("t", "a.java", firstPath, GenerationAction.New, "A"),
            new GenerationFileEntry("t", "b.java", secondPath, GenerationAction.Skip, "B")
        };

        await _writer.WriteFilesAsync(entries, progress, CancellationToken.None);

        Assert.Equal(2, progressValues.Count);
        Assert.All(progressValues, value => Assert.Equal(GenerationStage.Writing, value.Stage));
        Assert.Equal(2, progressValues[^1].Completed);
        Assert.Equal(2, progressValues[^1].Total);
    }

    /// <summary>
    /// 写盘应生成与动作对应的日志条目。
    /// </summary>
    [Fact]
    public async Task WriteFilesAsync_GeneratesActionLogs()
    {
        string tempRoot = CreateTempRoot();
        string targetPath = Path.Combine(tempRoot, "SysUser.java");
        var entries = new[]
        {
            new GenerationFileEntry("t", "SysUser.java", targetPath, GenerationAction.New, "NEW"),
            new GenerationFileEntry("t", "skip.java", Path.Combine(tempRoot, "skip.java"), GenerationAction.Skip, "SKIP")
        };

        GenerationResult result = await _writer.WriteFilesAsync(entries, null, CancellationToken.None);

        Assert.Contains(result.Logs, log => log.Message.Contains("已生成"));
        Assert.Contains(result.Logs, log => log.Message.Contains("已跳过"));
    }

    /// <summary>
    /// 在临时目录下创建并登记一个待清理的测试根目录。
    /// </summary>
    /// <returns>测试临时根目录路径。</returns>
    private string CreateTempRoot()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", Guid.NewGuid().ToString("N"));
        _tempRoots.Add(tempRoot);
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    /// <summary>
    /// 同步进度回调封装，避免 Progress&lt;T&gt; 异步派发导致的测试竞态。
    /// </summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SyncProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value)
        {
            _handler(value);
        }
    }
}
