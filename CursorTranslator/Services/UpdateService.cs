using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace CursorTranslator.Services;

public sealed class UpdateService
{
    private const string LatestReleaseEndpoint =
        "https://gitee.com/api/v5/repos/unbengable/cheems-auto-translate/releases/latest";
    private const string ReleasesPage = "https://gitee.com/unbengable/cheems-auto-translate/releases";
    private const string InstallerFileName = "CheemsTranslator-update.exe";
    private const string PartialFileName = InstallerFileName + ".part";
    private const string MetadataFileName = "download-state.json";
    private static readonly HttpClient ApiClient = CreateHttpClient(TimeSpan.FromSeconds(30));
    private static readonly HttpClient DownloadClient = CreateHttpClient(Timeout.InfiniteTimeSpan);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _updateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CursorTranslator",
        "Updates");

    private string InstallerPath => Path.Combine(_updateDirectory, InstallerFileName);
    private string PartialPath => Path.Combine(_updateDirectory, PartialFileName);
    private string MetadataPath => Path.Combine(_updateDirectory, MetadataFileName);
    private string InstallLogPath => Path.Combine(_updateDirectory, "update-install.log");

    public static string CurrentVersionLabel
    {
        get
        {
            var informationalVersion = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            return NormalizeVersionLabel(informationalVersion) ?? "0.0.1";
        }
    }

    public async Task<UpdateRelease?> CheckLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await ApiClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var apiMessage = ReadApiMessage(errorBody);
            if (string.Equals(apiMessage, "404 Not Found", StringComparison.OrdinalIgnoreCase))
                return null;

            throw new HttpRequestException(
                $"Gitee 更新接口无法访问此项目：{apiMessage ?? "项目不存在或当前不可访问"}",
                null,
                HttpStatusCode.NotFound);
        }

        response.EnsureSuccessStatusCode();
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tagName = GetString(root, "tag_name");
        if (string.IsNullOrWhiteSpace(tagName)
            || !TryParseVersion(tagName, out var releaseVersion))
            throw new InvalidDataException("Gitee 发行版没有有效的版本标签。");

        var releasePage = GetUri(GetString(root, "html_url"))
            ?? new Uri($"{ReleasesPage}/tag/{Uri.EscapeDataString(tagName)}");
        var asset = FindInstallerAsset(root);
        if (root.TryGetProperty("id", out var releaseIdElement)
            && releaseIdElement.TryGetInt64(out var releaseId)
            && (asset is null || !asset.Size.HasValue))
        {
            var attachedAsset = await FindAttachedInstallerAsync(releaseId, cancellationToken);
            if (asset is null)
            {
                asset = attachedAsset;
            }
            else if (attachedAsset is not null
                && string.Equals(asset.Name, attachedAsset.Name, StringComparison.OrdinalIgnoreCase))
            {
                asset = asset with { Size = attachedAsset.Size ?? asset.Size };
            }
        }
        var currentVersion = TryParseVersion(CurrentVersionLabel, out var parsedCurrent)
            ? parsedCurrent
            : new Version(0, 0, 1);

        return new UpdateRelease(
            tagName,
            releaseVersion,
            GetString(root, "name") ?? tagName,
            releasePage,
            asset?.DownloadUri,
            asset?.Size,
            IsVersionLabelNewer(tagName, CurrentVersionLabel, releaseVersion, currentVersion));
    }

    public DownloadState GetDownloadState(UpdateRelease release)
    {
        var metadata = ReadDownloadMetadata();
        if (metadata is null
            || !string.Equals(metadata.Version, release.TagName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(metadata.DownloadUrl, release.DownloadUri?.AbsoluteUri, StringComparison.Ordinal))
            return new DownloadState(0, release.AssetSize, null);

        var expectedSize = metadata.Size ?? release.AssetSize;
        if (File.Exists(InstallerPath))
        {
            var completedBytes = new FileInfo(InstallerPath).Length;
            if (!expectedSize.HasValue || completedBytes == expectedSize.Value)
                return new DownloadState(completedBytes, expectedSize, InstallerPath);
        }

        var downloadedBytes = File.Exists(PartialPath) ? new FileInfo(PartialPath).Length : 0;
        if (expectedSize.HasValue && downloadedBytes == expectedSize.Value && downloadedBytes > 0)
        {
            File.Move(PartialPath, InstallerPath, overwrite: true);
            return new DownloadState(downloadedBytes, expectedSize, InstallerPath);
        }

        return new DownloadState(downloadedBytes, expectedSize, null);
    }

    public async Task<string> DownloadInstallerAsync(
        UpdateRelease release,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        if (release.DownloadUri is null)
            throw new InvalidOperationException("这个 Gitee 发行版没有可下载的安装程序。");

        Directory.CreateDirectory(_updateDirectory);
        var metadata = ReadDownloadMetadata();
        var metadataMatches = metadata is not null
            && string.Equals(metadata.Version, release.TagName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(metadata.DownloadUrl, release.DownloadUri.AbsoluteUri, StringComparison.Ordinal);

        if (!metadataMatches)
        {
            DeleteIfExists(InstallerPath);
            DeleteIfExists(PartialPath);
        }

        await WriteDownloadMetadataAsync(release, release.AssetSize, cancellationToken);

        var existingBytes = File.Exists(PartialPath) ? new FileInfo(PartialPath).Length : 0;
        if (existingBytes > 0 && release.AssetSize == existingBytes)
        {
            File.Move(PartialPath, InstallerPath, overwrite: true);
            progress.Report(new DownloadProgress(existingBytes, release.AssetSize));
            return InstallerPath;
        }
        if (release.AssetSize.HasValue && existingBytes > release.AssetSize.Value)
        {
            DeleteIfExists(PartialPath);
            existingBytes = 0;
        }

        if (File.Exists(InstallerPath))
        {
            var completedBytes = new FileInfo(InstallerPath).Length;
            if (!release.AssetSize.HasValue || completedBytes == release.AssetSize.Value)
                return InstallerPath;
            DeleteIfExists(InstallerPath);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUri);
        if (existingBytes > 0)
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);

        using var response = await DownloadClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingBytes > 0)
        {
            var serverLength = response.Content.Headers.ContentRange?.Length;
            if (serverLength.HasValue && serverLength.Value == existingBytes)
            {
                await WriteDownloadMetadataAsync(release, serverLength, cancellationToken);
                File.Move(PartialPath, InstallerPath, overwrite: true);
                progress.Report(new DownloadProgress(existingBytes, serverLength));
                return InstallerPath;
            }

            response.Dispose();
            DeleteIfExists(PartialPath);
            return await DownloadFreshAsync(release, progress, cancellationToken);
        }

        response.EnsureSuccessStatusCode();

        var responseLength = response.Content.Headers.ContentRange?.Length
            ?? (response.Content.Headers.ContentLength.HasValue
                ? response.Content.Headers.ContentLength.Value
                    + (response.StatusCode == HttpStatusCode.PartialContent ? existingBytes : 0)
                : null);
        var totalBytes = release.AssetSize ?? responseLength;
        if (totalBytes.HasValue)
            await WriteDownloadMetadataAsync(release, totalBytes, cancellationToken);

        // Gitee's attachment CDN currently ignores Range and returns HTTP 200 with the full file.
        // Keep the existing partial file and skip its already-downloaded prefix in that response.
        // This preserves progress and avoids destroying resumable data, though the CDN still
        // retransmits the prefix because it does not implement byte-range requests.
        var replayPrefixBytes = existingBytes > 0
            && response.StatusCode == HttpStatusCode.OK
            ? existingBytes
            : 0;
        var append = existingBytes > 0
            && response.StatusCode == HttpStatusCode.PartialContent
            && response.Content.Headers.ContentRange?.From == existingBytes;
        if (existingBytes > 0
            && response.StatusCode == HttpStatusCode.PartialContent
            && !append)
            throw new IOException("下载服务器返回了不匹配的分段内容，已保留现有下载进度。请重试。");

        var destinationOffset = append || replayPrefixBytes > 0 ? existingBytes : 0;
        progress.Report(new DownloadProgress(destinationOffset, totalBytes, replayPrefixBytes > 0));

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = new FileStream(
            PartialPath,
            destinationOffset > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[64 * 1024];
            var downloadedBytes = destinationOffset;
            var prefixBytesRemaining = replayPrefixBytes;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;

                var writeOffset = 0;
                if (prefixBytesRemaining > 0)
                {
                    var skipped = (int)Math.Min(prefixBytesRemaining, read);
                    prefixBytesRemaining -= skipped;
                    writeOffset += skipped;
                }

                if (writeOffset < read)
                {
                    var newBytes = read - writeOffset;
                    await destination.WriteAsync(buffer.AsMemory(writeOffset, newBytes), cancellationToken);
                    downloadedBytes += newBytes;
                    progress.Report(new DownloadProgress(downloadedBytes, totalBytes, replayPrefixBytes > 0));
                }
            }
            await destination.FlushAsync(cancellationToken);
        }

        if (replayPrefixBytes > 0 && new FileInfo(PartialPath).Length == existingBytes)
            throw new IOException("下载源没有返回已下载位置之后的数据，已保留现有下载进度。请重试。");

        var finalLength = new FileInfo(PartialPath).Length;
        if (totalBytes.HasValue && finalLength != totalBytes.Value)
            throw new IOException($"安装包下载未完成（{finalLength} / {totalBytes.Value} 字节），可继续下载。");

        cancellationToken.ThrowIfCancellationRequested();
        File.Move(PartialPath, InstallerPath, overwrite: true);
        progress.Report(new DownloadProgress(finalLength, totalBytes ?? finalLength));
        return InstallerPath;
    }

    public void StartInstallerHandoff(string installerPath, int parentProcessId, string? fallbackExePath = null)
    {
        var targetExePath = GetInstalledExecutablePath();
        fallbackExePath ??= Environment.ProcessPath ?? targetExePath;
        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        var script = """
            $ErrorActionPreference = 'Stop'
            function Decode([string]$value) {
              [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($value))
            }
            $installer = Decode '__INSTALLER_B64__'
            $targetExe = Decode '__TARGET_EXE_B64__'
            $fallbackExe = Decode '__FALLBACK_EXE_B64__'
            $partial = Decode '__PARTIAL_B64__'
            $metadata = Decode '__METADATA_B64__'
            $log = Decode '__LOG_B64__'
            function Write-Log([string]$message) {
              Add-Content -LiteralPath $log -Value "$(Get-Date -Format o) $message" -Encoding UTF8
            }
            try {
              try { Wait-Process -Id __PARENT_PID__ -ErrorAction SilentlyContinue } catch {}
              Write-Log 'Starting installer.'
              if (-not (Test-Path -LiteralPath $installer)) { throw 'Installer package is missing.' }
              $setup = Start-Process -FilePath $installer `
                -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS' `
                -WorkingDirectory ([System.IO.Path]::GetDirectoryName($installer)) -Wait -PassThru
              Write-Log "Installer exit code: $($setup.ExitCode)."
              if ($setup.ExitCode -ne 0) { throw "Installer exited with code $($setup.ExitCode)." }
              if (-not (Test-Path -LiteralPath $targetExe)) { throw 'Updated application executable was not found.' }
              Start-Process -FilePath $targetExe -ArgumentList '--show-settings' `
                -WorkingDirectory ([System.IO.Path]::GetDirectoryName($targetExe))
              Write-Log 'Updated application restarted.'
              foreach ($path in @($installer, $partial, $metadata)) {
                Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
              }
              Write-Log 'Successful update package and download state removed.'
            } catch {
              Write-Log "Update failed: $($_.Exception.Message) $($_.InvocationInfo.PositionMessage)"
              if (Test-Path -LiteralPath $fallbackExe) {
                Start-Process -FilePath $fallbackExe -ArgumentList '--show-settings' `
                  -WorkingDirectory ([System.IO.Path]::GetDirectoryName($fallbackExe))
                Write-Log 'Previous application restarted as fallback.'
              }
            }
            """;

        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(
            script
                .Replace("__PARENT_PID__", parentProcessId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__INSTALLER_B64__", EncodeUtf8(installerPath), StringComparison.Ordinal)
                .Replace("__TARGET_EXE_B64__", EncodeUtf8(targetExePath), StringComparison.Ordinal)
                .Replace("__FALLBACK_EXE_B64__", EncodeUtf8(fallbackExePath), StringComparison.Ordinal)
                .Replace("__PARTIAL_B64__", EncodeUtf8(PartialPath), StringComparison.Ordinal)
                .Replace("__METADATA_B64__", EncodeUtf8(MetadataPath), StringComparison.Ordinal)
                .Replace("__LOG_B64__", EncodeUtf8(InstallLogPath), StringComparison.Ordinal)));

        var startInfo = new ProcessStartInfo(powershellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedScript);
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动更新安装进程。");
    }

    public static Uri ReleasesUri => new(ReleasesPage);

    private async Task<string> DownloadFreshAsync(
        UpdateRelease release,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        if (release.DownloadUri is null)
            throw new InvalidOperationException("这个 Gitee 发行版没有可下载的安装程序。");

        using var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUri);
        using var response = await DownloadClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = release.AssetSize ?? response.Content.Headers.ContentLength;
        if (totalBytes.HasValue)
            await WriteDownloadMetadataAsync(release, totalBytes, cancellationToken);
        progress.Report(new DownloadProgress(0, totalBytes));
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = new FileStream(
            PartialPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[64 * 1024];
            long downloadedBytes = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloadedBytes += read;
                progress.Report(new DownloadProgress(downloadedBytes, totalBytes));
            }
            await destination.FlushAsync(cancellationToken);
        }

        var finalLength = new FileInfo(PartialPath).Length;
        if (totalBytes.HasValue && finalLength != totalBytes.Value)
            throw new IOException($"安装包下载未完成（{finalLength} / {totalBytes.Value} 字节），可继续下载。");

        cancellationToken.ThrowIfCancellationRequested();
        File.Move(PartialPath, InstallerPath, overwrite: true);
        progress.Report(new DownloadProgress(finalLength, totalBytes ?? finalLength));
        return InstallerPath;
    }

    private async Task<InstallerAsset?> FindAttachedInstallerAsync(
        long releaseId,
        CancellationToken cancellationToken)
    {
        var endpoint = $"https://gitee.com/api/v5/repos/unbengable/cheems-auto-translate/releases/{releaseId}/attach_files";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await ApiClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        return FindInstallerAsset(document.RootElement);
    }

    private async Task WriteDownloadMetadataAsync(
        UpdateRelease release,
        long? size,
        CancellationToken cancellationToken)
    {
        if (release.DownloadUri is null)
            return;

        Directory.CreateDirectory(_updateDirectory);
        var metadata = new DownloadMetadata(release.TagName, release.DownloadUri.AbsoluteUri, size);
        var temporaryPath = MetadataPath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(metadata, JsonOptions),
            cancellationToken);
        File.Move(temporaryPath, MetadataPath, overwrite: true);
    }

    private static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"CheemsTranslator/{CurrentVersionLabel}");
        return client;
    }

    private static InstallerAsset? FindInstallerAsset(JsonElement release)
    {
        var assets = new List<InstallerAsset>();
        var items = new List<JsonElement>();
        if (release.ValueKind == JsonValueKind.Array)
        {
            items.AddRange(release.EnumerateArray());
        }
        else if (release.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "assets", "attach_files" })
            {
                if (release.TryGetProperty(propertyName, out var array)
                    && array.ValueKind == JsonValueKind.Array)
                    items.AddRange(array.EnumerateArray());
            }
        }

        foreach (var item in items)
        {
            var name = GetString(item, "name");
            var downloadUri = GetUri(GetString(item, "browser_download_url"))
                ?? GetUri(GetString(item, "download_url"));
            if (item.ValueKind == JsonValueKind.String)
            {
                downloadUri = GetUri(item.GetString());
                if (downloadUri is not null)
                    name = Path.GetFileName(downloadUri.LocalPath);
            }

            if (string.IsNullOrWhiteSpace(name)
                || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || downloadUri is null
                || downloadUri.Scheme != Uri.UriSchemeHttps)
                continue;

            long? size = null;
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("size", out var sizeElement)
                && sizeElement.TryGetInt64(out var parsedSize))
                size = parsedSize;

            assets.Add(new InstallerAsset(name, downloadUri, size));
        }

        return assets
            .OrderByDescending(asset => IsPreferredInstallerName(asset.Name))
            .FirstOrDefault();
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static bool IsPreferredInstallerName(string name)
        => name.Contains("安装程序", StringComparison.OrdinalIgnoreCase)
            || name.Contains("installer", StringComparison.OrdinalIgnoreCase)
            || name.Contains("setup", StringComparison.OrdinalIgnoreCase);

    private static string? ReadApiMessage(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return GetString(document.RootElement, "message");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Uri? GetUri(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static bool TryParseVersion(string value, out Version version)
    {
        var normalized = value.Trim().TrimStart('v', 'V');
        var suffixIndex = normalized.IndexOfAny(new[] { '-', '+' });
        if (suffixIndex >= 0)
            normalized = normalized[..suffixIndex];
        if (Version.TryParse(normalized, out var parsed))
        {
            version = parsed;
            return true;
        }

        version = new Version(0, 0);
        return false;
    }

    private static bool IsVersionLabelNewer(string candidate, string current, Version candidateVersion, Version currentVersion)
    {
        var numericComparison = candidateVersion.CompareTo(currentVersion);
        if (numericComparison != 0) return numericComparison > 0;

        static string? GetPrerelease(string label)
        {
            var normalized = label.Trim().TrimStart('v', 'V');
            var metadataIndex = normalized.IndexOf('+');
            if (metadataIndex >= 0) normalized = normalized[..metadataIndex];
            var suffixIndex = normalized.IndexOf('-');
            return suffixIndex < 0 ? null : normalized[(suffixIndex + 1)..];
        }

        var candidatePrerelease = GetPrerelease(candidate);
        var currentPrerelease = GetPrerelease(current);
        if (candidatePrerelease is null) return currentPrerelease is not null;
        if (currentPrerelease is null) return false;

        var candidateParts = candidatePrerelease.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var currentParts = currentPrerelease.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < Math.Min(candidateParts.Length, currentParts.Length); index++)
        {
            var candidateNumeric = ulong.TryParse(candidateParts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var candidateNumber);
            var currentNumeric = ulong.TryParse(currentParts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var currentNumber);
            if (candidateNumeric && currentNumeric)
            {
                var comparison = candidateNumber.CompareTo(currentNumber);
                if (comparison != 0) return comparison > 0;
            }
            else if (candidateNumeric != currentNumeric)
            {
                return !candidateNumeric;
            }
            else
            {
                var comparison = string.Compare(candidateParts[index], currentParts[index], StringComparison.Ordinal);
                if (comparison != 0) return comparison > 0;
            }
        }
        return candidateParts.Length > currentParts.Length;
    }

    private static string? NormalizeVersionLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        var metadataIndex = normalized.IndexOf('+');
        return metadataIndex >= 0 ? normalized[..metadataIndex] : normalized;
    }

    private static string EncodeUtf8(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private DownloadMetadata? ReadDownloadMetadata()
    {
        try
        {
            return File.Exists(MetadataPath)
                ? JsonSerializer.Deserialize<DownloadMetadata>(File.ReadAllText(MetadataPath))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static string GetInstalledExecutablePath()
    {
        const string uninstallKeyName = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F06FC4D-2A51-42E2-9AE8-E63A57EA74EF}_is1";
        using var uninstallKey = Registry.CurrentUser.OpenSubKey(uninstallKeyName);
        var installLocation = uninstallKey?.GetValue("InstallLocation") as string;
        if (!string.IsNullOrWhiteSpace(installLocation))
        {
            var installedExecutable = Path.Combine(installLocation, "CursorTranslator.exe");
            if (File.Exists(installedExecutable))
                return installedExecutable;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Cheems翻译",
            "CursorTranslator.exe");
    }

    private sealed record InstallerAsset(string Name, Uri DownloadUri, long? Size);
    private sealed record DownloadMetadata(string Version, string DownloadUrl, long? Size);
}

public sealed record UpdateRelease(
    string TagName,
    Version Version,
    string Name,
    Uri ReleasePage,
    Uri? DownloadUri,
    long? AssetSize,
    bool IsNewer);

public sealed record DownloadProgress(
    long DownloadedBytes,
    long? TotalBytes,
    bool IsReplayingExistingBytes = false);

public sealed record DownloadState(long DownloadedBytes, long? TotalBytes, string? CompletedInstallerPath)
{
    public bool IsComplete => CompletedInstallerPath is not null;
    public bool HasPartial => DownloadedBytes > 0 && !IsComplete;
}
