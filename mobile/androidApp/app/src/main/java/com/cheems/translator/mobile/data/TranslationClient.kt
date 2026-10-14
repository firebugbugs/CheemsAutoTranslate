package com.cheems.translator.mobile.data

import io.ktor.client.HttpClient
import io.ktor.client.engine.okhttp.OkHttp
import io.ktor.client.plugins.HttpTimeout
import io.ktor.client.plugins.expectSuccess
import io.ktor.client.request.header
import io.ktor.client.request.request
import io.ktor.client.request.setBody
import io.ktor.client.request.url
import io.ktor.client.statement.bodyAsText
import io.ktor.http.ContentType
import io.ktor.http.HttpHeaders
import io.ktor.http.HttpMethod
import io.ktor.http.contentType
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

class TranslationClient {
    private val json = Json { ignoreUnknownKeys = true; isLenient = true }
    private val client = HttpClient(OkHttp) {
        expectSuccess = false
        install(HttpTimeout) {
            connectTimeoutMillis = 10_000
            requestTimeoutMillis = 600_000
            socketTimeoutMillis = 600_000
        }
    }

    suspend fun translate(settings: MobileSettings, text: String): String {
        val profile = settings.activeTranslationProfile ?: throw TranslationException("请先添加翻译接口。")
        if (profile.type == PROFILE_HTTP) return translateHttp(settings, profile, text)
        return translateAi(settings, profile, text)
    }

    private suspend fun translateAi(settings: MobileSettings, profile: TranslationProfile, text: String): String {
        if (profile.endpoint.isBlank() || profile.model.isBlank())
            throw TranslationException("当前 AI 接口需要填写 API 地址和模型名称。")
        EndpointValidator.errorFor(profile.endpoint)?.let { throw TranslationException(it) }
        val apiKey = SecretStore.decrypt(profile.encryptedApiKey)
        val endpoint = profile.endpoint.trim().trimEnd('/').let { base ->
            if (base.endsWith("/chat/completions", true)) base else "$base/chat/completions"
        }
        val messages = buildList {
            if (settings.translationPromptEnabled) {
                settings.activePromptProfile?.prompt?.takeIf(String::isNotBlank)?.let {
                    add(ChatMessage("system", it))
                }
            }
            add(ChatMessage("user", text))
        }
        val response = withTimeout(profile.timeoutSeconds.coerceIn(1, 600) * 1_000L) {
            client.request(endpoint) {
                method = HttpMethod.Post
                contentType(ContentType.Application.Json)
                if (apiKey.isNotBlank()) header(HttpHeaders.Authorization, "Bearer $apiKey")
                setBody(ChatCompletionRequest(profile.model.trim(), messages))
            }
        }
        val body = response.bodyAsText()
        if (response.status.value !in 200..299) {
            throw TranslationException("翻译接口返回 HTTP ${response.status.value}：${body.take(260)}")
        }
        val result = runCatching { json.decodeFromString<ChatCompletionResponse>(body) }
            .getOrElse { throw TranslationException("AI 接口响应格式无法识别。") }
        return result.choices.firstOrNull()?.message?.content?.trim()?.takeIf(String::isNotEmpty)
            ?: throw TranslationException("AI 接口没有返回译文。")
    }

    private suspend fun translateHttp(settings: MobileSettings, profile: TranslationProfile, text: String): String {
        validateHttpEndpoint(profile.endpoint)?.let { throw TranslationException(it) }
        if (profile.method.isBlank()) throw TranslationException("HTTP 请求方式不能为空。")
        val apiKey = SecretStore.decrypt(profile.encryptedApiKey)
        val prompt = if (settings.translationPromptEnabled) settings.activePromptProfile?.prompt.orEmpty() else ""
        val values = mapOf(
            "text" to text,
            "textBase64" to android.util.Base64.encodeToString(text.toByteArray(), android.util.Base64.NO_WRAP),
            "model" to profile.model,
            "apiKey" to apiKey,
            "sourceLanguage" to profile.sourceLanguage,
            "targetLanguage" to profile.targetLanguage,
            "prompt" to prompt,
        )
        val headers = readStringMap(profile.headersJson, "请求头")
        val query = readStringMap(profile.queryJson, "查询参数")
        val uri = profile.endpoint.trim().let { endpoint ->
            val expanded = expand(endpoint, values)
            android.net.Uri.parse(expanded).buildUpon().apply {
                query.forEach { (key, value) -> appendQueryParameter(key, expand(value, values)) }
            }.build().toString()
        }
        val keyForQuery = when (profile.authType) {
            "ApiKeyQuery" -> apiKey
            else -> ""
        }
        val requestUrl = if (keyForQuery.isNotBlank()) {
            android.net.Uri.parse(uri).buildUpon().appendQueryParameter(profile.authQueryName.ifBlank { "api_key" }, keyForQuery).build().toString()
        } else uri
        val template = expand(profile.bodyTemplate, values)
        val body = if (profile.contentType.contains("json", true) && template.isNotBlank()) {
            runCatching { expandJson(json.parseToJsonElement(profile.bodyTemplate), values).toString() }
                .getOrElse { throw TranslationException("HTTP 请求体不是有效 JSON 模板：${it.message}") }
        } else template

        val response = withTimeout(profile.timeoutSeconds.coerceIn(1, 600) * 1_000L) {
            client.request {
                url(requestUrl)
                method = runCatching { HttpMethod(profile.method.trim().uppercase()) }
                    .getOrElse { throw TranslationException("HTTP 请求方式无效。") }
                if (profile.authType == "Bearer" && apiKey.isNotBlank()) header(HttpHeaders.Authorization, "Bearer $apiKey")
                if (profile.authType == "ApiKeyHeader" && apiKey.isNotBlank()) header(profile.authHeader.ifBlank { "Authorization" }, apiKey)
                if (profile.authType == "Basic" && apiKey.isNotBlank()) {
                    val token = android.util.Base64.encodeToString(
                        "${profile.basicUsername}:$apiKey".toByteArray(), android.util.Base64.NO_WRAP,
                    )
                    header(HttpHeaders.Authorization, "Basic $token")
                }
                headers.forEach { (name, value) -> header(name, expand(value, values)) }
                if (profile.method.uppercase() != "GET" && body.isNotBlank()) {
                    contentType(runCatching { ContentType.parse(profile.contentType) }.getOrDefault(ContentType.Application.Json))
                    setBody(body)
                }
            }
        }
        val responseText = response.bodyAsText()
        if (response.status.value !in 200..299)
            throw TranslationException("HTTP 翻译接口返回 ${response.status.value}：${responseText.take(260)}")
        if (profile.responseType == RESPONSE_RAW) return responseText.trim().ifBlank { throw TranslationException("HTTP 接口返回空译文。") }
        val root = runCatching { json.parseToJsonElement(responseText) }
            .getOrElse { throw TranslationException("HTTP 接口响应不是有效 JSON。") }
        val value = if (profile.responseJsonPath.isBlank()) findTranslation(root) else getJsonPath(root, profile.responseJsonPath)
        return (value as? JsonPrimitive)?.contentOrNull?.trim()?.takeIf(String::isNotEmpty)
            ?: throw TranslationException("响应字段 ${profile.responseJsonPath} 没有文本译文。")
    }

    private fun validateHttpEndpoint(endpoint: String): String? {
        val uri = runCatching { android.net.Uri.parse(endpoint.trim()) }.getOrNull()
            ?: return "请填写有效的 HTTP 接口地址。"
        if (uri.scheme !in listOf("http", "https") || uri.host.isNullOrBlank())
            return "HTTP 接口地址必须是包含主机名的 HTTP 或 HTTPS URL。"
        val endpointWithoutQuery = uri.buildUpon().clearQuery().fragment(null).build().toString()
        return EndpointValidator.errorFor(endpointWithoutQuery)
    }

    private fun readStringMap(source: String, label: String): Map<String, String> {
        if (source.isBlank()) return emptyMap()
        val obj = runCatching { json.parseToJsonElement(source).jsonObject }
            .getOrElse { throw TranslationException("${label}必须是 JSON 对象。") }
        return obj.mapValues { (key, value) ->
            (value as? JsonPrimitive)?.contentOrNull ?: throw TranslationException("$label“$key”的值必须是文本。")
        }
    }

    private fun expand(template: String, values: Map<String, String>): String = values.entries.fold(template) { text, entry ->
        text.replace("{{${entry.key}}}", entry.value)
    }

    private fun expandJson(element: JsonElement, values: Map<String, String>): JsonElement = when (element) {
        is JsonObject -> buildJsonObject { element.forEach { (key, value) -> put(key, expandJson(value, values)) } }
        is JsonArray -> buildJsonArray { element.forEach { add(expandJson(it, values)) } }
        is JsonPrimitive -> if (element.isString) JsonPrimitive(expand(element.content, values)) else element
        else -> element
    }

    private fun getJsonPath(root: JsonElement, path: String): JsonElement? {
        var current: JsonElement = root
        val segments = path.trim().trimStart('$', '.').replace("[", ".").replace("]", "")
            .split('.').filter(String::isNotBlank)
        for (segment in segments) {
            current = when {
                current is JsonObject -> current[segment] ?: return null
                current is JsonArray -> segment.toIntOrNull()?.let { current.getOrNull(it) } ?: return null
                else -> return null
            }
        }
        return current
    }

    private fun findTranslation(root: JsonElement): JsonPrimitive? {
        val preferred = setOf("translation", "translatedtext", "targettext", "tgttext", "output", "text", "result")
        fun visit(element: JsonElement): JsonPrimitive? {
            if (element is JsonObject) {
                element.forEach { (key, value) ->
                    val normalized = key.replace("_", "").replace("-", "").lowercase()
                    if (normalized in preferred && value is JsonPrimitive && value.isString && value.content.isNotBlank()) return value
                    visit(value)?.let { return it }
                }
            } else if (element is JsonArray) element.forEach { visit(it)?.let { value -> return value } }
            return null
        }
        return visit(root) ?: (root as? JsonPrimitive)?.takeIf { it.isString && it.content.isNotBlank() }
    }

    fun close() = client.close()
}

class TranslationException(message: String) : Exception(message)

@Serializable
private data class ChatCompletionRequest(val model: String, val messages: List<ChatMessage>, val max_tokens: Int = 1200)
@Serializable
private data class ChatMessage(val role: String, val content: String)
@Serializable
private data class ChatCompletionResponse(val choices: List<ChatChoice> = emptyList())
@Serializable
private data class ChatChoice(val message: ChatResponseMessage)
@Serializable
private data class ChatResponseMessage(val content: String? = null)
