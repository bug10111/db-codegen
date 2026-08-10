using DbCodeGen.Core.Model;

namespace DbCodeGen.Core.Generation;

/// <summary>
/// 批量代码生成服务统一接口，承载 dry-run 预览与按策略写盘两个阶段。
/// 安全线：写盘前必走 dry-run 重算；同名文件处理策略由请求指定（覆盖/跳过），无确认弹窗。
/// </summary>
public interface ICodeGenerator
{
    /// <summary>
    /// 构建 dry-run 预览：表与模板文件笛卡尔积渲染，解析绝对路径并防目录穿越，
    /// 按目标文件状态分类为新增/覆盖/跳过，全程支持取消并报告渲染与分类进度。
    /// </summary>
    /// <param name="request">生成请求，含勾选表集合、勾选模板文件集合与输出根路径。</param>
    /// <param name="progress">进度推送，报告 Rendering 与 Previewing 阶段。</param>
    /// <param name="cancellationToken">取消标记，取消时抛 OperationCanceledException。</param>
    /// <returns>dry-run 清单，含全部条目与新增/覆盖/跳过计数。</returns>
    /// <exception cref="ArgumentNullException">request 为 null 时抛出。</exception>
    /// <exception cref="OperationCanceledException">渲染或分类过程被取消时抛出。</exception>
    /// <exception cref="GenerationException">模板渲染失败、输出路径模板渲染失败或路径越界时抛出。</exception>
    Task<GenerationPreview> BuildPreviewAsync(
        GenerationRequest request,
        IProgress<GenerationProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// 按请求策略写盘：内部先重算 dry-run（安全线），按请求中同名文件策略（覆盖/跳过）分类后
    /// 逐文件写盘并统计，无确认弹窗；取消返回取消结果，生成完成后回写最近相对输出根。
    /// </summary>
    /// <param name="request">生成请求，含勾选表集合、勾选模板文件集合、输出根路径与同名文件处理策略。</param>
    /// <param name="progress">进度推送，报告 Rendering、Previewing 与 Writing 阶段。</param>
    /// <param name="cancellationToken">取消标记，取消时返回取消结果并携带已完成统计。</param>
    /// <returns>写盘结果统计与生成日志。</returns>
    /// <exception cref="ArgumentNullException">request 为 null 时抛出。</exception>
    /// <exception cref="GenerationException">模板渲染失败、输出路径模板渲染失败或路径越界时抛出。</exception>
    Task<GenerationResult> GenerateAsync(
        GenerationRequest request,
        IProgress<GenerationProgress>? progress,
        CancellationToken cancellationToken);
}
