using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using STranslate.Plugin.Ocr.VisionModel.View;
using STranslate.Plugin.Ocr.VisionModel.ViewModel;
using System.Collections.ObjectModel;
using System.Windows.Controls;

namespace STranslate.Plugin.Ocr.VisionModel;

/// <summary>
/// 多模态OCR 插件主入口。
/// 插件遵循 STranslate 的 IOcrPlugin 契约，并把所有网络、日志和配置能力交给宿主上下文处理。
/// </summary>
public sealed class Main : ObservableObject, IOcrPlugin, ILlm
{
    private const string StreamingIdleTimeoutMode = "流式相邻响应空闲超时";
    private const string NonStreamingTotalTimeoutMode = "非流式总超时";

    private Control? _settingUi;
    private SettingsViewModel? _viewModel;
    private IPluginContext _context = null!;
    private Settings _settings = null!;

    /// <summary>
    /// 暴露给设置页的官方提示词配置集合。每个 Prompt 的启用状态由宿主提示词编辑窗口维护，
    /// 当前启用项同时作为校验请求和正式 OCR 请求的提示词来源。
    /// </summary>
    public ObservableCollection<Prompt> Prompts { get; } = [];

    /// <summary>返回当前启用的提示词，并在设置页选择发生变化后保存选择状态。</summary>
    public Prompt? SelectedPrompt
    {
        get => Prompts.FirstOrDefault(prompt => prompt.IsEnabled);
        set => SelectPrompt(value);
    }

    /// <summary>
    /// 使用 STranslate 官方 ILlm 选择模式更新启用状态并持久化提示词配置。
    /// </summary>
    public void SelectPrompt(Prompt? prompt)
    {
        if (prompt is null || !Prompts.Contains(prompt)) return;

        foreach (var item in Prompts)
            item.IsEnabled = ReferenceEquals(item, prompt);

        OnPropertyChanged(nameof(SelectedPrompt));
        _settings.Prompts = Prompts.Select(item => item.Clone()).ToList();
        _context.SaveSettingStorage<Settings>();
    }

    public IEnumerable<LangEnum> SupportedLanguages => Enum.GetValues<LangEnum>();

    /// <summary>
    /// 多模态OCR 只返回纯文本，不提供像素坐标框。
    /// </summary>
    public bool SupportBoxPoints() => false;

    public Control GetSettingUI()
    {
        _viewModel ??= new SettingsViewModel(_context, _settings, this);
        _settingUi ??= new SettingsView { DataContext = _viewModel };
        return _settingUi;
    }

    public void Init(IPluginContext context)
    {
        _context = context;
        _settings = context.LoadSettingStorage<Settings>();
        _settings.Prompts ??= [];
        Prompts.Clear();
        _settings.Prompts.ForEach(Prompts.Add);
    }

    public string? GetLanguage(LangEnum langEnum) => null;

    public async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.Now;
        try
        {
            var prompt = GetSelectedPrompt();
            ValidateSettings(_settings, prompt);
            if (request.ImageData is null || request.ImageData.Length == 0)
                throw new InvalidOperationException("OCR 请求图片为空，无法发送给视觉模型。");

            var normalization = ImageDataHelper.NormalizeForVisionModel(request.ImageData);
            LogImageNormalization(normalization);
            var finalText = await ExecuteWithRetriesAsync(normalization.Data, null, cancellationToken, normalization, prompt);
            var result = new OcrResult { Duration = DateTimeOffset.Now - started };
            result.OcrContents.Add(new OcrContent { Text = finalText });
            return result;
        }
        catch (OperationCanceledException ex)
        {
            var message = BuildFailureMessage("OCR 识别已取消", ex, 0, null);
            _context.Logger.LogWarning(ex, "多模态OCR 已取消。{Message}", message);
            return CreateFailureResult(message, DateTimeOffset.Now - started);
        }
        catch (Exception ex)
        {
            var message = ex is RetryExhaustedException exhausted
                ? exhausted.Message
                : BuildFailureMessage("OCR 识别失败", ex, 0, null);
            _context.Logger.LogError(ex, "多模态OCR 执行失败。{Message}", message);
            return CreateFailureResult(message, DateTimeOffset.Now - started);
        }
    }

    /// <summary>
    /// 设置页校验入口，向调用方提供每次累计文本变化，从而实时显示流式输出。
    /// </summary>
    internal Task<string> ValidateAsync(Action<string> onTextUpdated, CancellationToken cancellationToken = default)
    {
        var prompt = GetSelectedPrompt();
        ValidateSettings(_settings, prompt);
        var validationImage = ValidationImageProvider.Load();
        var normalization = ImageDataHelper.NormalizeForVisionModel(validationImage);
        LogImageNormalization(normalization);
        return ExecuteWithRetriesAsync(normalization.Data, onTextUpdated, cancellationToken, normalization, prompt);
    }

    public void Dispose() => _viewModel?.Dispose();

    private async Task<string> ExecuteWithRetriesAsync(
        byte[] imageData,
        Action<string>? onTextUpdated,
        CancellationToken cancellationToken,
        ImageNormalizationResult? normalization,
        Prompt prompt)
    {
        ValidateSettings(_settings, prompt);
        var maxAttempts = _settings.MaxRequestCount!.Value;
        AttemptDiagnostics? last = null;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var diagnostics = await ExecuteAttemptAsync(imageData, attempt, onTextUpdated, cancellationToken, normalization, prompt);
                last = diagnostics;
                if (!string.IsNullOrWhiteSpace(diagnostics.Text))
                {
                    _context.Logger.LogInformation("多模态OCR 成功。Attempt={Attempt}, TextLength={TextLength}", attempt, diagnostics.Text.Length);
                    return diagnostics.Text.Trim();
                }

                lastException = new InvalidOperationException("模型响应中没有非空纯文本。");
                _context.Logger.LogWarning("多模态OCR 返回空内容，将继续重试。Attempt={Attempt}", attempt);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (ex is AttemptFailedException failed)
                    last = failed.Diagnostics;
                _context.Logger.LogError(ex, "多模态OCR 请求失败，将继续重试。Attempt={Attempt}", attempt);
            }
        }

        var failure = BuildFailureMessage(
            $"多模态OCR 请求失败，已用完全部 {maxAttempts} 次请求次数",
            lastException ?? new InvalidOperationException("模型没有返回非空文本。"),
            maxAttempts,
            last);
        _context.Logger.LogError("{Failure}", failure);
        throw new RetryExhaustedException(failure, lastException, last);
    }

    private async Task<AttemptDiagnostics> ExecuteAttemptAsync(
        byte[] imageData,
        int attempt,
        Action<string>? onTextUpdated,
        CancellationToken cancellationToken,
        ImageNormalizationResult? normalization,
        Prompt prompt)
    {
        var requestStarted = DateTimeOffset.Now;
        var built = RequestBuilder.Build(_settings, imageData, prompt);
        var url = BuildRequestUrl(_settings.ApiUrl);
        var streamEnabled = ReadStreamEnabled(built.Body["stream"]);
        var timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds!.Value);
        var options = new Options
        {
            Headers = built.Headers,
            Timeout = streamEnabled ? Timeout.InfiniteTimeSpan : timeout,
            ContentType = "application/json"
        };
        var headersForLog = string.Join(", ", built.Headers.Select(pair => $"{pair.Key}={RedactHeader(pair.Key, pair.Value)}"));
        var ignoredCustomPaths = built.IgnoredCustomPaths.Count == 0 ? "无" : string.Join(", ", built.IgnoredCustomPaths);
        var ignoredCustomHeaders = built.IgnoredCustomHeaderKeys.Count == 0 ? "无" : string.Join(", ", built.IgnoredCustomHeaderKeys);
        var customBodyFields = built.CustomBodyFields.Count == 0 ? "无" : string.Join(", ", built.CustomBodyFields);
        var customHeaderFields = built.CustomHeaderFields.Count == 0 ? "无" : string.Join(", ", built.CustomHeaderFields);
        _context.Logger.LogInformation(
            "多模态OCR 请求开始。Attempt={Attempt}, RequestTime={RequestTime}, Url={Url}, Model={Model}, ImageBytes={ImageBytes}, MimeType={MimeType}, Sha256={Sha256}, Headers={Headers}, TimeoutSeconds={TimeoutSeconds}, TimeoutMode={TimeoutMode}, RequestBodyRaw={RequestBodyRaw}",
            attempt,
            requestStarted.ToString("O"),
            url,
            _settings.ModelId,
            imageData.Length,
            built.MimeType,
            ImageDataHelper.ComputeSha256(imageData),
            headersForLog,
            _settings.TimeoutSeconds.Value,
            streamEnabled ? StreamingIdleTimeoutMode : NonStreamingTotalTimeoutMode,
            built.RawBody);
        _context.Logger.LogInformation(
            "多模态OCR 自定义字段处理。IgnoredManagedPaths={IgnoredManagedPaths}, IgnoredCoreHeaders={IgnoredCoreHeaders}, CustomFieldsRemain=非消息字段均已保留",
            ignoredCustomPaths,
            ignoredCustomHeaders);
        _context.Logger.LogInformation(
            "多模态OCR 自定义字段类型。BodyFields={BodyFields}, HeaderFields={HeaderFields}, ThinkingLocation={ThinkingLocation}",
            customBodyFields,
            customHeaderFields,
            customBodyFields.Contains("thinking:", StringComparison.OrdinalIgnoreCase) ? "请求体" : (customHeaderFields.Contains("thinking:", StringComparison.OrdinalIgnoreCase) ? "请求头" : "未填写"));
        _context.Logger.LogInformation(
            "多模态OCR 图片传输信息。Attempt={Attempt}, OriginalImageBytes={OriginalImageBytes}, OriginalMimeType={OriginalMimeType}, OriginalSha256={OriginalSha256}, SentImageBytes={SentImageBytes}, SentMimeType={SentMimeType}, SentSha256={SentSha256}, LosslessBmpToPng={LosslessBmpToPng}",
            attempt,
            normalization?.OriginalData.Length ?? imageData.Length,
            normalization?.OriginalMimeType ?? built.MimeType,
            ImageDataHelper.ComputeSha256(normalization?.OriginalData ?? imageData),
            imageData.Length,
            built.MimeType,
            ImageDataHelper.ComputeSha256(imageData),
            normalization?.Converted ?? false);

        var parser = new StreamResponseParser();
        var rawResponse = new System.Text.StringBuilder();
        var responseLineCount = 0;
        DateTimeOffset? firstResponseAt = null;
        DateTimeOffset? lastResponseAt = null;
        var maxInterResponseWaitMilliseconds = 0d;
        try
        {
            if (streamEnabled)
            {
                using var streamTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var streamToken = streamTimeoutCts.Token;
                await using var enumerator = _context.HttpService
                    .StreamPostAsyncEnumerable(url, built.Body, options, streamToken)
                    .GetAsyncEnumerator(streamToken);

                while (true)
                {
                    streamTimeoutCts.CancelAfter(timeout);
                    bool hasNext;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync();
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && streamTimeoutCts.IsCancellationRequested)
                    {
                        throw new StreamIdleTimeoutException(
                            timeout,
                            lastResponseAt,
                            responseLineCount,
                            parser.CurrentText.Length,
                            requestStarted);
                    }
                    finally
                    {
                        streamTimeoutCts.CancelAfter(Timeout.InfiniteTimeSpan);
                    }

                    if (!hasNext)
                        break;

                    var line = enumerator.Current;
                    if (string.IsNullOrEmpty(line))
                        continue;

                    var responseAt = DateTimeOffset.Now;
                    var waitMilliseconds = lastResponseAt.HasValue
                        ? (responseAt - lastResponseAt.Value).TotalMilliseconds
                        : (responseAt - requestStarted).TotalMilliseconds;
                    responseLineCount++;
                    firstResponseAt ??= responseAt;
                    lastResponseAt = responseAt;
                    maxInterResponseWaitMilliseconds = Math.Max(maxInterResponseWaitMilliseconds, waitMilliseconds);
                    rawResponse.AppendLine(line);
                    _context.Logger.LogInformation(
                        "多模态OCR 流式响应 RAW。Attempt={Attempt}, ResponseTime={ResponseTime}, ResponseLineCount={ResponseLineCount}, InterResponseWaitMilliseconds={InterResponseWaitMilliseconds}, CumulativeTextLength={CumulativeTextLength}, RawLine={RawLine}",
                        attempt,
                        responseAt.ToString("O"),
                        responseLineCount,
                        waitMilliseconds,
                        parser.CurrentText.Length,
                        line);
                    var previous = parser.CurrentText;
                    var current = parser.AppendStreamLine(line);
                    if (!string.Equals(previous, current, StringComparison.Ordinal))
                        onTextUpdated?.Invoke(current);
                }
            }
            else
            {
                var response = await _context.HttpService.PostAsync(url, built.Body, options, cancellationToken);
                rawResponse.Append(response);
                _context.Logger.LogInformation("多模态OCR 普通响应 RAW。Attempt={Attempt}, ResponseTime={ResponseTime}, RawBody={RawBody}", attempt, DateTimeOffset.Now.ToString("O"), response);
                parser.AppendNonStreamingResponse(response);
                onTextUpdated?.Invoke(parser.CurrentText);
            }

            var completed = DateTimeOffset.Now;
            _context.Logger.LogInformation(
                "多模态OCR 响应完成。Attempt={Attempt}, ResponseTime={ResponseTime}, ResponseBodyRaw={ResponseBodyRaw}, ResponseLineCount={ResponseLineCount}, FirstResponseTime={FirstResponseTime}, LastResponseTime={LastResponseTime}, MaxInterResponseWaitMilliseconds={MaxInterResponseWaitMilliseconds}, TimeoutMode={TimeoutMode}, TextLength={TextLength}",
                attempt,
                completed.ToString("O"),
                rawResponse.ToString(),
                responseLineCount,
                firstResponseAt?.ToString("O") ?? "无",
                lastResponseAt?.ToString("O") ?? "无",
                maxInterResponseWaitMilliseconds,
                streamEnabled ? StreamingIdleTimeoutMode : NonStreamingTotalTimeoutMode,
                parser.CurrentText.Length);
            return new AttemptDiagnostics(parser.CurrentText, rawResponse.ToString(), requestStarted, completed, url, built.RawBody, headersForLog, responseLineCount, firstResponseAt, lastResponseAt, maxInterResponseWaitMilliseconds, streamEnabled ? StreamingIdleTimeoutMode : NonStreamingTotalTimeoutMode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var completed = DateTimeOffset.Now;
            _context.Logger.LogError(ex, "多模态OCR 响应异常。Attempt={Attempt}, ResponseTime={ResponseTime}, ResponseBodyRaw={ResponseBodyRaw}, ResponseLineCount={ResponseLineCount}, FirstResponseTime={FirstResponseTime}, LastResponseTime={LastResponseTime}, MaxInterResponseWaitMilliseconds={MaxInterResponseWaitMilliseconds}, TimeoutMode={TimeoutMode}", attempt, completed.ToString("O"), rawResponse.ToString(), responseLineCount, firstResponseAt?.ToString("O") ?? "无", lastResponseAt?.ToString("O") ?? "无", maxInterResponseWaitMilliseconds, streamEnabled ? StreamingIdleTimeoutMode : NonStreamingTotalTimeoutMode);
            throw new AttemptFailedException(
                $"第 {attempt} 次请求在 {completed:O} 失败。响应 RAW：{rawResponse}",
                new AttemptDiagnostics(parser.CurrentText, rawResponse.ToString(), requestStarted, completed, url, built.RawBody, headersForLog, responseLineCount, firstResponseAt, lastResponseAt, maxInterResponseWaitMilliseconds, streamEnabled ? StreamingIdleTimeoutMode : NonStreamingTotalTimeoutMode),
                ex);
        }
    }

    /// <summary>
    /// 自定义请求体允许使用 JSON 布尔值或字符串布尔值控制 stream，未填写时保持插件默认流式行为。
    /// </summary>
    private static bool ReadStreamEnabled(System.Text.Json.Nodes.JsonNode? streamValue)
    {
        if (streamValue is System.Text.Json.Nodes.JsonValue value)
        {
            if (value.TryGetValue<bool>(out var booleanValue)) return booleanValue;
            if (value.TryGetValue<string>(out var stringValue) && bool.TryParse(stringValue, out var parsedValue)) return parsedValue;
        }

        return true;
    }

    /// <summary>
    /// 使用 STranslate 官方 URL 规则生成地址，并兼容带有供应商前缀的 OpenAI 兼容根路径。
    /// 例如 https://host/codex/v1 需要变成 https://host/codex/v1/chat/completions。
    /// 地址末尾使用 # 时仍由 UrlHelper 保持用户指定的原样路径，不做任何追加。
    /// </summary>
    private static string BuildRequestUrl(string configuredUrl)
    {
        var normalized = configuredUrl.Trim();
        var resolved = UrlHelper.BuildFinalUrl(normalized, "/v1/chat/completions");
        if (normalized.EndsWith('#')) return resolved;

        if (Uri.TryCreate(resolved, UriKind.Absolute, out var uri)
            && uri.AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(uri)
            {
                Path = uri.AbsolutePath.TrimEnd('/') + "/chat/completions"
            };
            return builder.Uri.ToString();
        }

        return resolved;
    }

    private Prompt GetSelectedPrompt()
    {
        return Prompts.FirstOrDefault(prompt => prompt.IsEnabled)
            ?? throw new InvalidOperationException("提示词配置中没有启用的提示词，请在提示词配置区域选择一个提示词。");
    }

    private static void ValidateSettings(Settings settings, Prompt prompt)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiUrl)) throw new InvalidOperationException("API 地址不能为空。");
        if (!Uri.TryCreate(settings.ApiUrl.TrimEnd('#').Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("API 地址必须是有效的 HTTP 或 HTTPS 地址。");
        if (string.IsNullOrWhiteSpace(settings.ApiKey)) throw new InvalidOperationException("API 密钥不能为空。");
        if (string.IsNullOrWhiteSpace(settings.ModelId)) throw new InvalidOperationException("模型 ID 不能为空。");
        var systemPrompt = prompt.Items.FirstOrDefault(item => string.Equals(item.Role, "system", StringComparison.OrdinalIgnoreCase))?.Content;
        if (string.IsNullOrWhiteSpace(systemPrompt)) throw new InvalidOperationException("当前启用提示词的 system 内容不能为空，请在提示词配置区域填写系统提示词。");
        if (settings.MaxRequestCount is null || settings.MaxRequestCount < 1) throw new InvalidOperationException("最大请求次数必须大于或等于 1。");
        if (settings.TimeoutSeconds is null || settings.TimeoutSeconds < 3) throw new InvalidOperationException("超时时间必须大于或等于 3 秒。");
    }

    private static OcrResult CreateFailureResult(string message, TimeSpan duration)
    {
        // STranslate 宿主只有在 IsSuccess 为 true 时才会把 OcrResult.Text 写入结果区域。
        // 失败信息同时保存在 ErrorMessage 和 OcrContent.Text 中，使用户能够看到具体诊断内容。
        // 该状态仅用于展示错误文本，不表示模型识别成功，也不会把错误内容伪装成识别结果。
        var result = new OcrResult
        {
            Duration = duration,
            ErrorMessage = message,
            IsSuccess = true
        };
        result.OcrContents.Add(new OcrContent { Text = message });
        return result;
    }

    /// <summary>
    /// 记录图片标准化结果，明确区分宿主原始字节和实际发送给模型的字节。
    /// </summary>
    private void LogImageNormalization(ImageNormalizationResult normalization)
    {
        _context.Logger.LogInformation(
            "多模态OCR 图片标准化完成。OriginalImageBytes={OriginalImageBytes}, OriginalMimeType={OriginalMimeType}, OriginalSha256={OriginalSha256}, SentImageBytes={SentImageBytes}, SentMimeType={SentMimeType}, SentSha256={SentSha256}, LosslessBmpToPng={LosslessBmpToPng}",
            normalization.OriginalData.Length,
            normalization.OriginalMimeType,
            ImageDataHelper.ComputeSha256(normalization.OriginalData),
            normalization.Data.Length,
            normalization.MimeType,
            ImageDataHelper.ComputeSha256(normalization.Data),
            normalization.Converted);
    }

    private static string BuildFailureMessage(string title, Exception exception, int attempts, AttemptDiagnostics? diagnostics)
    {
        return string.Join(Environment.NewLine,
            title,
            $"异常类型：{exception.GetType().FullName}",
            $"异常信息：{exception.Message}",
            $"已执行请求次数：{attempts}",
            diagnostics is null ? "最近一次请求诊断：无" : $"最近一次请求时间：{diagnostics.RequestStarted:O}{Environment.NewLine}请求完成时间：{diagnostics.ResponseCompleted:O}{Environment.NewLine}请求地址：{diagnostics.Url}{Environment.NewLine}请求头：{diagnostics.Headers}{Environment.NewLine}请求体 RAW：{diagnostics.RawRequest}{Environment.NewLine}最近一次响应 RAW：{diagnostics.RawResponse}",
            diagnostics is null ? "响应行数：无" : $"响应行数：{diagnostics.ResponseLineCount}{Environment.NewLine}首次响应时间：{diagnostics.FirstResponseAt?.ToString("O") ?? "无"}{Environment.NewLine}最近一次响应时间：{diagnostics.LastResponseAt?.ToString("O") ?? "无"}{Environment.NewLine}最大相邻响应间隔毫秒数：{diagnostics.MaxInterResponseWaitMilliseconds}{Environment.NewLine}超时模式：{diagnostics.TimeoutMode}",
            $"异常堆栈：{exception}");
    }

    private static string RedactHeader(string name, string value)
    {
        return name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            ? "Bearer ***"
            : value;
    }

    private sealed record AttemptDiagnostics(
        string Text,
        string RawResponse,
        DateTimeOffset RequestStarted,
        DateTimeOffset ResponseCompleted,
        string Url = "",
        string RawRequest = "",
        string Headers = "",
        int ResponseLineCount = 0,
        DateTimeOffset? FirstResponseAt = null,
        DateTimeOffset? LastResponseAt = null,
        double MaxInterResponseWaitMilliseconds = 0,
        string TimeoutMode = "");

    /// <summary>
    /// 在请求已获得部分响应后抛出异常时，保留本次诊断信息供最终错误结果和重试日志使用。
    /// </summary>
    private sealed class AttemptFailedException : Exception
    {
        public AttemptFailedException(string message, AttemptDiagnostics diagnostics, Exception innerException)
            : base(message, innerException)
        {
            Diagnostics = diagnostics;
        }

        public AttemptDiagnostics Diagnostics { get; }
    }

    /// <summary>
    /// 表示流式响应在相邻数据之间超过配置时限，且请求被插件层主动取消。
    /// </summary>
    private sealed class StreamIdleTimeoutException : TimeoutException
    {
        public StreamIdleTimeoutException(TimeSpan timeout, DateTimeOffset? lastResponseAt, int responseLineCount, int textLength, DateTimeOffset requestStarted)
            : base(string.Join(Environment.NewLine,
                "流式响应空闲超时",
                $"超时时间：{timeout.TotalSeconds:0} 秒",
                $"最近一次响应时间：{lastResponseAt?.ToString("O") ?? "无"}",
                $"已接收响应行数：{responseLineCount}",
                $"当前累计文本长度：{textLength}",
                $"请求开始时间：{requestStarted:O}",
                "请求已经收到部分响应，但连续等待超过配置的相邻响应间隔。"))
        {
            LastResponseAt = lastResponseAt;
            ResponseLineCount = responseLineCount;
            TextLength = textLength;
        }

        public DateTimeOffset? LastResponseAt { get; }
        public int ResponseLineCount { get; }
        public int TextLength { get; }
    }

    /// <summary>
    /// 保存重试耗尽后的完整失败文本和最后一次请求诊断，避免外层异常处理丢失关键日志信息。
    /// </summary>
    private sealed class RetryExhaustedException : Exception
    {
        public RetryExhaustedException(string message, Exception? innerException, AttemptDiagnostics? diagnostics)
            : base(message, innerException)
        {
            Diagnostics = diagnostics;
        }

        public AttemptDiagnostics? Diagnostics { get; }
    }
}
