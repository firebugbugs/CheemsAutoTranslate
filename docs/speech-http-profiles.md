# HTTP 语音接口档案

“通用 HTTP”语音页使用一个或多个档案描述服务协议。档案编辑器提供基础设置表单和高级 JSON 配置；预设只填入常见请求的起步参数，之后仍可在表单或高级配置中自由调整。API Key 和 API Secret 在编辑器内单独填写，并由 Windows DPAPI 加密保存在当前 Windows 用户配置中。高级配置可通过剪贴板复制和导入 JSON，方便备份和复用。

## 内置预设

- **通用 HTTP · POST JSON / WAV**：POST JSON 请求体包含 `text`，接口直接返回 WAV 音频；默认不加鉴权。
- **通用 HTTP · GET 参数 / JSON 音频**：GET 查询参数包含 `text` 和 `voice`，JSON 音频字段可自动识别；默认设置 API Key 请求头。
- **合我意示例 · GET / MP3**：填入合我意的 TTS 地址和 `type=speech`，需要用户填写自己的 `key`。`voice`、`format`、`speed` 和 `model` 默认留空，让服务使用默认值；基础设置中填写音色或模型后，会把值带入请求。语音合成成功时按直接音频流处理，格式设为 MP3。

预设不会覆盖档案名称或已填写的 API Key / Secret。应用预设会替换请求、鉴权类型和音频响应的基础配置，并将任务流程设回同步请求。

## 档案结构

```json
{
  "name": "同步 WAV 示例",
  "model": "",
  "voice": "",
  "request": {
    "url": "http://127.0.0.1:8000/tts",
    "method": "POST",
    "headers": {},
    "query": {},
    "body": {
      "text": "{{text}}",
      "model": "{{model}}",
      "voice": "{{voice}}"
    },
    "timeoutSeconds": 90
  },
  "auth": {
    "type": "Bearer",
    "headerName": "Authorization",
    "queryName": "api_key",
    "username": "",
    "signatureInput": "",
    "signatureEncoding": "Base64",
    "signatureHeader": "Authorization",
    "signatureTemplate": "{{signature}}",
    "dateHeader": ""
  },
  "workflow": {
    "type": "Sync",
    "taskIdJsonPath": "",
    "statusJsonPath": "",
    "successStatus": "",
    "errorStatuses": "",
    "pollIntervalSeconds": 2,
    "queryRequest": {
      "url": "",
      "method": "POST",
      "headers": {},
      "query": {},
      "body": null,
      "timeoutSeconds": 90
    }
  },
  "response": {
    "type": "RawAudio",
    "jsonPath": "",
    "format": "Wav",
    "sampleRate": 24000,
    "channels": 1,
    "bitsPerSample": 16
  }
}
```

## 占位符

请求 URL、请求头、查询参数、JSON 字符串字段和签名模板可以使用以下值：

| 占位符 | 含义 |
| --- | --- |
| `{{text}}` | 要合成的文本 |
| `{{textBase64}}` | UTF-8 文本的标准 Base64 编码 |
| `{{model}}`、`{{voice}}` | 档案中的模型名和音色 |
| `{{taskId}}` | 异步任务编号 |
| `{{apiKey}}` | 档案密钥 |
| `{{host}}`、`{{path}}`、`{{pathAndQuery}}` | 最终请求地址信息 |
| `{{method}}` | HTTP 方法 |
| `{{dateRfc1123}}`、`{{dateIso8601}}` | 当前 UTC 时间 |
| `{{signature}}` | HMAC-SHA256 签名结果 |

## 鉴权类型

`auth.type` 支持 `None`、`Bearer`、`ApiKeyHeader`、`ApiKeyQuery`、`Basic` 和 `HmacSha256`。自定义普通请求头放在 `request.headers`。

HMAC 模式使用 API Secret 作为 HMAC-SHA256 密钥。`signatureInput` 是待签名模板，`signatureEncoding` 可设为 `Base64` 或 `Hex`，签名结果放入 `signatureHeader`，并按 `signatureTemplate` 格式化。签名模板也能引用 `{{apiKey}}`、`{{dateRfc1123}}` 等占位符。需要发送日期头时设置 `dateHeader`。

## 异步任务

任务创建请求使用 `request`。将 `workflow.type` 设为 `AsyncTask`，再填写创建响应中的 `taskIdJsonPath`、轮询响应中的 `statusJsonPath` 和 `successStatus`。`queryRequest` 描述轮询请求，可以在 URL、请求头、查询参数或 JSON 请求体中使用 `{{taskId}}`。可用逗号分隔 `errorStatuses`；轮询总时长受 `request.timeoutSeconds` 限制。

JSON 路径支持点号和数组索引，例如 `payload.audio.audio`、`data.items[0].audio`。JSON 音频响应的 `jsonPath` 可以留空，运行时会按 `audio`、`audio_url`、`audio_data`、`url`、`data` 等常见字段名递归寻找字符串；如果服务字段结构不同，填写确切路径即可。音频响应类型支持 `RawAudio`、`JsonBase64`、`JsonUrl` 和 `JsonBase64OrUrl`；音频格式支持 `Wav`、`Pcm`、`Mp3`。PCM 会根据采样率、声道和位深封装成 WAV 播放；MP3 由 Windows 媒体解码器播放。

讯飞长文本 TTS 的主要能力可通过档案表达：任务创建和轮询分别配置在 `request` 与 `workflow.queryRequest`，鉴权使用 `HmacSha256`，签名输入可组合 `{{host}}`、`{{dateRfc1123}}`、`{{method}}` 和 `{{path}}`；请求体可将文本放入 `{{textBase64}}`，返回的 `payload.audio.audio` 可用 `JsonBase64OrUrl` 解析。AppID、音色、采样参数等按服务文档填入 JSON。这样签名字段和任务字段都由档案配置，无须为这一服务改程序代码。
