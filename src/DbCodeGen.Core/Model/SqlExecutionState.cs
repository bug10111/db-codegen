namespace DbCodeGen.Core.Model;

/// <summary>
/// SQL 执行会话生命周期状态，驱动面板执行按钮禁用与状态展示。
/// </summary>
public enum SqlExecutionState
{
    /// <summary>
    /// 就绪，可执行。
    /// </summary>
    Idle,

    /// <summary>
    /// 执行中，UI 禁用执行按钮并展示加载指示。
    /// </summary>
    Executing,

    /// <summary>
    /// 执行成功，查询结果或影响行数已返回。
    /// </summary>
    Success,

    /// <summary>
    /// 执行失败，连接、语法或 SQL 异常。
    /// </summary>
    Error,

    /// <summary>
    /// 用户取消，CancellationToken 触发。
    /// </summary>
    Cancelled
}
