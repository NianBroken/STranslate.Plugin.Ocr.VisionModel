using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace STranslate.Plugin.Ocr.VisionModel;

/// <summary>
/// 解析常见视觉模型的 SSE 和普通 JSON 响应。
/// 解析器只提取模型文本，不把思考过程、工具调用或位置信息交给 OCR 结果。
/// </summary>
internal sealed class StreamResponseParser
{
    private readonly StringBuilder _text = new();
    private bool _insideThinkTag;

    public string AppendStreamLine(string line)
    {
        // SSE 可能在 data 行之前发送 event、id、retry 或注释行，这些行不承载模型正文。
        if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("retry:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith(":", StringComparison.Ordinal))
            return CurrentText;

        var payload = line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? line[5..].Trim()
            : line.Trim();

        if (payload.Length == 0 || payload.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
            return CurrentText;

        JsonNode? json;
        try
        {
            json = JsonNode.Parse(payload);
        }
        catch (Exception ex)
        {
            throw new FormatException($"流式响应不是有效 JSON，原始数据：{line}，解析错误：{ex.Message}", ex);
        }

        if (json is null)
            return CurrentText;

        var error = ExtractError(json);
        if (!string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException($"模型返回错误：{error}");

        var delta = ExtractStreamText(json);
        if (!string.IsNullOrEmpty(delta))
            AppendVisibleText(delta);

        return CurrentText;
    }

    public string AppendNonStreamingResponse(string rawResponse)
    {
        JsonNode json;
        try
        {
            json = JsonNode.Parse(rawResponse)
                ?? throw new FormatException("普通响应 JSON 根节点为空。");
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new FormatException($"普通响应不是有效 JSON，原始响应：{rawResponse}，解析错误：{ex.Message}", ex);
        }

        var error = ExtractError(json);
        if (!string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException($"模型返回错误：{error}");

        var value = ExtractNonStreamingText(json);
        if (!string.IsNullOrWhiteSpace(value))
            AppendVisibleText(value);

        return CurrentText;
    }

    /// <summary>
    /// 读取已经过滤思考标签和视觉标注说明后的用户可见文本。
    /// 逐行剔除模型附带的定位声明，避免这些元数据进入 OCR 结果区域。
    /// </summary>
    public string CurrentText => RemoveMetadataStatements(_text.ToString());

    private void AppendVisibleText(string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var index = 0;
        while (index < normalized.Length)
        {
            if (!_insideThinkTag && normalized[index..].StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
            {
                _insideThinkTag = true;
                index += "<think>".Length;
                continue;
            }

            if (_insideThinkTag)
            {
                var end = normalized.IndexOf("</think>", index, StringComparison.OrdinalIgnoreCase);
                if (end < 0) return;
                _insideThinkTag = false;
                index = end + "</think>".Length;
                continue;
            }

            var nextTag = normalized.IndexOf("<think>", index, StringComparison.OrdinalIgnoreCase);
            var endIndex = nextTag >= 0 ? nextTag : normalized.Length;
            var visible = normalized[index..endIndex];
            if (!string.IsNullOrEmpty(visible) && !string.Equals(visible, "reasoning_content", StringComparison.OrdinalIgnoreCase))
                _text.Append(visible);
            index = endIndex;
        }
    }

    private static string? ExtractStreamText(JsonNode root)
    {
        var choices = root["choices"] as JsonArray;
        if (choices is { Count: > 0 })
        {
            var choice = choices[0];
            var delta = choice?["delta"];
            var value = ReadTextValue(delta?["content"])
                        ?? ReadTextValue(delta?["text"])
                        ?? ReadTextValue(choice?["message"]?["content"]);
            if (!string.IsNullOrEmpty(value)) return value;
        }

        return ReadTextValue(root["output_text"])
            ?? ReadTextValue(root["delta"]?["text"])
            ?? ReadTextValue(root["delta"])
            ?? ReadTextValue(root["response"]?["output_text"])
            ?? ReadTextValue(root["content_block_delta"]?["delta"]?["text"])
            ?? ReadTextValue(root["candidates"]?[0]?["content"]?["parts"]?[0]?["text"])
            ?? ReadTextValue(root["content"]?[0]?["text"]);
    }

    private static string? ExtractNonStreamingText(JsonNode root)
    {
        var choices = root["choices"] as JsonArray;
        if (choices is { Count: > 0 })
        {
            var choice = choices[0];
            return ReadTextValue(choice?["message"]?["content"])
                ?? ReadTextValue(choice?["text"])
                ?? ReadTextValue(choice?["delta"]?["content"]);
        }

        return ReadTextValue(root["output_text"])
            ?? ReadTextValue(root["output"]?[0]?["content"])
            ?? ReadTextValue(root["candidates"]?[0]?["content"]?["parts"]?[0]?["text"])
            ?? ReadTextValue(root["content"])
            ?? ReadTextValue(root["text"]);
    }

    private static string? ReadTextValue(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        if (node is JsonArray array)
        {
            var builder = new StringBuilder();
            foreach (var item in array)
            {
                var itemText = ReadTextValue(item?["text"] ?? item?["content"] ?? item);
                if (!string.IsNullOrEmpty(itemText)) builder.Append(itemText);
            }
            return builder.Length == 0 ? null : builder.ToString();
        }
        return null;
    }

    private static string? ExtractError(JsonNode root)
    {
        return ReadTextValue(root["error"]?["message"])
            ?? ReadTextValue(root["response"]?["error"]?["message"])
            ?? ReadTextValue(string.Equals(root["type"]?.ToString(), "error", StringComparison.OrdinalIgnoreCase) ? root["message"] : null);
    }

    private static string RemoveMetadataStatements(string text)
    {
        // 跨多个 SSE 分片的标签在增量阶段无法一次识别，最终读取时用整体规则再次移除。
        var withoutThinking = Regex.Replace(text, "<think>.*?</think>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        withoutThinking = Regex.Replace(withoutThinking, "<think>.*$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var filteredLines = withoutThinking.Split('\n')
            .Where(line => !line.Contains("无位置信息", StringComparison.OrdinalIgnoreCase)
                           && !line.Contains("无法标注", StringComparison.OrdinalIgnoreCase)
                           && !line.Contains("图上选中文本", StringComparison.OrdinalIgnoreCase)
                           && !line.Contains("坐标信息", StringComparison.OrdinalIgnoreCase)
                           && !line.Contains("bounding box", StringComparison.OrdinalIgnoreCase)
                           && !line.Contains("bounding boxes", StringComparison.OrdinalIgnoreCase));
        return string.Join("\n", filteredLines).Trim();
    }
}
