using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CursorTranslator.Models;

namespace CursorTranslator.Services;

public sealed class TranslationHttpProfileService
{
    public async Task<string> TranslateAsync(
        TranslationHttpProfile profile,
        string text,
        CancellationToken cancellationToken)
    {
        ValidateProfile(profile);
        var timeoutSeconds = Math.Clamp(profile.Request.TimeoutSeconds, 1, 600);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var values = CreateTemplateValues(profile, text, "");
            var response = await HttpProfileRequestSender.SendAsync(
                profile.Request,
                profile.Auth,
                profile.ApiKey,
                profile.ApiSecret,
                values,
                timeout.Token,
                "翻译");

            if (profile.Workflow.Type.Equals("AsyncTask", StringComparison.OrdinalIgnoreCase))
            {
                using var createDocument = JsonDocument.Parse(response.Bytes);
                var taskId = GetJsonPathValue(createDocument.RootElement, profile.Workflow.TaskIdJsonPath);
                if (string.IsNullOrWhiteSpace(taskId))
                    throw new InvalidOperationException($"未能从创建任务响应的 {profile.Workflow.TaskIdJsonPath} 读取任务编号。");

                values = CreateTemplateValues(profile, text, taskId);
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(profile.Workflow.PollIntervalSeconds, 1, 60)), timeout.Token);
                    response = await HttpProfileRequestSender.SendAsync(
                        profile.Workflow.QueryRequest,
                        profile.Auth,
                        profile.ApiKey,
                        profile.ApiSecret,
                        values,
                        timeout.Token,
                        "翻译");
                    using var queryDocument = JsonDocument.Parse(response.Bytes);
                    var status = GetJsonPathValue(queryDocument.RootElement, profile.Workflow.StatusJsonPath);
                    if (string.Equals(status, profile.Workflow.SuccessStatus, StringComparison.OrdinalIgnoreCase))
                        break;
                    if (profile.Workflow.ErrorStatuses.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Any(error => string.Equals(error, status, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException($"翻译任务失败，服务状态：{status}。");
                }
            }

            return ReadTranslationResult(profile.Response, response.Bytes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"HTTP 翻译请求超过 {timeoutSeconds} 秒，已停止本次请求。");
        }
    }

    private static string ReadTranslationResult(TranslationHttpResponseProfile response, byte[] bytes)
    {
        if (bytes.Length == 0) throw new InvalidOperationException("翻译接口返回了空响应。");
        if (response.Type.Equals("RawText", StringComparison.OrdinalIgnoreCase))
        {
            var raw = Encoding.UTF8.GetString(bytes).Trim('\uFEFF', '\u200B', ' ', '\t', '\r', '\n');
            if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("翻译接口返回了空译文。");
            return raw;
        }

        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (string.IsNullOrWhiteSpace(response.JsonPath))
        {
            var automatic = FindTranslationValue(root);
            if (automatic is { } value) return ConvertJsonValue(value, "翻译接口没有返回译文。");
        }
        else
        {
            var selected = GetJsonPathElement(root, response.JsonPath);
            return ConvertJsonValue(selected, $"响应字段 {response.JsonPath} 不是文本。");
        }

        var errorCode = FindNamedValue(root, "error_code", "errorCode", "code");
        var errorMessage = FindNamedValue(root, "error_msg", "errorMessage", "message", "msg");
        var detail = string.Join("：", new[] { errorCode, errorMessage }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        throw new HttpRequestException(string.IsNullOrWhiteSpace(detail)
            ? "翻译接口没有返回译文。请在档案中设置 JSON 字段路径。"
            : $"翻译接口错误：{detail}");
    }

    private static Dictionary<string, string> CreateTemplateValues(TranslationHttpProfile profile, string text, string taskId)
        => new(StringComparer.Ordinal)
        {
            ["text"] = text,
            ["textBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
            ["sourceLanguage"] = profile.SourceLanguage,
            ["targetLanguage"] = profile.TargetLanguage,
            ["apiKey"] = profile.ApiKey,
            ["taskId"] = taskId
        };

    private static JsonElement? FindTranslationValue(JsonElement root)
    {
        JsonElement? best = null;
        var bestPriority = int.MaxValue;

        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    var name = property.Name.Replace("_", "", StringComparison.Ordinal)
                        .Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
                    var priority = name switch
                    {
                        "translation" or "translatedtext" or "targettext" or "tgttext" or "translated" => 0,
                        "output" or "resulttext" or "text" => 1,
                        "result" or "data" => 2,
                        _ => int.MaxValue
                    };
                    if (priority < bestPriority && property.Value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                    {
                        best = property.Value;
                        bestPriority = priority;
                    }
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) Visit(item);
            }
        }

        Visit(root);
        if (best is not null) return best;
        return root.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(root.GetString())
            ? root
            : null;
    }

    private static string? FindNamedValue(JsonElement root, params string[] names)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                    && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                    return property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : property.Value.GetRawText();
                var nested = FindNamedValue(property.Value, names);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                var nested = FindNamedValue(item, names);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        return null;
    }

    private static JsonElement GetJsonPathElement(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Trim().TrimStart('$', '.').Replace("[", ".", StringComparison.Ordinal)
                     .Replace("]", "", StringComparison.Ordinal).Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && current.ValueKind == JsonValueKind.Array && index >= 0 && index < current.GetArrayLength())
                current = current[index];
            else if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var property))
                current = property;
            else
                throw new InvalidOperationException($"响应中找不到 JSON 路径 {path}。");
        }
        return current;
    }

    private static string GetJsonPathValue(JsonElement root, string path)
        => ConvertJsonValue(GetJsonPathElement(root, path), $"响应字段 {path} 不是文本。");

    private static string ConvertJsonValue(JsonElement element, string errorMessage)
    {
        var value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException(errorMessage)
            : value.Trim();
    }

    private static void ValidateProfile(TranslationHttpProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Request.Url)
            || !Uri.TryCreate(profile.Request.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("接口档案中的 URL 必须是有效的 HTTP 或 HTTPS 地址。");
        if (string.IsNullOrWhiteSpace(profile.Request.Method))
            throw new InvalidOperationException("接口档案中的 HTTP 方法不能为空。");
        if (profile.Request.TimeoutSeconds is < 1 or > 600)
            throw new InvalidOperationException("请求超时必须在 1 到 600 秒之间。");
        if (string.IsNullOrWhiteSpace(profile.SourceLanguage) || string.IsNullOrWhiteSpace(profile.TargetLanguage))
            throw new InvalidOperationException("源语言和目标语言不能为空。");
        if (!new[] { "None", "Bearer", "ApiKeyHeader", "ApiKeyQuery", "Basic", "HmacSha256" }
                .Contains(profile.Auth.Type, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"不支持鉴权类型：{profile.Auth.Type}。");
        if (!profile.Workflow.Type.Equals("Sync", StringComparison.OrdinalIgnoreCase)
            && !profile.Workflow.Type.Equals("AsyncTask", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"不支持任务流程：{profile.Workflow.Type}。");
        if (profile.Workflow.Type.Equals("AsyncTask", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(profile.Workflow.TaskIdJsonPath)
                || string.IsNullOrWhiteSpace(profile.Workflow.StatusJsonPath)
                || string.IsNullOrWhiteSpace(profile.Workflow.SuccessStatus)
                || string.IsNullOrWhiteSpace(profile.Workflow.QueryRequest.Url)))
            throw new InvalidOperationException("异步翻译档案需要填写任务编号、状态路径、成功状态和轮询 URL。");
        if (!new[] { "JsonText", "RawText" }.Contains(profile.Response.Type, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"不支持翻译响应类型：{profile.Response.Type}。");
    }
}
