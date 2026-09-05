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

        if (customBody is not null)
            MergeCustomFields(request, customBody, ignoredPaths);

        InjectPromptAndImage(request, promptItems, systemPrompt, userPrompt, imageDataUrl, mimeType);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {settings.ApiKey}",
            ["Accept"] = DefaultAccept
        };

        if (!string.IsNullOrWhiteSpace(settings.RequestHeadersJson))
        {
            var customHeaders = ParseObject(settings.RequestHeadersJson, "自定义请求头");
            foreach (var property in customHeaders)
                headers[property.Key] = ConvertJsonValueToHeader(property.Value);
        }

        return new BuiltRequest(request, headers, request.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), mimeType, ignoredPaths);
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
            if (ManagedMessageKeys.Contains(property.Key))
            {
                ignoredPaths.Add(propertyPath);
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
}

internal sealed record BuiltRequest(
    JsonObject Body,
    Dictionary<string, string> Headers,
    string RawBody,
    string MimeType,
    IReadOnlyList<string> IgnoredCustomPaths);

/// <summary>视觉请求中承载消息的常见根级结构。</summary>
internal enum MessageShape
{
    Messages,
    Input,
    Contents
}
