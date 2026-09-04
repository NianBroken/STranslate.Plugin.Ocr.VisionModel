using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using System.Windows;

namespace STranslate.Plugin.Ocr.VisionModel.ViewModel;

/// <summary>
/// 多模态OCR 设置页视图模型。
/// 所有字段变化都会同步到 SDK 提供的插件存储，校验命令使用当前内存中的完整设置。
/// </summary>
public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly IPluginContext _context;
    private readonly Settings _settings;
    private readonly Main _main;
    private CancellationTokenSource? _validationCancellation;

    public SettingsViewModel(IPluginContext context, Settings settings, Main main)
    {
        _context = context;
        _settings = settings;
        _main = main;

        ApiUrl = settings.ApiUrl;
        ApiKey = settings.ApiKey;
        ModelId = settings.ModelId;
        RequestBodyJson = settings.RequestBodyJson;
        RequestHeadersJson = settings.RequestHeadersJson;
        MaxRequestCount = settings.MaxRequestCount;
        TimeoutSeconds = settings.TimeoutSeconds;

        PropertyChanged += OnPropertyChanged;
    }

    /// <summary>
    /// 向设置页公开插件主对象，使提示词配置和当前选中提示词直接绑定到宿主使用的官方模型。
    /// </summary>
    public Main Main => _main;

    [ObservableProperty] public partial string ApiUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string ApiKey { get; set; } = string.Empty;
    [ObservableProperty] public partial string ModelId { get; set; } = string.Empty;
    [ObservableProperty] public partial string RequestBodyJson { get; set; } = string.Empty;
    [ObservableProperty] public partial string RequestHeadersJson { get; set; } = string.Empty;
    [ObservableProperty] public partial double? MaxRequestCount { get; set; }
    [ObservableProperty] public partial double? TimeoutSeconds { get; set; }
    [ObservableProperty] public partial string ValidationResult { get; private set; } = "校验尚未返回结果。";
    [ObservableProperty] public partial bool IsValidating { get; private set; }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ApiUrl): _settings.ApiUrl = ApiUrl; break;
            case nameof(ApiKey): _settings.ApiKey = ApiKey; break;
            case nameof(ModelId): _settings.ModelId = ModelId; break;
            case nameof(RequestBodyJson): _settings.RequestBodyJson = RequestBodyJson; break;
            case nameof(RequestHeadersJson): _settings.RequestHeadersJson = RequestHeadersJson; break;
            case nameof(MaxRequestCount): _settings.MaxRequestCount = ToNullableInt(MaxRequestCount); break;
            case nameof(TimeoutSeconds): _settings.TimeoutSeconds = ToNullableInt(TimeoutSeconds); break;
            default: return;
        }
        _context.SaveSettingStorage<Settings>();
    }

    [RelayCommand]
    private async Task ValidateAsync()
    {
        _validationCancellation?.Cancel();
        _validationCancellation?.Dispose();
        _validationCancellation = new CancellationTokenSource();
        IsValidating = true;
        ValidationResult = "校验尚未返回结果。";

        try
        {
            await _main.ValidateAsync(text =>
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.CheckAccess())
                    ValidationResult = text;
                else
                    dispatcher.Invoke(() => ValidationResult = text);
            }, _validationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            ValidationResult = "校验已取消，未获得模型文本。";
        }
        catch (Exception ex)
        {
            ValidationResult = ex.Message;
        }
        finally
        {
            IsValidating = false;
        }
    }

    [RelayCommand]
    private void EditPrompt()
    {
        var dialog = _context.GetPromptEditWindow(_main.Prompts);
        if (dialog.ShowDialog() != true) return;

        // 提示词编辑窗口直接维护宿主提供的提示词对象，保存时复制为设置存储使用的独立列表。
        _settings.Prompts = _main.Prompts.Select(prompt => prompt.Clone()).ToList();
        _context.SaveSettingStorage<Settings>();
    }

    private static int? ToNullableInt(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return null;
        // 向下取整可以保留低于限制的输入状态，例如 0.5 会保存为 0 并由统一校验给出明确错误。
        return (int)Math.Truncate(value.Value);
    }

    public void Dispose()
    {
        PropertyChanged -= OnPropertyChanged;
        _validationCancellation?.Cancel();
        _validationCancellation?.Dispose();
        _validationCancellation = null;
    }
}
