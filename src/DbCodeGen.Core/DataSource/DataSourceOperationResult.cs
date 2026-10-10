using DbCodeGen.Core.Model;

namespace DbCodeGen.Core.DataSource;

/// <summary>
/// 数据源连接管理操作结果，携带状态、面向调用方的可读消息与成功时的连接配置。
/// </summary>
public sealed class DataSourceOperationResult
{
    private DataSourceOperationResult(DataSourceOperationStatus status, string message, DataSourceConfig? config)
    {
        Status = status;
        Message = message;
        Config = config;
    }

    /// <summary>
    /// 操作结果状态。
    /// </summary>
    public DataSourceOperationStatus Status { get; }

    /// <summary>
    /// 结果或失败原因的可读消息，不含任何明文密码。
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// 成功时携带的连接配置（保存为落盘后的配置，删除为被删除的配置）；失败状态为 null。
    /// </summary>
    public DataSourceConfig? Config { get; }

    /// <summary>
    /// 是否操作成功。
    /// </summary>
    public bool IsSuccess => Status == DataSourceOperationStatus.Success;

    /// <summary>
    /// 构造成功结果，携带落盘（或被删除）的连接配置。
    /// </summary>
    /// <param name="config">成功操作对应的连接配置。</param>
    /// <returns>Success 状态的结果。</returns>
    public static DataSourceOperationResult Successful(DataSourceConfig config)
    {
        return new DataSourceOperationResult(DataSourceOperationStatus.Success, "操作成功", config);
    }

    /// <summary>
    /// 构造失败结果，携带失败状态与可读消息。
    /// </summary>
    /// <param name="status">失败状态（非 Success）。</param>
    /// <param name="message">失败原因可读消息。</param>
    /// <returns>对应失败状态的结果。</returns>
    public static DataSourceOperationResult Failed(DataSourceOperationStatus status, string message)
    {
        return new DataSourceOperationResult(status, message, null);
    }
}
