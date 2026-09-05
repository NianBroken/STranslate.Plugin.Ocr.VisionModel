using System.Text.Json;
using System.Text.Json.Nodes;

namespace STranslate.Plugin.Ocr.VisionModel;

using STranslate.Plugin;

/// <summary>
/// 构造视觉模型请求，并将用户输入与插件内置协议合并。
/// 内置消息字段由插件统一生成，用户填写的非消息字段保留原始类型并覆盖同名默认值。
/// </summary>
internal static class RequestBuilder
{
    private const string DefaultAccept = "text/event-stream";
    private static readonly HashSet<string> ManagedMessageKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "messages",
        "contents",
        "input"
    };
    private static readonly HashSet<string> ManagedMessageItemKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "role", "content", "parts", "type", "source", "image_url", "inline_data", "input_text", "input_image"
    };
    private static readonly HashSet<string> ManagedHeaderKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "accept", "content-type"
    };

    public static BuiltRequest Build(Settings settings, byte[] imageData, Prompt prompt)
    {
        var mimeType = ImageDataHelper.DetectMimeType(imageData);
        var imageDataUrl = $"data:{mimeType};base64,{Convert.ToBase64String(imageData)}";
        JsonObject? customBody = null;
        if (!string.IsNullOrWhiteSpace(settings.RequestBodyJson))
            customBody = ParseObject(settings.RequestBodyJson, "自定义请求体");
        var promptItems = prompt.Items.Select(item => item.Clone()).ToList();
        var systemPrompt = promptItems.FirstOrDefault(item => string.Equals(item.Role, "system", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty;
        var userPrompt = promptItems.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty;
        var messageShape = DetectMessageShape(customBody);
        var request = CreateDefaultRequest(settings.ModelId, promptItems, systemPrompt, userPrompt, imageDataUrl, messageShape);
        var ignoredPaths = new List<string>();
        var ignoredHeaderKeys = new List<string>();
        var customBodyFields = customBody is null ? new List<string>() : DescribeFields(customBody);
        var customHeaderFields = new List<string>();

        if (customBody is not null)
        {
            CollectManagedMessagePaths(customBody, ignoredPaths);
            MergeCustomFields(request, customBody, ignoredPaths);
        }

        InjectPromptAndImage(request, promptItems, systemPrompt, userPrompt, imageDataUrl, mimeType);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {settings.ApiKey}",
            ["Accept"] = DefaultAccept
        };

        if (!string.IsNullOrWhiteSpace(settings.RequestHeadersJson))
        {
            var customHeaders = ParseObject(settings.RequestHeadersJson, "自定义请求头");
            customHeaderFields = DescribeFields(customHeaders);
            foreach (var property in customHeaders)
            {
                if (string.IsNullOrWhiteSpace(property.Key))
                    throw new FormatException("自定义请求头包含空的请求头名称。");
                if (ManagedHeaderKeys.Contains(property.Key))
                {
                    ignoredHeaderKeys.Add(property.Key);
                    continue;
                }
                headers[property.Key] = ConvertJsonValueToHeader(property.Value);
            }
        }

        return new BuiltRequest(request, headers, request.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), mimeType, ignoredPaths, ignoredHeaderKeys, customBodyFields, customHeaderFields);
    }

    private static JsonObject CreateDefaultRequest(string modelId, IReadOnlyList<PromptItem> promptItems, string systemPrompt, string userPrompt, string imageDataUrl, MessageShape messageShape)
    {
        var messages = new JsonArray();
        var lastUserIndex = -1;
        for (var index = 0; index < promptItems.Count; index++)
        {
            if (string.Equals(promptItems[index].Role, "user", StringComparison.OrdinalIgnoreCase))
                lastUserIndex = index;
        }

        for (var index = 0; index < promptItems.Count; index++)
        {
            var item = promptItems[index];
            if (index == lastUserIndex)
            {
                messages.Add(CreateOpenAiUserMessage(item.Content, imageDataUrl));
            }
            else
            {
                messages.Add(new JsonObject { ["role"] = item.Role, ["content"] = item.Content });
            }
        }

        if (lastUserIndex < 0)
            messages.Add(CreateOpenAiUserMessage(userPrompt, imageDataUrl));

        var request = new JsonObject
        {
            ["model"] = modelId,
            ["stream"] = true
        };
        request[GetMessagePropertyName(messageShape)] = messageShape == MessageShape.Messages
            ? messages
            : new JsonArray();
        return request;
    }

    private static MessageShape DetectMessageShape(JsonObject? customBody)
    {
        if (customBody is null) return MessageShape.Messages;
        if (customBody.Any(property => string.Equals(property.Key, "contents", StringComparison.OrdinalIgnoreCase))) return MessageShape.Contents;
        if (customBody.Any(property => string.Equals(property.Key, "input", StringComparison.OrdinalIgnoreCase))) return MessageShape.Input;
        return MessageShape.Messages;
    }

    private static string GetMessagePropertyName(MessageShape messageShape) => messageShape switch
    {
        MessageShape.Input => "input",
        MessageShape.Contents => "contents",
        _ => "messages"
    };

    /// <summary>
    /// 递归合并用户自定义的非消息字段。消息根节点及其提示词和图片内容由插件统一生成，
    /// 用户重复填写时记录路径并忽略，其他字段保留原始 JSON 类型和嵌套结构。
    /// </summary>
    private static void MergeCustomFields(JsonObject target, JsonObject source, ICollection<string> ignoredPaths, string path = "")
    {
        foreach (var property in source)
        {
            var propertyPath = string.IsNullOrEmpty(path) ? property.Key : $"{path}.{property.Key}";
            if (string.IsNullOrEmpty(path) && ManagedMessageKeys.Contains(property.Key))
            {
                continue;
            }

            var existingName = target.Select(item => item.Key)
                .FirstOrDefault(name => string.Equals(name, property.Key, StringComparison.OrdinalIgnoreCase));

            if (existingName is not null && target[existingName] is JsonObject existingObject && property.Value is JsonObject sourceObject)
            {
                MergeCustomFields(existingObject, sourceObject, ignoredPaths, propertyPath);
                continue;
            }

            if (existingName is not null)
                target[existingName] = property.Value?.DeepClone();
            else
                target[property.Key] = property.Value?.DeepClone();
        }
    }

    /// <summary>
    /// 记录用户请求体中由插件生成的消息节点和消息项字段，便于日志定位冲突字段。
    /// </summary>
    private static void CollectManagedMessagePaths(JsonObject source, ICollection<string> ignoredPaths)
    {
        foreach (var rootProperty in source)
        {
            if (!ManagedMessageKeys.Contains(rootProperty.Key))
                continue;
            var rootPath = rootProperty.Key;
            ignoredPaths.Add(rootPath);
            if (rootProperty.Value is not JsonArray items)
                continue;
            for (var index = 0; index < items.Count; index++)
            {
                if (items[index] is not JsonObject item)
                    continue;
                foreach (var property in item)
                {
                    if (ManagedMessageItemKeys.Contains(property.Key))
                        ignoredPaths.Add($"{rootPath}[{index}].{property.Key}");
                }
            }
        }
    }

    private static void InjectPromptAndImage(JsonObject root, IReadOnlyList<PromptItem> promptItems, string systemPrompt, string userPrompt, string imageDataUrl, string mimeType)
    {
        if (root["messages"] is JsonArray messages)
        {
            var system = messages.OfType<JsonObject>().FirstOrDefault(message => string.Equals(message["role"]?.ToString(), "system", StringComparison.OrdinalIgnoreCase));
            if (system is null)
            {
                messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
            }
            else
            {
                system["content"] = systemPrompt;
            }

            var user = messages.OfType<JsonObject>().LastOrDefault(message => string.Equals(message["role"]?.ToString(), "user", StringComparison.OrdinalIgnoreCase));
            if (user is null)
            {
                messages.Add(CreateOpenAiUserMessage(userPrompt, imageDataUrl));
                return;
            }

            user["content"] = CreateUserContent(user["content"], userPrompt, imageDataUrl, mimeType);
            return;
        }

        if (root["contents"] is JsonArray contents)
        {
            var first = contents.OfType<JsonObject>().FirstOrDefault();
            if (first is null)
            {
                contents.Add(CreateGeminiContent(userPrompt, imageDataUrl, mimeType));
                root["system_instruction"] = new JsonObject
                {
                    ["parts"] = new JsonArray { new JsonObject { ["text"] = systemPrompt } }
                };
                return;
            }

            first["role"] ??= "user";
            var parts = first["parts"] as JsonArray ?? new JsonArray();
            var textPart = parts.OfType<JsonObject>().FirstOrDefault(part => part["text"] is not null);
            if (textPart is null)
                parts.Insert(0, new JsonObject { ["text"] = userPrompt });
            else
                textPart["text"] = userPrompt;

            var imagePart = parts.OfType<JsonObject>().FirstOrDefault(part => part["inline_data"] is JsonObject);
            var inlineData = new JsonObject { ["mime_type"] = mimeType, ["data"] = imageDataUrl[(imageDataUrl.IndexOf(',') + 1)..] };
            if (imagePart is null)
                parts.Add(new JsonObject { ["inline_data"] = inlineData });
            else
                imagePart["inline_data"] = inlineData;
            first["parts"] = parts;

            // Gemini 使用独立的 system_instruction 节点传递系统提示词，确保当前设置始终进入请求。
            root["system_instruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = systemPrompt } }
            };
            return;
        }

        if (root["input"] is JsonArray input)
        {
            // OpenAI Responses 协议使用 input_text 和 input_image，结构与 Chat Completions 不同。
            var system = input.OfType<JsonObject>().FirstOrDefault(item => string.Equals(item["role"]?.ToString(), "system", StringComparison.OrdinalIgnoreCase));
            if (system is null)
                input.Insert(0, new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
            else
                system["content"] = systemPrompt;

            var user = input.OfType<JsonObject>().LastOrDefault(item => string.Equals(item["role"]?.ToString(), "user", StringComparison.OrdinalIgnoreCase));
            if (user is null)
                input.Add(CreateResponsesUserInput(userPrompt, imageDataUrl));
            else
                user["content"] = CreateResponsesUserContent(user["content"], userPrompt, imageDataUrl);
            return;
        }

        root["messages"] = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
            CreateOpenAiUserMessage(userPrompt, imageDataUrl)
        };
    }

    private static JsonObject CreateOpenAiUserMessage(string userPrompt, string imageDataUrl)
    {
        return new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = userPrompt },
                new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = imageDataUrl } }
            }
        };
    }

    private static JsonObject CreateGeminiContent(string userPrompt, string imageDataUrl, string mimeType)
    {
        return new JsonObject
        {
            ["role"] = "user",
            ["parts"] = new JsonArray
            {
                new JsonObject { ["text"] = userPrompt },
                new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = mimeType, ["data"] = imageDataUrl[(imageDataUrl.IndexOf(',') + 1)..] } }
            }
        };
    }

    private static JsonNode CreateUserContent(JsonNode? existing, string userPrompt, string imageDataUrl, string mimeType)
    {
        if (existing is JsonArray existingArray && existingArray.Any(item => item is JsonObject objectItem && string.Equals(objectItem["type"]?.ToString(), "image", StringComparison.OrdinalIgnoreCase)))
        {
            var clone = (JsonArray)existingArray.DeepClone();
            var image = clone.OfType<JsonObject>().FirstOrDefault(item => string.Equals(item["type"]?.ToString(), "image", StringComparison.OrdinalIgnoreCase));
            if (image is not null)
                image["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = mimeType,
                    ["data"] = imageDataUrl[(imageDataUrl.IndexOf(',') + 1)..]
                };
            var text = clone.OfType<JsonObject>().FirstOrDefault(item => string.Equals(item["type"]?.ToString(), "text", StringComparison.OrdinalIgnoreCase));
            if (text is null)
                clone.Insert(0, new JsonObject { ["type"] = "text", ["text"] = userPrompt });
            else
                text["text"] = userPrompt;
            return clone;
        }

        if (existing is JsonArray existingParts && existingParts.Any(item => item is JsonObject objectItem && objectItem["inline_data"] is not null))
        {
            var clone = (JsonArray)existingParts.DeepClone();
            var text = clone.OfType<JsonObject>().FirstOrDefault(item => item["text"] is not null);
            if (text is null)
                clone.Insert(0, new JsonObject { ["text"] = userPrompt });
            else
                text["text"] = userPrompt;
            var image = clone.OfType<JsonObject>().FirstOrDefault(item => item["inline_data"] is JsonObject);
            if (image is not null)
                image["inline_data"] = new JsonObject { ["mime_type"] = mimeType, ["data"] = imageDataUrl[(imageDataUrl.IndexOf(',') + 1)..] };
            return clone;
        }

        return new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = userPrompt },
            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = imageDataUrl } }
        };
    }

    private static JsonObject CreateResponsesUserInput(string userPrompt, string imageDataUrl)
    {
        return new JsonObject
        {
            ["role"] = "user",
            ["content"] = CreateResponsesUserContent(null, userPrompt, imageDataUrl)
        };
    }

    private static JsonArray CreateResponsesUserContent(JsonNode? existing, string userPrompt, string imageDataUrl)
    {
        var content = existing as JsonArray is { } array ? (JsonArray)array.DeepClone() : new JsonArray();
        var text = content.OfType<JsonObject>().FirstOrDefault(item => item["type"]?.ToString() is "input_text" or "text");
        if (text is null)
            content.Insert(0, new JsonObject { ["type"] = "input_text", ["text"] = userPrompt });
        else
        {
            text["type"] = "input_text";
            text["text"] = userPrompt;
        }

        var image = content.OfType<JsonObject>().FirstOrDefault(item => item["type"]?.ToString() is "input_image" or "image_url");
        if (image is null)
            content.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = imageDataUrl });
        else
        {
            image["type"] = "input_image";
            image["image_url"] = imageDataUrl;
        }
        return content;
    }

    private static JsonObject ParseObject(string json, string fieldName)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject
                ?? throw new FormatException($"{fieldName}的根节点必须是 JSON 对象。");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"{fieldName}不是有效的 JSON，位置：{ex.BytePositionInLine}。", ex);
        }
    }

    private static string ConvertJsonValueToHeader(JsonNode? value)
    {
        if (value is null) return string.Empty;
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var stringValue)) return stringValue;
        return value.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>
    /// 枚举用户配置中的全部字段路径和 JSON 类型。该诊断信息只包含字段名和类型，不包含密钥值。
    /// </summary>
    private static List<string> DescribeFields(JsonNode node, string path = "")
    {
        var result = new List<string>();
        if (node is JsonObject objectNode)
        {
            foreach (var property in objectNode)
            {
                var propertyPath = string.IsNullOrEmpty(path) ? property.Key : $"{path}.{property.Key}";
                result.Add($"{propertyPath}:{GetJsonType(property.Value)}");
                if (property.Value is not null)
                    result.AddRange(DescribeFields(property.Value, propertyPath));
            }
        }
        else if (node is JsonArray arrayNode)
        {
            for (var index = 0; index < arrayNode.Count; index++)
            {
                var itemPath = $"{path}[{index}]";
                result.Add($"{itemPath}:{GetJsonType(arrayNode[index])}");
                if (arrayNode[index] is not null)
                    result.AddRange(DescribeFields(arrayNode[index]!, itemPath));
            }
        }
        return result;
    }

    /// <summary>将 JSON 节点映射为稳定的类型名称，保证日志能够区分对象、数组和基本值。</summary>
    private static string GetJsonType(JsonNode? value) => value switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue jsonValue when jsonValue.TryGetValue<string>(out _) => "string",
        JsonValue jsonValue when jsonValue.TryGetValue<bool>(out _) => "boolean",
        JsonValue => "number",
        _ => "unknown"
    };
}

internal sealed record BuiltRequest(
    JsonObject Body,
    Dictionary<string, string> Headers,
    string RawBody,
    string MimeType,
    IReadOnlyList<string> IgnoredCustomPaths,
    IReadOnlyList<string> IgnoredCustomHeaderKeys,
    IReadOnlyList<string> CustomBodyFields,
    IReadOnlyList<string> CustomHeaderFields);

/// <summary>视觉请求中承载消息的常见根级结构。</summary>
internal enum MessageShape
{
    Messages,
    Input,
    Contents
}
