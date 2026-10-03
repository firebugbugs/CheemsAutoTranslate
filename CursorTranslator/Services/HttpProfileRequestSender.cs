using System.Globalization;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CursorTranslator.Models;

namespace CursorTranslator.Services;

public static class HttpProfileRequestSender
{
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task<HttpProfileResponse> SendAsync(
        SpeechHttpRequestProfile requestProfile,
        SpeechHttpAuthProfile auth,
        string apiKey,
        string apiSecret,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken,
        string serviceName)
    {
        var date = DateTimeOffset.UtcNow.ToString("r", CultureInfo.InvariantCulture);
        var requestValues = values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        requestValues["dateRfc1123"] = date;
        requestValues["dateIso8601"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        requestValues["method"] = requestProfile.Method.Trim().ToUpperInvariant();
        requestValues["apiKey"] = apiKey;
        var url = Expand(requestProfile.Url, requestValues);
        var query = new Dictionary<string, string>(requestProfile.Query, StringComparer.OrdinalIgnoreCase);
        foreach (var item in query.ToArray()) query[item.Key] = Expand(item.Value, requestValues);

        var authType = auth.Type.Trim();
        if (authType.Equals("ApiKeyQuery", StringComparison.OrdinalIgnoreCase))
        {
            EnsureCredential(apiKey, "API Key");
            query[auth.QueryName] = apiKey;
        }
        url = AddQuery(url, query);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("接口档案中的 URL 必须是有效的 HTTP 或 HTTPS 地址。");

        requestValues["host"] = uri.IsDefaultPort ? uri.Host : uri.Authority;
        requestValues["path"] = uri.AbsolutePath;
        requestValues["pathAndQuery"] = uri.PathAndQuery;

        using var request = new HttpRequestMessage(new HttpMethod(requestProfile.Method.Trim()), uri);
        if (requestProfile.Body is not null)
        {
            var body = ExpandNodeValue(requestProfile.Body, requestValues) ?? new JsonObject();
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        if (authType.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            EnsureCredential(apiKey, "API Key");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }
        else if (authType.Equals("ApiKeyHeader", StringComparison.OrdinalIgnoreCase))
        {
            EnsureCredential(apiKey, "API Key");
            SetHeader(request, auth.HeaderName, apiKey);
        }
        else if (authType.Equals("Basic", StringComparison.OrdinalIgnoreCase))
        {
            EnsureCredential(apiKey, "Basic 密码");
            var userPass = $"{Expand(auth.Username, requestValues)}:{apiKey}";
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(userPass)));
        }
        else if (authType.Equals("HmacSha256", StringComparison.OrdinalIgnoreCase))
        {
            EnsureCredential(apiSecret, "API Secret");
            if (!string.IsNullOrWhiteSpace(auth.DateHeader))
                SetHeader(request, auth.DateHeader, date);
            var signatureInput = Expand(auth.SignatureInput, requestValues);
            var signatureBytes = HMACSHA256.HashData(Encoding.UTF8.GetBytes(apiSecret), Encoding.UTF8.GetBytes(signatureInput));
            var signature = auth.SignatureEncoding.Equals("Hex", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToHexString(signatureBytes).ToLowerInvariant()
                : Convert.ToBase64String(signatureBytes);
            requestValues["signature"] = signature;
            SetHeader(request, auth.SignatureHeader, Expand(auth.SignatureTemplate, requestValues));
        }

        foreach (var header in requestProfile.Headers)
            SetHeader(request, header.Key, Expand(header.Value, requestValues));

        var speechRequest = serviceName.Equals("语音", StringComparison.Ordinal);
        var endpoint = GetSafeEndpoint(uri);
        var stopwatch = Stopwatch.StartNew();
        if (speechRequest)
            AppLog.Info("Speech HTTP", $"Sending profile request; method={request.Method}; endpoint={endpoint}; auth={authType}.");

        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (speechRequest)
                AppLog.Error("Speech HTTP",
                    $"Profile request failed while sending; method={request.Method}; endpoint={endpoint}; elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F0}.",
                    exception);
            throw;
        }

        using (response)
        {
            if (speechRequest)
                AppLog.Info("Speech HTTP",
                    $"Profile request received headers; endpoint={GetSafeEndpoint(response.RequestMessage?.RequestUri ?? uri)}; " +
                    $"status={(int)response.StatusCode}; httpVersion={response.Version}; " +
                    $"contentType={response.Content.Headers.ContentType?.MediaType ?? "(none)"}; " +
                    $"contentLength={response.Content.Headers.ContentLength?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; " +
                    $"elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F0}.");

            byte[] bytes;
            try
            {
                bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (speechRequest)
                    AppLog.Error("Speech HTTP",
                        $"Profile response body failed while reading; endpoint={GetSafeEndpoint(response.RequestMessage?.RequestUri ?? uri)}; " +
                        $"status={(int)response.StatusCode}; elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F0}.",
                        exception);
                throw;
            }

            if (!response.IsSuccessStatusCode)
            {
                if (speechRequest)
                    AppLog.Warning("Speech HTTP",
                        $"Profile endpoint returned an unsuccessful status; endpoint={GetSafeEndpoint(response.RequestMessage?.RequestUri ?? uri)}; " +
                        $"status={(int)response.StatusCode}; responseBytes={bytes.Length}; " +
                        $"elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F0}.");

                var detail = Encoding.UTF8.GetString(bytes);
                if (detail.Length > 500) detail = detail[..500];
                throw new HttpRequestException($"{serviceName}服务返回 {(int)response.StatusCode}: {detail}", null, response.StatusCode);
            }

            if (speechRequest)
                AppLog.Info("Speech HTTP",
                    $"Profile response body received; responseBytes={bytes.Length}; elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F0}.");

            return new HttpProfileResponse(bytes, response.RequestMessage?.RequestUri ?? uri);
        }
    }

    public static string GetSafeEndpoint(Uri uri)
    {
        var host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.IdnHost}]" : uri.IdnHost;
        var port = uri.IsDefaultPort ? "" : $":{uri.Port.ToString(CultureInfo.InvariantCulture)}";
        return $"{uri.Scheme}://{host}{port}";
    }

    public static string Expand(string? template, IReadOnlyDictionary<string, string> values)
    {
        var result = template ?? "";
        foreach (var pair in values)
            result = result.Replace("{{" + pair.Key + "}}", pair.Value, StringComparison.Ordinal);
        return result;
    }

    private static string AddQuery(string url, IReadOnlyDictionary<string, string> query)
    {
        if (query.Count == 0) return url;
        var encoded = string.Join("&", query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return url + (url.Contains('?') ? "&" : "?") + encoded;
    }

    private static JsonNode? ExpandNodeValue(JsonNode? node, IReadOnlyDictionary<string, string> values)
    {
        if (node is JsonObject obj)
        {
            var expanded = new JsonObject();
            foreach (var pair in obj)
                expanded[pair.Key] = ExpandNodeValue(pair.Value, values);
            return expanded;
        }
        if (node is JsonArray array)
        {
            var expanded = new JsonArray();
            foreach (var child in array) expanded.Add(ExpandNodeValue(child, values));
            return expanded;
        }
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return JsonValue.Create(Expand(text, values));
        return node?.DeepClone();
    }

    private static void SetHeader(HttpRequestMessage request, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("HTTP 请求头名称不能为空。");
        if (!request.Headers.TryAddWithoutValidation(name, value))
        {
            request.Content ??= new ByteArrayContent([]);
            request.Content.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static void EnsureCredential(string credential, string name)
    {
        if (string.IsNullOrWhiteSpace(credential)) throw new InvalidOperationException($"接口档案需要填写 {name}。");
    }
}

public sealed record HttpProfileResponse(byte[] Bytes, Uri EffectiveUri);
