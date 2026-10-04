using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Media;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CursorTranslator.Models;
using EdgeTTS.DotNet;
using EdgeTTS.DotNet.Models;
using NAudio.Wave;

namespace CursorTranslator.Services;

public sealed class SpeechSynthesisService : IDisposable
{
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly object _playbackLock = new();
    private SoundPlayer? _player;
    private MemoryStream? _audioStream;
    private System.Windows.Media.MediaPlayer? _mediaPlayer;
    private WasapiPlayer? _streamingMp3Player;
    private MediaFoundationReader? _streamingMp3Reader;
    private string? _temporaryAudioPath;
    private long _generation;
    private bool _disposed;

    public async Task SpeakAsync(AppSettings settings, string text, CancellationToken cancellationToken)
    {
        if (!settings.IsSpeechConfigured)
            throw new InvalidOperationException(settings.SpeechProvider == SpeechProviderKind.GenericHttp
                ? "请先配置通用 HTTP 语音接口档案。"
                : "请先配置语音模型地址、模型名称和音色。");
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("待播报文本不能为空。", nameof(text));
        if (text.Length > 4_096)
            throw new InvalidOperationException("语音服务单次最多合成 4096 个字符。");

        var generation = Interlocked.Increment(ref _generation);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var configuredTimeout = settings.SpeechProvider == SpeechProviderKind.GenericHttp
            ? Math.Clamp(settings.ActiveSpeechHttpProfile!.Request.TimeoutSeconds, 1, 600)
            : 90;
        timeout.CancelAfter(TimeSpan.FromSeconds(configuredTimeout));
        try
        {
            AppLog.Info("Speech synthesis",
                $"Starting request; provider={settings.SpeechProvider}; textCharacters={text.Length}; timeoutSeconds={configuredTimeout}.");

            if (settings.SpeechProvider == SpeechProviderKind.GenericHttp
                && settings.ActiveSpeechHttpProfile!.IsEdgeTts)
            {
                AppLog.Info("Speech synthesis", "Using Microsoft Edge TTS WebSocket transport.");
                var edgeAudio = await SynthesizeEdgeTtsAsync(settings.ActiveSpeechHttpProfile, text, timeout.Token);
                await PlayMp3Async(edgeAudio, generation, timeout.Token);
                return;
            }

            Uri directMp3Uri = null!;
            var useDirectMp3 = settings.SpeechProvider == SpeechProviderKind.GenericHttp
                && TryCreateDirectMp3Uri(settings.ActiveSpeechHttpProfile!, text, out directMp3Uri);
            if (useDirectMp3)
            {
                AppLog.Info("Speech synthesis", "Using direct GET/MP3 playback route.");
                await RequestAndPlayMp3Async(directMp3Uri, generation, timeout.Token);
                return;
            }

            AppLog.Info("Speech synthesis", "Using buffered audio response route.");
            var result = settings.SpeechProvider == SpeechProviderKind.GenericHttp
                ? await SynthesizeWithProfileAsync(settings.ActiveSpeechHttpProfile!, text, timeout.Token)
                : await SynthesizeOpenAiCompatibleAsync(settings, text, timeout.Token);

            AppLog.Info("Speech synthesis",
                $"Audio response ready; format={result.Format}; bytes={result.Bytes.Length}; " +
                $"sampleRate={result.SampleRate}; channels={result.Channels}; bitsPerSample={result.BitsPerSample}.");

            if (result.Format.Equals("Mp3", StringComparison.OrdinalIgnoreCase))
            {
                await PlayMp3Async(result.Bytes, generation, timeout.Token);
                return;
            }

            var audio = result.Format.Equals("Pcm", StringComparison.OrdinalIgnoreCase)
                ? WrapPcmAsWave(result.Bytes, result.SampleRate, result.Channels, result.BitsPerSample)
                : result.Bytes;
            NormalizeStreamingWaveHeader(audio);
            var stream = new MemoryStream(audio, writable: false);
            var player = new SoundPlayer(stream);
            try
            {
                await Task.Run(player.Load, timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();

                lock (_playbackLock)
                {
                    if (_disposed || generation != Interlocked.Read(ref _generation))
                    {
                        player.Dispose();
                        stream.Dispose();
                        return;
                    }

                    StopPlaybackLocked();
                    _audioStream = stream;
                    _player = player;
                    player.Play();
                }
            }
            catch
            {
                player.Dispose();
                stream.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var timeoutException = new TimeoutException("语音请求超过配置的超时时间，已停止本次播报。");
            AppLog.Error("Speech synthesis",
                $"Speech operation timed out; provider={settings.SpeechProvider}; timeoutSeconds={configuredTimeout}.",
                timeoutException);
            throw timeoutException;
        }
    }

    private static async Task<byte[]> SynthesizeEdgeTtsAsync(
        SpeechHttpProfile profile,
        string text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.Voice))
            throw new InvalidOperationException("Edge TTS 音色不能为空，例如 zh-CN-XiaoxiaoNeural。");

        var communicate = new Communicate(text, voice: profile.Voice.Trim());
        await using var audio = new MemoryStream();
        try
        {
            await foreach (var chunk in communicate.StreamAsync().WithCancellation(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (chunk is AudioChunk audioChunk)
                    await audio.WriteAsync(audioChunk.Data, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLog.Error("Speech synthesis", "Microsoft Edge TTS synthesis failed.", exception);
            throw new InvalidOperationException($"Microsoft Edge TTS 合成失败：{exception.Message}", exception);
        }

        if (audio.Length == 0)
            throw new InvalidOperationException("Microsoft Edge TTS 没有返回音频数据，请检查音色 ID 和网络连接。");
        AppLog.Info("Speech synthesis", $"Microsoft Edge TTS audio received; voice={profile.Voice}; audioBytes={audio.Length}.");
        return audio.ToArray();
    }

    private static async Task<AudioResult> SynthesizeOpenAiCompatibleAsync(
        AppSettings settings,
        string text,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(new Uri(settings.SpeechEndpoint.TrimEnd('/') + "/"), "audio/speech");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(settings.SpeechApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.SpeechApiKey.Trim());
        request.Content = JsonContent.Create(new
        {
            model = settings.SpeechModel.Trim(),
            input = text,
            voice = settings.SpeechVoice.Trim(),
            response_format = "wav"
        });

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        EnsureSuccess(response, bytes);
        if (bytes.Length == 0) throw new InvalidOperationException("语音服务返回了空音频。");
        return new AudioResult(bytes, "Wav", 24000, 1, 16);
    }

    private static async Task<AudioResult> SynthesizeWithProfileAsync(
        SpeechHttpProfile profile,
        string text,
        CancellationToken cancellationToken)
    {
        ValidateProfile(profile);
        AppLog.Info("Speech synthesis",
            $"Using HTTP profile; workflow={profile.Workflow.Type}; method={profile.Request.Method}; " +
            $"responseType={profile.Response.Type}; format={profile.Response.Format}.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(profile.Request.TimeoutSeconds, 1, 600)));

        var values = CreateTemplateValues(profile, text, "");
        var response = await SendProfileRequestAsync(profile, profile.Request, values, timeout.Token);
        var responseBytes = response.Bytes;
        var responseUri = response.EffectiveUri;

        if (profile.Workflow.Type.Equals("AsyncTask", StringComparison.OrdinalIgnoreCase))
        {
            using var createDocument = JsonDocument.Parse(responseBytes);
            var taskId = GetJsonPathValue(createDocument.RootElement, profile.Workflow.TaskIdJsonPath);
            if (string.IsNullOrWhiteSpace(taskId))
                throw new InvalidOperationException($"未能从创建任务响应的 {profile.Workflow.TaskIdJsonPath} 读取任务编号。");

            values = CreateTemplateValues(profile, text, taskId);
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(profile.Workflow.PollIntervalSeconds, 1, 60)), timeout.Token);
                response = await SendProfileRequestAsync(profile, profile.Workflow.QueryRequest, values, timeout.Token);
                responseBytes = response.Bytes;
                responseUri = response.EffectiveUri;
                using var queryDocument = JsonDocument.Parse(responseBytes);
                var status = GetJsonPathValue(queryDocument.RootElement, profile.Workflow.StatusJsonPath);
                if (string.Equals(status, profile.Workflow.SuccessStatus, StringComparison.OrdinalIgnoreCase))
                    break;
                if (profile.Workflow.ErrorStatuses.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Any(error => string.Equals(error, status, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"语音任务失败，服务状态：{status}。");
                }
            }
        }

        return await ReadAudioResultAsync(profile.Response, responseBytes, responseUri, timeout.Token);
    }

    private static async Task<ProfileResponseData> SendProfileRequestAsync(
        SpeechHttpProfile profile,
        SpeechHttpRequestProfile requestProfile,
        Dictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        var response = await HttpProfileRequestSender.SendAsync(
            requestProfile,
            profile.Auth,
            profile.ApiKey,
            profile.ApiSecret,
            values,
            cancellationToken,
            "语音");
        var bytes = response.Bytes;
        if (bytes.Length == 0) throw new InvalidOperationException("语音接口返回了空响应。");
        return new ProfileResponseData(bytes, response.EffectiveUri);
    }

    private static async Task<AudioResult> ReadAudioResultAsync(
        SpeechHttpResponseProfile response,
        byte[] responseBytes,
        Uri responseUri,
        CancellationToken cancellationToken)
    {
        var format = response.Format.Trim();
        byte[] audio;
        var type = response.Type.Trim();
        AppLog.Info("Speech synthesis",
            $"Parsing response; responseType={type}; format={format}; responseBytes={responseBytes.Length}.");
        if (type.Equals("RawAudio", StringComparison.OrdinalIgnoreCase))
        {
            if (LooksLikeHtmlAudioPage(responseBytes))
            {
                AppLog.Info("Speech synthesis", "Raw audio response is an HTML audio page; resolving its source URL.");
                audio = await DownloadHtmlAudioSourceAsync(responseBytes, responseUri, cancellationToken);
            }
            else
            {
                audio = responseBytes;
            }
        }
        else
        {
            using var document = JsonDocument.Parse(responseBytes);
            var value = string.IsNullOrWhiteSpace(response.JsonPath)
                ? FindAudioJsonValue(document.RootElement)
                : GetJsonPathElement(document.RootElement, response.JsonPath);
            if (value is null || value.Value.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(response.JsonPath)
                    ? "JSON 响应中没有识别到音频字符串字段。请在接口档案中填写音频字段路径，例如 data.audio_url。"
                    : $"响应字段 {response.JsonPath} 不存在或不是字符串。");
            var text = value.Value.GetString() ?? "";

            if (type.Equals("JsonUrl", StringComparison.OrdinalIgnoreCase))
            {
                audio = await DownloadAudioAsync(text, cancellationToken);
            }
            else if (type.Equals("JsonBase64", StringComparison.OrdinalIgnoreCase))
            {
                audio = DecodeBase64(text);
            }
            else if (type.Equals("JsonBase64OrUrl", StringComparison.OrdinalIgnoreCase))
            {
                audio = await DecodeBase64OrUrlAsync(text, cancellationToken);
            }
            else
            {
                throw new InvalidOperationException($"不支持音频响应类型：{type}。");
            }
        }

        if (audio.Length == 0) throw new InvalidOperationException("语音接口返回了空音频。");
        return new AudioResult(audio, format, response.SampleRate, response.Channels, response.BitsPerSample);
    }

    private static bool LooksLikeHtmlAudioPage(byte[] bytes)
    {
        var length = Math.Min(bytes.Length, 4096);
        var prefix = Encoding.UTF8.GetString(bytes, 0, length).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (!prefix.StartsWith('<')) return false;

        return prefix.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || prefix.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || prefix.StartsWith("<audio", StringComparison.OrdinalIgnoreCase)
            || prefix.StartsWith("<source", StringComparison.OrdinalIgnoreCase)
            || prefix.Contains("<audio", StringComparison.OrdinalIgnoreCase)
            || prefix.Contains("<source", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> DownloadHtmlAudioSourceAsync(
        byte[] htmlBytes,
        Uri responseUri,
        CancellationToken cancellationToken)
    {
        var audioUri = GetHtmlAudioSourceUri(htmlBytes, responseUri);
        AppLog.Info("Speech synthesis",
            $"Resolved audio source; endpoint={HttpProfileRequestSender.GetSafeEndpoint(audioUri)}; " +
            $"refererOrigin={HttpProfileRequestSender.GetSafeEndpoint(responseUri)}.");
        var audio = await DownloadAudioAsync(audioUri.AbsoluteUri, cancellationToken, responseUri);
        if (IsJsonResponse(audio, null))
            throw new InvalidOperationException("音频源站返回了 JSON 错误响应，而不是 MP3 音频。");
        if (!LooksLikeMp3(audio))
            throw new InvalidOperationException("音频源站返回的数据不是有效的 MP3 音频。");
        AppLog.Info("Speech synthesis", $"Validated downloaded MP3 audio; audioBytes={audio.Length}.");
        return audio;
    }

    private static Uri GetHtmlAudioSourceUri(byte[] htmlBytes, Uri responseUri)
    {
        var html = Encoding.UTF8.GetString(htmlBytes);
        var match = Regex.Match(html,
            "<(?:source|audio)\\b[^>]*\\bsrc\\s*=\\s*(?:\"(?<url>[^\"]*)\"|'(?<url>[^']*)'|(?<url>[^\\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new InvalidOperationException("语音接口返回了 HTML 播放页，但没有找到音频地址。请确认接口返回了带 audio/source 地址的播放页，或调整响应类型。");

        var source = WebUtility.HtmlDecode(match.Groups["url"].Value).Trim();
        if (!Uri.TryCreate(responseUri, source, out var audioUri)
            || (audioUri.Scheme != Uri.UriSchemeHttp && audioUri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("语音接口返回了 HTML 播放页，但其中的音频地址不是有效的 HTTP 或 HTTPS 地址。");

        return audioUri;
    }

    private static async Task<byte[]> DownloadAudioAsync(
        string url,
        CancellationToken cancellationToken,
        Uri? referer = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("音频 URL 必须是有效的 HTTP 或 HTTPS 地址。");

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return (await GetSpeechHttpResponseAsync(
                    uri, cancellationToken, "Generated audio download", referer)).Bytes;
            }
            catch (HttpRequestException exception) when (attempt == 1
                && !cancellationToken.IsCancellationRequested
                && IsConnectionReset(exception))
            {
                AppLog.Warning("Speech HTTP",
                    $"Audio source connection was reset; retrying once; endpoint={HttpProfileRequestSender.GetSafeEndpoint(uri)}.",
                    exception);
                await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken);
            }
            catch (HttpRequestException exception) when (IsConnectionReset(exception))
            {
                throw new InvalidOperationException(
                    "下载语音文件失败：音频源站主动断开了连接。请检查当前网络、代理或防火墙是否能访问该音频服务。", exception);
            }
        }
    }

    private static bool IsConnectionReset(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socketException
                && socketException.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
                return true;
        }

        return false;
    }

    private static async Task<ProfileResponseData> GetSpeechHttpResponseAsync(
        Uri uri,
        CancellationToken cancellationToken,
        string operation,
        Uri? referer = null)
    {
        var endpoint = HttpProfileRequestSender.GetSafeEndpoint(uri);
        var stopwatch = Stopwatch.StartNew();
        AppLog.Info("Speech HTTP", $"Starting {operation}; method=GET; endpoint={endpoint}.");

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (referer is not null)
            {
                // The provider returns a browser-style audio page whose CDN may apply hotlink checks.
                // Send only the page origin, never its query string (which may contain text or credentials).
                request.Headers.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                    "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                request.Headers.Referrer = new Uri(referer.GetLeftPart(UriPartial.Authority) + "/");
            }
            response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.Error("Speech HTTP",
                $"{operation} failed while sending; endpoint={endpoint}; elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F0}.",
                exception);
            throw;
        }

        using (response)
        {
            var effectiveUri = response.RequestMessage?.RequestUri ?? uri;
            var effectiveEndpoint = HttpProfileRequestSender.GetSafeEndpoint(effectiveUri);
            AppLog.Info("Speech HTTP",
                $"{operation} response headers received; endpoint={effectiveEndpoint}; status={(int)response.StatusCode}; " +
                $"httpVersion={response.Version}; contentType={response.Content.Headers.ContentType?.MediaType ?? "(none)"}; " +
                $"contentLength={response.Content.Headers.ContentLength?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; " +
                $"elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F0}.");

            byte[] bytes;
            try
            {
                bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AppLog.Error("Speech HTTP",
                    $"{operation} failed while reading the response body; endpoint={effectiveEndpoint}; " +
                    $"status={(int)response.StatusCode}; elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F0}.",
                    exception);
                throw;
            }

            try
            {
                EnsureSuccess(response, bytes);
            }
            catch (HttpRequestException exception)
            {
                AppLog.Warning("Speech HTTP",
                    $"{operation} returned an unsuccessful HTTP status; endpoint={effectiveEndpoint}; " +
                    $"status={(int)response.StatusCode}; responseBytes={bytes.Length}; " +
                    $"elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F0}.", exception);
                throw;
            }

            AppLog.Info("Speech HTTP",
                $"{operation} completed; endpoint={effectiveEndpoint}; responseBytes={bytes.Length}; " +
                $"elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F0}.");
            return new ProfileResponseData(bytes, effectiveUri, response.Content.Headers.ContentType?.MediaType);
        }
    }

    private static async Task<byte[]> DecodeBase64OrUrlAsync(string value, CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var directUri)
            && (directUri.Scheme == Uri.UriSchemeHttp || directUri.Scheme == Uri.UriSchemeHttps))
            return await DownloadAudioAsync(value, cancellationToken);

        var bytes = DecodeBase64(value);
        var decodedText = Encoding.UTF8.GetString(bytes);
        if (Uri.TryCreate(decodedText, UriKind.Absolute, out var decodedUri)
            && (decodedUri.Scheme == Uri.UriSchemeHttp || decodedUri.Scheme == Uri.UriSchemeHttps))
            return await DownloadAudioAsync(decodedText, cancellationToken);
        return bytes;
    }

    private static byte[] DecodeBase64(string value)
    {
        try { return Convert.FromBase64String(value); }
        catch (FormatException ex) { throw new InvalidOperationException("音频响应字段不是有效的 Base64。", ex); }
    }

    private async Task PlayMp3Async(byte[] audio, long generation, CancellationToken cancellationToken)
    {
        AppLog.Info("Speech playback", $"Preparing local MP3 playback; audioBytes={audio.Length}.");
        var path = Path.Combine(Path.GetTempPath(), $"cheems-tts-{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(path, audio, cancellationToken);
        var player = new System.Windows.Media.MediaPlayer();
        await OpenAndPlayMp3Async(player, new Uri(path), path, generation, cancellationToken);
    }

    private async Task RequestAndPlayMp3Async(Uri requestUri, long generation, CancellationToken cancellationToken)
    {
        var response = await GetSpeechHttpResponseAsync(requestUri, cancellationToken, "Speech synthesis endpoint request");
        var responseBytes = response.Bytes;
        if (responseBytes.Length == 0)
            throw new InvalidOperationException("语音服务返回了空音频。");

        var effectiveUri = response.EffectiveUri;
        if (LooksLikeHtmlAudioPage(responseBytes))
        {
            AppLog.Info("Speech playback",
                "The synthesis endpoint returned an HTML player page; downloading its audio source with page-origin headers.");
            var audio = await DownloadHtmlAudioSourceAsync(responseBytes, effectiveUri, cancellationToken);
            await PlayMp3Async(audio, generation, cancellationToken);
            return;
        }

        var contentType = response.ContentType ?? "(未提供)";
        if (IsJsonResponse(responseBytes, response.ContentType))
        {
            AppLog.Warning("Speech synthesis",
                $"Expected MP3 bytes but received a JSON response; endpoint={HttpProfileRequestSender.GetSafeEndpoint(effectiveUri)}; " +
                $"contentType={contentType}; responseBytes={responseBytes.Length}.");
            throw new InvalidOperationException(
                "语音接口返回了 JSON 响应而不是 MP3 音频。请检查接口参数、API Key、额度或服务端状态。");
        }

        if (!LooksLikeMp3(responseBytes))
        {
            AppLog.Warning("Speech synthesis",
                $"Expected MP3 bytes but response does not have an MP3 signature; endpoint={HttpProfileRequestSender.GetSafeEndpoint(effectiveUri)}; " +
                $"contentType={contentType}; responseBytes={responseBytes.Length}.");
            throw new InvalidOperationException(
                $"语音接口没有返回有效的 MP3 音频（Content-Type: {contentType}）。请检查接口响应格式和服务端状态。");
        }

        // For endpoints that return MP3 bytes directly, keep using the local-file
        // player so we do not issue the synthesis request a second time.
        await PlayMp3Async(responseBytes, generation, cancellationToken);
    }

    private static bool IsJsonResponse(byte[] bytes, string? contentType)
    {
        if (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true) return true;
        var prefix = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 128))
            .TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return prefix.StartsWith('{') || prefix.StartsWith('[');
    }

    private static bool LooksLikeMp3(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == (byte)'I' && bytes[1] == (byte)'D' && bytes[2] == (byte)'3')
            return true;

        // MPEG audio frames start with an 11-bit sync word; ID3 headers are optional.
        return bytes.Length >= 2 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0;
    }

    private async Task PlayMp3UriAsync(Uri audioUri, long generation, CancellationToken cancellationToken)
    {
        AppLog.Info("Speech playback",
            $"Opening generated MP3 stream with Media Foundation; endpoint={HttpProfileRequestSender.GetSafeEndpoint(audioUri)}.");
        var readerTask = Task.Run(() => new MediaFoundationReader(audioUri.AbsoluteUri));
        MediaFoundationReader? reader = null;
        WasapiPlayer? player = null;
        try
        {
            try
            {
                reader = await readerTask.WaitAsync(cancellationToken);
            }
            catch (Exception streamException) when (!cancellationToken.IsCancellationRequested)
            {
                DisposeReaderWhenReady(readerTask);
                AppLog.Warning("Speech playback",
                    $"Media Foundation could not open the generated MP3 stream; retrying by downloading the audio locally; " +
                    $"endpoint={HttpProfileRequestSender.GetSafeEndpoint(audioUri)}.", streamException);

                var audio = await DownloadAudioAsync(audioUri.AbsoluteUri, cancellationToken);
                await PlayMp3Async(audio, generation, cancellationToken);
                return;
            }
            catch
            {
                DisposeReaderWhenReady(readerTask);
                throw;
            }

            cancellationToken.ThrowIfCancellationRequested();
            player = await new WasapiPlayerBuilder().BuildAsync();
            cancellationToken.ThrowIfCancellationRequested();
            player.Init(reader);
            player.PlaybackStopped += (_, args) =>
            {
                if (args.Exception is not null)
                    AppLog.Error("Speech playback", "Network MP3 playback stopped with an error.", args.Exception);
            };

            lock (_playbackLock)
            {
                if (_disposed || generation != Interlocked.Read(ref _generation))
                    return;

                StopPlaybackLocked();
                _streamingMp3Reader = reader;
                _streamingMp3Player = player;
                reader = null;
                player = null;
                _streamingMp3Player.Play();
            }
        }
        finally
        {
            if (player is not null)
                await player.DisposeAsync();
            reader?.Dispose();
        }
    }

    private static void DisposeReaderWhenReady(Task<MediaFoundationReader> readerTask)
        => _ = readerTask.ContinueWith(
            static task =>
            {
                if (task.Status == TaskStatus.RanToCompletion)
                    task.Result.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private async Task OpenAndPlayMp3Async(
        System.Windows.Media.MediaPlayer player,
        Uri audioUri,
        string? temporaryPath,
        long generation,
        CancellationToken cancellationToken)
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler openedHandler = (_, _) => opened.TrySetResult();
        EventHandler<System.Windows.Media.ExceptionEventArgs> failedHandler = (_, args) =>
            opened.TrySetException(args.ErrorException ?? new InvalidOperationException("Windows 无法解码该 MP3 音频。"));
        player.MediaOpened += openedHandler;
        player.MediaFailed += failedHandler;
        try
        {
            player.Open(audioUri);
            await opened.Task.WaitAsync(cancellationToken);
            player.MediaOpened -= openedHandler;
            player.MediaFailed -= failedHandler;

            lock (_playbackLock)
            {
                if (_disposed || generation != Interlocked.Read(ref _generation))
                {
                    player.Close();
                    if (temporaryPath is not null) TryDelete(temporaryPath);
                    return;
                }

                StopPlaybackLocked();
                _mediaPlayer = player;
                _temporaryAudioPath = temporaryPath;
                player.Play();
                AppLog.Info("Speech playback", "Local audio playback started.");
            }
        }
        catch
        {
            player.MediaOpened -= openedHandler;
            player.MediaFailed -= failedHandler;
            player.Close();
            if (temporaryPath is not null) TryDelete(temporaryPath);
            throw;
        }
    }

    private static bool TryCreateDirectMp3Uri(SpeechHttpProfile profile, string text, out Uri uri)
    {
        uri = null!;
        ValidateProfile(profile);
        if (!profile.Workflow.Type.Equals("Sync", StringComparison.OrdinalIgnoreCase)
            || !profile.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            || profile.Request.Body is not null
            || profile.Request.Headers.Count != 0
            || !profile.Response.Type.Equals("RawAudio", StringComparison.OrdinalIgnoreCase)
            || !profile.Response.Format.Equals("Mp3", StringComparison.OrdinalIgnoreCase))
            return false;

        var authType = profile.Auth.Type.Trim();
        if (authType.Equals("ApiKeyQuery", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(profile.ApiKey) || string.IsNullOrWhiteSpace(profile.Auth.QueryName))
                return false;
        }
        else if (!authType.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            // Media Foundation URL playback cannot add custom authentication headers.
            return false;
        }

        var values = CreateTemplateValues(profile, text, "");
        values["dateRfc1123"] = DateTimeOffset.UtcNow.ToString("r", CultureInfo.InvariantCulture);
        values["dateIso8601"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        values["method"] = "GET";
        var url = HttpProfileRequestSender.Expand(profile.Request.Url, values);
        var query = new Dictionary<string, string>(profile.Request.Query, StringComparer.OrdinalIgnoreCase);
        foreach (var item in query.ToArray()) query[item.Key] = HttpProfileRequestSender.Expand(item.Value, values);
        if (authType.Equals("ApiKeyQuery", StringComparison.OrdinalIgnoreCase))
            query[profile.Auth.QueryName] = profile.ApiKey;

        url = AddQuery(url, query);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUri)
            || (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
            return false;

        uri = parsedUri;
        return true;
    }

    private static Dictionary<string, string> CreateTemplateValues(SpeechHttpProfile profile, string text, string taskId)
        => new(StringComparer.Ordinal)
        {
            ["text"] = text,
            ["textBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
            ["model"] = profile.Model,
            ["voice"] = profile.Voice,
            ["apiKey"] = profile.ApiKey,
            ["taskId"] = taskId
        };

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

    private static string Expand(string? template, IReadOnlyDictionary<string, string> values)
    {
        var result = template ?? "";
        foreach (var pair in values)
            result = result.Replace("{{" + pair.Key + "}}", pair.Value, StringComparison.Ordinal);
        return result;
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
    {
        var element = GetJsonPathElement(root, path);
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }

    private static JsonElement? FindAudioJsonValue(JsonElement root)
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
                        "audio" or "audiourl" or "audiodata" or "speech" or "speechurl" => 0,
                        "url" or "downloadurl" or "fileurl" => 1,
                        "data" or "result" => 2,
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

    private static void EnsureSuccess(HttpResponseMessage response, byte[] body)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = Encoding.UTF8.GetString(body);
        if (detail.Length > 500) detail = detail[..500];
        throw new HttpRequestException($"语音服务返回 {(int)response.StatusCode}: {detail}");
    }

    private static void ValidateProfile(SpeechHttpProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Request.Url))
            throw new InvalidOperationException("接口档案中的请求 URL 不能为空。");
        if (string.IsNullOrWhiteSpace(profile.Request.Method))
            throw new InvalidOperationException("接口档案中的 HTTP 方法不能为空。");
        if (!Uri.TryCreate(Expand(profile.Request.Url, new Dictionary<string, string>()), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("接口档案中的 URL 必须是有效的 HTTP 或 HTTPS 地址。");
        if (profile.Request.TimeoutSeconds is < 1 or > 600)
            throw new InvalidOperationException("请求超时必须在 1 到 600 秒之间。");
        if (profile.Request.Headers is null || profile.Request.Query is null)
            throw new InvalidOperationException("请求头和查询参数必须是 JSON 对象。");
        if (!new[] { "None", "Bearer", "ApiKeyHeader", "ApiKeyQuery", "Basic", "HmacSha256" }
                .Contains(profile.Auth.Type, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"不支持鉴权类型：{profile.Auth.Type}。");
        if (!profile.Workflow.Type.Equals("Sync", StringComparison.OrdinalIgnoreCase)
            && !profile.Workflow.Type.Equals("AsyncTask", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"不支持任务流程：{profile.Workflow.Type}。");
        if (profile.Workflow.Type.Equals("AsyncTask", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(profile.Workflow.TaskIdJsonPath)
                || string.IsNullOrWhiteSpace(profile.Workflow.StatusJsonPath)
                || string.IsNullOrWhiteSpace(profile.Workflow.SuccessStatus)
                || string.IsNullOrWhiteSpace(profile.Workflow.QueryRequest.Url)
                || string.IsNullOrWhiteSpace(profile.Workflow.QueryRequest.Method)
                || profile.Workflow.QueryRequest.Headers is null
                || profile.Workflow.QueryRequest.Query is null)
                throw new InvalidOperationException("异步任务档案需要填写任务编号路径、状态路径、成功状态和轮询 URL。");
        }
        var responseType = profile.Response.Type;
        if (!new[] { "RawAudio", "JsonBase64", "JsonUrl", "JsonBase64OrUrl" }
                .Contains(responseType, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"不支持音频响应类型：{responseType}。");
        if (!new[] { "Wav", "Pcm", "Mp3" }.Contains(profile.Response.Format, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"不支持音频格式：{profile.Response.Format}。");
        if (profile.Response.Format.Equals("Pcm", StringComparison.OrdinalIgnoreCase)
            && (profile.Response.SampleRate < 1 || profile.Response.Channels is < 1 or > 8
                || profile.Response.BitsPerSample is not (8 or 16 or 24 or 32)))
            throw new InvalidOperationException("PCM 音频的采样率、声道数或位深无效。");
    }

    private static byte[] WrapPcmAsWave(byte[] pcm, int sampleRate, int channels, int bitsPerSample)
    {
        var bytesPerSample = bitsPerSample / 8;
        var blockAlign = checked((short)(channels * bytesPerSample));
        var byteRate = checked(sampleRate * blockAlign);
        var wave = new byte[checked(44 + pcm.Length)];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wave, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4, 4), (uint)(wave.Length - 8));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wave, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22, 2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24, 4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(28, 4), (uint)byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32, 2), (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34, 2), (ushort)bitsPerSample);
        Encoding.ASCII.GetBytes("data").CopyTo(wave, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40, 4), (uint)pcm.Length);
        pcm.CopyTo(wave, 44);
        return wave;
    }

    private static void NormalizeStreamingWaveHeader(byte[] audio)
    {
        if (audio.Length < 12 || Encoding.ASCII.GetString(audio, 0, 4) != "RIFF")
            return;

        BinaryPrimitives.WriteUInt32LittleEndian(audio.AsSpan(4, 4), (uint)(audio.Length - 8));
        var offset = 12;
        while (offset <= audio.Length - 8)
        {
            var chunkId = Encoding.ASCII.GetString(audio, offset, 4);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(audio.AsSpan(offset + 4, 4));
            var chunkDataOffset = offset + 8;
            var remainingBytes = audio.Length - chunkDataOffset;

            if (chunkId == "data")
            {
                if (chunkSize == uint.MaxValue || chunkSize > remainingBytes)
                    BinaryPrimitives.WriteUInt32LittleEndian(audio.AsSpan(offset + 4, 4), (uint)remainingBytes);
                return;
            }

            if (chunkSize > remainingBytes)
                return;

            offset = chunkDataOffset + (int)chunkSize + (int)(chunkSize & 1);
        }
    }

    public void Stop()
    {
        Interlocked.Increment(ref _generation);
        lock (_playbackLock)
            StopPlaybackLocked();
    }

    private void StopPlaybackLocked()
    {
        try
        {
            _player?.Stop();
            _player?.Dispose();
            _mediaPlayer?.Stop();
            _mediaPlayer?.Close();
        }
        catch (Exception)
        {
            // Playback may already have stopped or the audio device may be unavailable.
        }
        _player = null;
        _audioStream?.Dispose();
        _audioStream = null;
        _mediaPlayer = null;
        var streamingPlayer = _streamingMp3Player;
        var streamingReader = _streamingMp3Reader;
        _streamingMp3Player = null;
        _streamingMp3Reader = null;
        if (streamingPlayer is not null)
            _ = DisposeStreamingMp3Async(streamingPlayer, streamingReader);
        else
            streamingReader?.Dispose();
        if (_temporaryAudioPath is not null) TryDelete(_temporaryAudioPath);
        _temporaryAudioPath = null;
    }

    private static async Task DisposeStreamingMp3Async(WasapiPlayer player, MediaFoundationReader? reader)
    {
        try
        {
            await player.DisposeAsync();
        }
        catch (Exception)
        {
            // The audio device may already have stopped or been removed.
        }
        finally
        {
            reader?.Dispose();
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (_playbackLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
    }

    private sealed record AudioResult(byte[] Bytes, string Format, int SampleRate, int Channels, int BitsPerSample);
    private sealed record ProfileResponseData(byte[] Bytes, Uri EffectiveUri, string? ContentType = null);
}
