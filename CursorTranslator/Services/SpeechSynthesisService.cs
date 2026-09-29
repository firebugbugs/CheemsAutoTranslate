using System.Buffers.Binary;
using System.IO;
using System.Media;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using CursorTranslator.Models;

namespace CursorTranslator.Services;

public sealed class SpeechSynthesisService : IDisposable
{
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly object _playbackLock = new();
    private SoundPlayer? _player;
    private MemoryStream? _audioStream;
    private long _generation;
    private bool _disposed;

    public async Task SpeakAsync(AppSettings settings, string text, CancellationToken cancellationToken)
    {
        if (!settings.IsSpeechConfigured)
            throw new InvalidOperationException("请先配置语音模型地址、模型名称和音色。");
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("待播报文本不能为空。", nameof(text));
        if (text.Length > 4_096)
            throw new InvalidOperationException("语音服务单次最多合成 4096 个字符。");

        var generation = Interlocked.Increment(ref _generation);
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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var audio = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var detail = Encoding.UTF8.GetString(audio);
                if (detail.Length > 500) detail = detail[..500];
                throw new HttpRequestException($"语音服务返回 {(int)response.StatusCode}: {detail}");
            }
            if (audio.Length == 0)
                throw new InvalidOperationException("语音服务返回了空音频。");

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
            throw new TimeoutException("语音请求超过 90 秒，已停止本次播报。");
        }
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
        }
        catch (Exception)
        {
            // Playback may already have stopped or the audio device may be unavailable.
        }
        _player = null;
        _audioStream?.Dispose();
        _audioStream = null;
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
}
