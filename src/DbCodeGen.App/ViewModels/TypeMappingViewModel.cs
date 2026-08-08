using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbCodeGen.App.Services;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates;

namespace DbCodeGen.App.ViewModels;

/// <summary>
/// 类型映射行视图模型，承载单条映射的四个可编辑字段，供类型映射窗口 DataGrid 双向绑定。
/// 保存时经 ToEntry 转为持久化模型写入配置。
/// </summary>
public sealed partial class TypeMappingRowViewModel : ObservableObject
{
    /// <summary>
    /// 数据库原始类型，如 bigint、jsonb。
    /// </summary>
    [ObservableProperty]
    private string _dbType = string.Empty;

    /// <summary>
    /// 目标语言类型，如 Long、BigDecimal。
    /// </summary>
    [ObservableProperty]
    private string _targetType = string.Empty;

    /// <summary>
    /// 可选导包，如 java.math.BigDecimal，可为空。
    /// </summary>
    [ObservableProperty]
    private string _import = string.Empty;

    /// <summary>
    /// 可选备注说明，可为空。
    /// </summary>
    [ObservableProperty]
    private string _remark = string.Empty;

    /// <summary>
    /// 使用空值构造映射行，供新增操作使用。
    /// </summary>
    public TypeMappingRowViewModel()
    {
    }

    /// <summary>
    /// 由持久化映射条目构造映射行，空字段以空串回填。
    /// </summary>
    /// <param name="entry">持久化映射条目。</param>
    /// <exception cref="ArgumentNullException">entry 为 null 时抛出。</exception>
    public TypeMappingRowViewModel(TypeMappingEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        DbType = entry.DbType ?? string.Empty;
        TargetType = entry.TargetType ?? string.Empty;
        Import = entry.Import ?? string.Empty;
        Remark = entry.Remark ?? string.Empty;
    }

    /// <summary>
    /// 将当前行转为持久化映射条目，字段去首尾空白，空白导包/备注置空。
    /// </summary>
    /// <returns>持久化映射条目。</returns>
    public TypeMappingEntry ToEntry()
    {
        return new TypeMappingEntry
        {
            DbType = DbType.Trim(),
            TargetType = TargetType.Trim(),
            Import = string.IsNullOrWhiteSpace(Import) ? null : Import.Trim(),
            Remark = string.IsNullOrWhiteSpace(Remark) ? null : Remark
        };
    }
}

/// <summary>
/// 类型映射窗口视图模型，承载全局类型映射表的加载、增删改、恢复默认、导入导出与保存。
/// 映射写入配置后生成代码即时生效，无需重启。
/// </summary>
public sealed partial class TypeMappingViewModel : ObservableObject
{
    private readonly IConfigService _configService;
    private readonly IDialogService _dialogService;
    private readonly IConfirmDialogService _confirmDialogService;
    private readonly IFilePickerService _filePickerService;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// 映射行集合，绑定类型映射窗口 DataGrid。
    /// </summary>
    public ObservableCollection<TypeMappingRowViewModel> Rows { get; } = new();

    /// <summary>
    /// 当前选中的映射行，用于删除操作。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveRowCommand))]
    private TypeMappingRowViewModel? _selectedRow;

    /// <summary>
    /// 使用配置服务、消息提示服务、二次确认服务与文件选择服务构造类型映射视图模型，并加载当前映射表。
    /// </summary>
    /// <param name="configService">配置持久化服务，映射表读写的唯一通道。</param>
    /// <param name="dialogService">消息提示服务，用于校验失败与保存结果反馈。</param>
    /// <param name="confirmDialogService">二次确认服务，用于恢复默认与导入前的覆盖确认。</param>
    /// <param name="filePickerService">文件选择服务，用于映射表的 JSON 导入导出。</param>
    /// <exception cref="ArgumentNullException">任一依赖参数为 null 时抛出。</exception>
    public TypeMappingViewModel(
        IConfigService configService,
        IDialogService dialogService,
        IConfirmDialogService confirmDialogService,
        IFilePickerService filePickerService)
    {
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(confirmDialogService);
        ArgumentNullException.ThrowIfNull(filePickerService);

        _configService = configService;
        _dialogService = dialogService;
        _confirmDialogService = confirmDialogService;
        _filePickerService = filePickerService;

        LoadFromConfig();
    }

    /// <summary>
    /// 新增一行空映射并选中，供用户填写新类型映射。
    /// </summary>
    [RelayCommand]
    private void AddRow()
    {
        var row = new TypeMappingRowViewModel();
        Rows.Add(row);
        SelectedRow = row;
    }

    /// <summary>
    /// 删除当前选中的映射行。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveRow))]
    private void RemoveRow()
    {
        if (SelectedRow is null)
        {
            return;
        }

        Rows.Remove(SelectedRow);
        SelectedRow = null;
    }

    /// <summary>
    /// 判定删除命令是否可执行：存在选中行时可删除。
    /// </summary>
    private bool CanRemoveRow() => SelectedRow is not null;

    /// <summary>
    /// 将当前映射表重置为内置默认映射集，重置前经二次确认。
    /// </summary>
    [RelayCommand]
    private async Task RestoreDefaultsAsync()
    {
        bool confirmed = await _confirmDialogService.ConfirmAsync(
            "恢复默认",
            "将用内置默认映射集替换当前映射表，当前编辑内容将丢失。确认恢复？");
        if (!confirmed)
        {
            return;
        }

        Rows.Clear();
        foreach (TypeMappingEntry entry in TypeMappingDefaults.BuildDefault())
        {
            Rows.Add(new TypeMappingRowViewModel(entry));
        }
    }

    /// <summary>
    /// 将当前映射表导出为 JSON 文件，供备份分享或迁移使用。
    /// </summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        string? path = await _filePickerService.PickSaveJsonAsync("type-mappings.json");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        List<TypeMappingEntry> allEntries = Rows.Select(row => row.ToEntry()).ToList();
        List<TypeMappingEntry> entries = allEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.DbType) && !string.IsNullOrWhiteSpace(entry.TargetType))
            .ToList();

        try
        {
            string json = JsonSerializer.Serialize(entries, JsonOptions);
            await File.WriteAllTextAsync(path, json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _dialogService.ShowError($"导出失败：{exception.Message}");
            return;
        }

        _dialogService.ShowInfo($"已导出 {entries.Count} 条类型映射。");
    }

    /// <summary>
    /// 从 JSON 文件导入映射表，导入内容替换当前映射列表，导入前经二次确认。
    /// </summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        string? path = await _filePickerService.PickOpenJsonAsync();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        List<TypeMappingEntry> entries;
        try
        {
            string json = await File.ReadAllTextAsync(path);
            entries = JsonSerializer.Deserialize<List<TypeMappingEntry>>(json, JsonOptions) ?? new List<TypeMappingEntry>();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _dialogService.ShowError($"导入失败：{exception.Message}");
            return;
        }

        List<TypeMappingEntry> validEntries = entries
            .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.DbType) && !string.IsNullOrWhiteSpace(entry.TargetType))
            .ToList();
        if (validEntries.Count == 0)
        {
            _dialogService.ShowError("导入文件不含有效的类型映射条目。");
            return;
        }

        bool confirmed = await _confirmDialogService.ConfirmAsync(
            "导入类型映射",
            $"将用导入的 {validEntries.Count} 条映射替换当前映射列表，确认导入？");
        if (!confirmed)
        {
            return;
        }

        Rows.Clear();
        foreach (TypeMappingEntry entry in validEntries)
        {
            Rows.Add(new TypeMappingRowViewModel(entry));
        }
    }

    /// <summary>
    /// 校验并保存映射表到配置，保存成功后提示即时生效。
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        if (!TryValidate(out string? errorMessage))
        {
            // 校验失败路径下 errorMessage 必然已填充,空值不可能进入提示分支
            _dialogService.ShowError(errorMessage!);
            return;
        }

        AppConfig config = _configService.Current;
        config.TypeMappings = Rows.Select(row => row.ToEntry()).ToList();

        try
        {
            _configService.Save();
        }
        catch (ConfigSaveException exception)
        {
            _dialogService.ShowError($"映射保存失败：{exception.Message}");
            return;
        }

        _dialogService.ShowInfo("类型映射已保存，生成代码时即时生效。");
    }

    /// <summary>
    /// 从配置快照加载映射表到编辑行集合。
    /// </summary>
    private void LoadFromConfig()
    {
        AppConfig config = _configService.Load();
        foreach (TypeMappingEntry entry in config.TypeMappings)
        {
            if (entry is null)
            {
                continue;
            }

            Rows.Add(new TypeMappingRowViewModel(entry));
        }
    }

    /// <summary>
    /// 校验映射表：数据库类型与目标类型均不可为空，数据库类型不可重复（忽略大小写与长度/精度修饰）。
    /// </summary>
    /// <param name="errorMessage">校验失败的可读消息；校验通过为 null。</param>
    private bool TryValidate(out string? errorMessage)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (TypeMappingRowViewModel row in Rows)
        {
            if (string.IsNullOrWhiteSpace(row.DbType) || string.IsNullOrWhiteSpace(row.TargetType))
            {
                errorMessage = "存在未填写的映射行：数据库类型与目标类型均不能为空。";
                return false;
            }

            // 与解析匹配同一规范化规则判重，保证 varchar 与 varchar(255) 等修饰差异也被视为重复
            string key = TypeMapper.Normalize(row.DbType);
            if (!seen.Add(key))
            {
                errorMessage = $"存在重复的数据库类型：{row.DbType.Trim()}，请合并或删除重复行。";
                return false;
            }
        }

        errorMessage = null;
        return true;
    }
}
