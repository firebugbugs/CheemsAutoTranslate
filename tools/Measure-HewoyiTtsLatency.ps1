param(
    [string]$Text = "你好",
    [ValidateRange(1, 5)]
    [int]$Runs = 3
)

$ErrorActionPreference = 'Stop'

function Get-JsonProperty {
    param($Element, [string]$Name)
    foreach ($property in $Element.EnumerateObject()) {
        if ([string]::Equals($property.Name, $Name, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $property.Value
        }
    }
    throw "设置中缺少字段：$Name"
}

function Expand-ProfileValue {
    param([string]$Value, [hashtable]$Values)
    foreach ($name in $Values.Keys) {
        $Value = $Value.Replace("{{$name}}", [string]$Values[$name])
    }
    return $Value
}

function Get-SanitizedError {
    param([Exception]$Exception)
    $message = $Exception.Message
    $message = [regex]::Replace($message, '([?&][^=&#\s]+)=([^&#\s]*)', '$1=[已隐藏]')
    $message = [regex]::Replace($message, '(?i)(api.?key|token|secret)\s*[=:]\s*[^\s&,;]+', '$1=[已隐藏]')
    return "$($Exception.GetType().Name): $message"
}

$settingsPath = Join-Path $env:LOCALAPPDATA 'CursorTranslator\settings.json'
if (-not (Test-Path -LiteralPath $settingsPath)) {
    throw "找不到应用设置：$settingsPath"
}

$settingsDocument = [System.Text.Json.JsonDocument]::Parse([System.IO.File]::ReadAllText($settingsPath))
$settingsRoot = $settingsDocument.RootElement
$profileId = (Get-JsonProperty $settingsRoot 'activeSpeechHttpProfileId').GetString()
$profile = $null
foreach ($candidate in (Get-JsonProperty $settingsRoot 'speechHttpProfiles').EnumerateArray()) {
    if ((Get-JsonProperty $candidate 'Id').GetString() -eq $profileId) {
        $profile = $candidate
        break
    }
}
if (-not $profile) {
    throw '没有找到当前选中的语音接口档案。'
}

$requestProfile = Get-JsonProperty $profile 'Request'
$responseProfile = Get-JsonProperty $profile 'Response'
$authProfile = Get-JsonProperty $profile 'Auth'
$method = (Get-JsonProperty $requestProfile 'Method').GetString()
$responseType = (Get-JsonProperty $responseProfile 'Type').GetString()
$responseFormat = (Get-JsonProperty $responseProfile 'Format').GetString()
if ($method -ne 'GET' -or $responseType -ne 'RawAudio' -or $responseFormat -ne 'Mp3') {
    throw '当前档案不是 GET + 原始 MP3，脚本不会替换或修改档案。'
}

$secretsProperty = Get-JsonProperty $settingsRoot 'protectedSpeechHttpProfileSecrets'
if ($secretsProperty.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
    throw '当前语音档案没有已保存的 API Key。'
}
$cipher = [Convert]::FromBase64String($secretsProperty.GetString())
$plain = [System.Security.Cryptography.ProtectedData]::Unprotect(
    $cipher,
    $null,
    [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
$credentialsDocument = [System.Text.Json.JsonDocument]::Parse([System.Text.Encoding]::UTF8.GetString($plain))
$credential = Get-JsonProperty $credentialsDocument.RootElement $profileId
$apiKey = (Get-JsonProperty $credential 'ApiKey').GetString()
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    throw '当前语音档案没有已保存的 API Key。'
}

$queryName = (Get-JsonProperty $authProfile 'QueryName').GetString()
$authType = (Get-JsonProperty $authProfile 'Type').GetString()
if ($authType -notin @('ApiKeyQuery', 'None')) {
    throw '该脚本只支持无鉴权或 API Key 查询参数鉴权。'
}

$textBase64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($Text))
$values = @{
    text = $Text
    textBase64 = $textBase64
    model = (Get-JsonProperty $profile 'Model').GetString()
    voice = (Get-JsonProperty $profile 'Voice').GetString()
    apiKey = $apiKey
    taskId = ''
    dateRfc1123 = [DateTimeOffset]::UtcNow.ToString('r', [Globalization.CultureInfo]::InvariantCulture)
    dateIso8601 = [DateTimeOffset]::UtcNow.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
    method = 'GET'
}

$url = Expand-ProfileValue (Get-JsonProperty $requestProfile 'Url').GetString() $values
$queryPairs = [System.Collections.Generic.List[string]]::new()
foreach ($queryProperty in (Get-JsonProperty $requestProfile 'Query').EnumerateObject()) {
    $value = Expand-ProfileValue $queryProperty.Value.GetString() $values
    $queryPairs.Add([Uri]::EscapeDataString($queryProperty.Name) + '=' + [Uri]::EscapeDataString($value))
}
if ($authType -eq 'ApiKeyQuery') {
    $queryPairs.Add([Uri]::EscapeDataString($queryName) + '=' + [Uri]::EscapeDataString($apiKey))
}
$separator = if ($url.Contains('?')) { '&' } else { '?' }
$requestUri = [Uri]($url + $separator + [string]::Join('&', $queryPairs))

$naudioDirectory = Join-Path $PSScriptRoot '..\CursorTranslator\bin\Release\net10.0-windows'
Add-Type -Path (Join-Path $naudioDirectory 'NAudio.Core.dll')
Add-Type -Path (Join-Path $naudioDirectory 'NAudio.Wasapi.dll')

$timeoutSeconds = [Math]::Clamp((Get-JsonProperty $requestProfile 'TimeoutSeconds').GetInt32(), 1, 600)
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds($timeoutSeconds)
$runResults = [System.Collections.Generic.List[object]]::new()

try {
    for ($run = 1; $run -le $Runs; $run++) {
        $totalWatch = [System.Diagnostics.Stopwatch]::StartNew()
        $apiWatch = [System.Diagnostics.Stopwatch]::StartNew()
        $apiResponse = $null
        $audioResponse = $null
        $reader = $null
        try {
            $apiResponse = $client.GetAsync(
                $requestUri,
                [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            $apiHeadersMs = $apiWatch.Elapsed.TotalMilliseconds
            $htmlBytes = $apiResponse.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            if (-not $apiResponse.IsSuccessStatusCode) {
                throw "语音 API 返回 HTTP $([int]$apiResponse.StatusCode)。"
            }
            $apiCompleteMs = $apiWatch.Elapsed.TotalMilliseconds

            $html = [System.Text.Encoding]::UTF8.GetString($htmlBytes)
            $match = [regex]::Match(
                $html,
                '<(?:source|audio)\b[^>]*\bsrc\s*=\s*(?:"(?<url>[^"]*)"|''(?<url>[^'']*)''|(?<url>[^\s>]+))',
                [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor
                [System.Text.RegularExpressions.RegexOptions]::Singleline)
            if (-not $match.Success) {
                throw 'API 响应里没有找到 audio/source 音频地址。'
            }
            $audioSource = [System.Net.WebUtility]::HtmlDecode($match.Groups['url'].Value).Trim()
            $audioUri = [Uri]::new($apiResponse.RequestMessage.RequestUri, $audioSource)
            if ($audioUri.Scheme -notin @('http', 'https')) {
                throw '响应里的音频地址不是 HTTP 或 HTTPS。'
            }

            $audioWatch = [System.Diagnostics.Stopwatch]::StartNew()
            $audioResponse = $client.GetAsync(
                $audioUri,
                [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            $audioHeadersMs = $audioWatch.Elapsed.TotalMilliseconds
            if (-not $audioResponse.IsSuccessStatusCode) {
                throw "音频文件返回 HTTP $([int]$audioResponse.StatusCode)。"
            }
            $audioStream = $audioResponse.Content.ReadAsStream()
            $firstBytes = [byte[]]::new(8192)
            $firstByteCount = $audioStream.Read($firstBytes, 0, $firstBytes.Length)
            $audioFirstBytesMs = $audioWatch.Elapsed.TotalMilliseconds
            $audioContentType = if ($audioResponse.Content.Headers.ContentType) {
                $audioResponse.Content.Headers.ContentType.MediaType
            } else {
                '(missing)'
            }

            $decodeWatch = [System.Diagnostics.Stopwatch]::StartNew()
            $reader = New-Object -TypeName NAudio.Wave.MediaFoundationReader -ArgumentList $audioUri.AbsoluteUri
            $decoderOpenMs = $decodeWatch.Elapsed.TotalMilliseconds
            $pcmBuffer = [byte[]]::new(8192)
            $pcmBytes = $reader.Read($pcmBuffer, 0, $pcmBuffer.Length)
            $firstPcmMs = $decodeWatch.Elapsed.TotalMilliseconds
            $totalWatch.Stop()

            $runResults.Add([pscustomobject]@{
                Run = $run
                ApiHeadersMs = [Math]::Round($apiHeadersMs)
                ApiHtmlCompleteMs = [Math]::Round($apiCompleteMs)
                AudioHeadersMs = [Math]::Round($audioHeadersMs)
                AudioFirstBytesMs = [Math]::Round($audioFirstBytesMs)
                AudioContentType = $audioContentType
                DecoderOpenMs = [Math]::Round($decoderOpenMs)
                FirstPcmReadyMs = [Math]::Round($firstPcmMs)
                PcmBytes = $pcmBytes
                EstimatedFirstSoundMs = [Math]::Round($apiCompleteMs + $firstPcmMs)
            })
        }
        catch {
            $safeError = Get-SanitizedError $_.Exception
            Write-Output "第 $run 次探测失败：$safeError"
        }
        finally {
            if ($reader) { $reader.Dispose() }
            if ($audioResponse) { $audioResponse.Dispose() }
            if ($apiResponse) { $apiResponse.Dispose() }
        }
    }

    if ($runResults.Count -gt 0) {
        $runResults | Format-Table -AutoSize
        $best = $runResults | Sort-Object EstimatedFirstSoundMs | Select-Object -First 1
        Write-Output "最快探测：约 $($best.EstimatedFirstSoundMs) ms 可得到首批可解码音频数据（不含扬声器输出缓冲）。"
    } else {
        throw '所有探测都失败了。'
    }
}
finally {
    $client.Dispose()
    $credentialsDocument.Dispose()
    $settingsDocument.Dispose()
    [Array]::Clear($plain, 0, $plain.Length)
    [Array]::Clear($cipher, 0, $cipher.Length)
    $apiKey = $null
    $requestUri = $null
    $audioUri = $null
}
