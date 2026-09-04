using STranslate.Plugin;

namespace STranslate.Plugin.Ocr.VisionModel;

/// <summary>
/// 保存多模态OCR 服务的全部用户设置。
/// 字符串和数值设置默认保持为空，由设置页和请求入口统一执行必填校验。
/// </summary>
public sealed class Settings
{
    /// <summary>视觉模型 API 地址。</summary>
    public string ApiUrl { get; set; } = string.Empty;

    /// <summary>API 密钥。</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>模型 ID。</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>用户自定义 JSON 请求体，空值表示使用内置请求体。</summary>
    public string RequestBodyJson { get; set; } = string.Empty;

    /// <summary>用户自定义 JSON 请求头，空值表示使用内置请求头。</summary>
    public string RequestHeadersJson { get; set; } = string.Empty;

    /// <summary>
    /// 使用 STranslate 官方 Prompt 模型保存提示词配置。默认创建一个启用中的空提示词，
    /// 其中同时包含 system 和 user 项，用户可以通过宿主提供的提示词编辑窗口维护内容。
    /// </summary>
    public List<Prompt> Prompts { get; set; } = null!;

    /// <summary>单次 OCR 请求允许使用的最大请求次数。</summary>
    public int? MaxRequestCount { get; set; }

    /// <summary>单次 HTTP 请求的超时时间，单位为秒。</summary>
    public int? TimeoutSeconds { get; set; }
}
