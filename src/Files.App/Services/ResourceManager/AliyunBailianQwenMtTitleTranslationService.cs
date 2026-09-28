// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Files.App.Services.ResourceManager;

public sealed class AliyunBailianQwenMtTitleTranslationService : IBailianQwenMtTitleTranslationService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(45) };
    private static readonly Uri Endpoint = new("https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions");

    public async Task<string> TranslateJapaneseTitleAsync(
        string title,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("标题不能为空。", nameof(title));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("请先配置阿里云百炼 API Key。", nameof(apiKey));

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        using var requestBody = new MemoryStream();
        using (var writer = new Utf8JsonWriter(requestBody))
        {
            writer.WriteStartObject();
            writer.WriteString("model", "qwen-mt-plus");
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", title.Trim());
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteStartObject("translation_options");
            writer.WriteString("source_lang", "Japanese");
            writer.WriteString("target_lang", "Chinese");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        request.Content = new ByteArrayContent(requestBody.ToArray());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "百炼 API Key 无效或无权调用该模型，请检查翻译设置。",
                HttpStatusCode.TooManyRequests => "百炼翻译调用频率或额度已达到限制，请稍后重试或查看百炼控制台。",
                _ => $"百炼翻译请求失败（HTTP {(int)response.StatusCode}）。",
            };
            throw new HttpRequestException(message, null, response.StatusCode);
        }

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (!TryGetTranslation(document.RootElement, out var translatedTitle) || string.IsNullOrWhiteSpace(translatedTitle))
            throw new InvalidDataException("百炼返回的翻译结果为空或格式无法识别。");

        return translatedTitle.Trim();
    }

    private static bool TryGetTranslation(JsonElement root, out string? translatedTitle)
    {
        translatedTitle = null;
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                translatedTitle = content.GetString();
                return true;
            }
        }

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object &&
            output.TryGetProperty("choices", out choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                translatedTitle = content.GetString();
                return true;
            }
        }

        return false;
    }
}
