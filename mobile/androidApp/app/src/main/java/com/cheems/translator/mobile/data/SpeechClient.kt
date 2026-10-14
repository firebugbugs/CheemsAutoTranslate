package com.cheems.translator.mobile.data

import android.content.Context
import android.media.MediaPlayer
import android.net.Uri
import android.util.Base64
import io.ktor.client.HttpClient
import io.ktor.client.engine.okhttp.OkHttp
import io.ktor.client.plugins.HttpTimeout
import io.ktor.client.plugins.expectSuccess
import io.ktor.client.request.header
import io.ktor.client.request.get
import io.ktor.client.request.request
import io.ktor.client.request.setBody
import io.ktor.client.request.url
import io.ktor.client.statement.bodyAsBytes
import io.ktor.http.ContentType
import io.ktor.http.HttpMethod
import io.ktor.http.contentType
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import java.io.File

/** Plays audio from the selected AI-compatible or generic HTTP speech profile. */
class SpeechClient(context: Context) {
    private val appContext = context.applicationContext
    private val json = Json { ignoreUnknownKeys = true; isLenient = true }
    private val client = HttpClient(OkHttp) {
        expectSuccess = false
        install(HttpTimeout) {
            connectTimeoutMillis = 10_000
            requestTimeoutMillis = 600_000
            socketTimeoutMillis = 600_000
        }
    }
    private var player: MediaPlayer? = null
    private var currentFile: File? = null

    suspend fun speak(profile: SpeechProfile, text: String) {
        if (profile.endpoint.isBlank()) throw IllegalArgumentException("请先填写语音接口地址。")
        if (profile.type == PROFILE_AI && profile.model.isBlank()) throw IllegalArgumentException("请先填写语音模型名称。")
        if (profile.voice.isBlank()) throw IllegalArgumentException("请先填写语音音色。")
        val bytes = withTimeout(profile.timeoutSeconds.coerceIn(1, 600) * 1_000L) {
            withContext(Dispatchers.IO) { requestAudio(profile, text) }
        }
        if (bytes.isEmpty()) throw IllegalStateException("语音接口返回了空音频。")
        val file = withContext(Dispatchers.IO) {
            File.createTempFile("cheems-speech-", ".${profile.audioFormat.ifBlank { "mp3" }}", appContext.cacheDir)
                .apply { writeBytes(bytes) }
        }
        withContext(Dispatchers.Main.immediate) { playFile(file) }
    }

    private suspend fun requestAudio(profile: SpeechProfile, text: String): ByteArray {
        val key = SecretStore.decrypt(profile.encryptedApiKey)
        val headers = if (profile.type == PROFILE_HTTP) parseMap(profile.headersJson, "语音请求头") else emptyMap()
        val values = mapOf("text" to text, "model" to profile.model, "voice" to profile.voice, "apiKey" to key)
        val endpoint = if (profile.type == PROFILE_AI) {
            EndpointValidator.errorFor(profile.endpoint)?.let { throw IllegalArgumentException(it) }
            profile.endpoint.trim().trimEnd('/').let { if (it.endsWith("/audio/speech", true)) it else "$it/audio/speech" }
        } else {
            val endpointError = validateHttpEndpoint(profile.endpoint)
            if (endpointError != null) throw IllegalArgumentException(endpointError)
            expand(profile.endpoint, values)
        }
        val template = when {
            profile.type == PROFILE_AI -> """{"model":"{{model}}","input":"{{text}}","voice":"{{voice}}","response_format":"${profile.audioFormat}"}"""
            else -> profile.bodyTemplate
        }
        val body = if (template.isBlank()) "" else runCatching {
            expandJson(json.parseToJsonElement(template), values).toString()
        }.getOrElse { throw IllegalArgumentException("语音请求体必须是有效 JSON 模板：${it.message}") }
        val response = client.request(endpoint) {
            method = runCatching { HttpMethod(profile.method.trim().uppercase()) }.getOrElse { HttpMethod.Post }
            if (profile.type == PROFILE_AI && key.isNotBlank()) header("Authorization", "Bearer $key")
            headers.forEach { (name, value) -> header(name, expand(value, values)) }
            if (method.value !in listOf("GET", "HEAD") && body.isNotBlank()) {
                contentType(ContentType.Application.Json)
                setBody(body)
            }
        }
        val responseBytes = response.bodyAsBytes()
        if (response.status.value !in 200..299)
            throw IllegalStateException("语音接口返回 HTTP ${response.status.value}：${responseBytes.toString(Charsets.UTF_8).take(240)}")
        if (profile.type == PROFILE_AI || profile.responseType == "RawAudio") return responseBytes
        val root = json.parseToJsonElement(responseBytes.toString(Charsets.UTF_8))
        val value = jsonPath(root, profile.responseJsonPath)
        return when (profile.responseType) {
            "JsonBase64" -> Base64.decode((value as? JsonPrimitive)?.contentOrNull ?: error("响应字段不是 Base64 音频文本。"), Base64.DEFAULT)
            "JsonUrl" -> {
                val audioUrl = (value as? JsonPrimitive)?.contentOrNull ?: error("响应字段不是音频 URL。")
                val audioResponse = client.get(audioUrl)
                if (audioResponse.status.value !in 200..299) error("音频下载失败：HTTP ${audioResponse.status.value}")
                audioResponse.bodyAsBytes()
            }
            else -> error("不支持的语音响应类型：${profile.responseType}")
        }
    }

    private fun playFile(file: File) {
        stopPlayback()
        currentFile = file
        player = MediaPlayer().apply {
            setDataSource(file.absolutePath)
            setOnCompletionListener { finishPlayback(this) }
            setOnErrorListener { mediaPlayer, _, _ -> finishPlayback(mediaPlayer); true }
            setOnPreparedListener { mediaPlayer -> if (player === mediaPlayer) mediaPlayer.start() }
            prepareAsync()
        }
    }

    private fun finishPlayback(mediaPlayer: MediaPlayer) {
        runCatching { mediaPlayer.release() }
        if (player === mediaPlayer) player = null
        currentFile?.delete()
        currentFile = null
    }

    fun stopPlayback() {
        player?.let { old -> runCatching { old.stop() }; runCatching { old.release() } }
        player = null
        currentFile?.delete()
        currentFile = null
    }

    fun close() {
        stopPlayback()
        client.close()
    }

    private fun parseMap(text: String, label: String): Map<String, String> {
        if (text.isBlank()) return emptyMap()
        val obj = runCatching { json.parseToJsonElement(text).jsonObject }
            .getOrElse { throw IllegalArgumentException("${label}必须是 JSON 对象。") }
        return obj.mapValues { (name, value) ->
            (value as? JsonPrimitive)?.contentOrNull ?: throw IllegalArgumentException("$label $name 的值必须是文本。")
        }
    }

    private fun validateHttpEndpoint(endpoint: String): String? {
        val uri = runCatching { Uri.parse(endpoint.trim()) }.getOrNull() ?: return "请填写有效的语音接口地址。"
        if (uri.scheme !in listOf("http", "https") || uri.host.isNullOrBlank()) return "语音接口必须是有效的 HTTP 或 HTTPS URL。"
        return EndpointValidator.errorFor(uri.buildUpon().clearQuery().fragment(null).build().toString())
    }

    private fun expand(text: String, values: Map<String, String>): String = values.entries.fold(text) { current, entry ->
        current.replace("{{${entry.key}}}", entry.value)
    }

    private fun expandJson(element: JsonElement, values: Map<String, String>): JsonElement = when (element) {
        is JsonObject -> buildJsonObject { element.forEach { (key, value) -> put(key, expandJson(value, values)) } }
        is JsonArray -> buildJsonArray { element.forEach { add(expandJson(it, values)) } }
        is JsonPrimitive -> if (element.isString) JsonPrimitive(expand(element.content, values)) else element
        else -> element
    }

    private fun jsonPath(root: JsonElement, path: String): JsonElement? {
        var current: JsonElement = root
        for (segment in path.trim().trimStart('$', '.').replace("[", ".").replace("]", "")
            .split('.').filter(String::isNotBlank)) {
            current = when {
                current is JsonObject -> current[segment] ?: return null
                current is JsonArray -> segment.toIntOrNull()?.let { current.getOrNull(it) } ?: return null
                else -> return null
            }
        }
        return current
    }
}
