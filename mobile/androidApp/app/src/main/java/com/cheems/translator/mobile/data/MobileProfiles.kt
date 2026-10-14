package com.cheems.translator.mobile.data

import kotlinx.serialization.Serializable
import java.util.UUID

@Serializable
data class TranslationProfile(
    val id: String = UUID.randomUUID().toString(),
    val name: String = "默认 AI 接口",
    /** AI uses OpenAI-compatible Chat Completions; HTTP uses the exact request template below. */
    val type: String = PROFILE_AI,
    val endpoint: String = "https://api.openai.com/v1",
    val model: String = "",
    val encryptedApiKey: String = "",
    val method: String = "POST",
    val authType: String = "Bearer",
    val authHeader: String = "Authorization",
    val authQueryName: String = "api_key",
    val basicUsername: String = "",
    val headersJson: String = "{}",
    val queryJson: String = "{}",
    val bodyTemplate: String = """{"text":"{{text}}","source":"{{sourceLanguage}}","target":"{{targetLanguage}}"}""",
    val contentType: String = "application/json",
    val responseType: String = RESPONSE_JSON,
    val responseJsonPath: String = "translation",
    val sourceLanguage: String = "auto",
    val targetLanguage: String = "en",
    val timeoutSeconds: Int = 30,
)

@Serializable
data class PromptProfile(
    val id: String = UUID.randomUUID().toString(),
    val name: String = "默认翻译提示词",
    val prompt: String = DEFAULT_TRANSLATION_PROMPT,
)

@Serializable
data class SpeechProfile(
    val id: String = UUID.randomUUID().toString(),
    val name: String = "默认语音模型",
    /** AI uses OpenAI-compatible audio/speech; HTTP uses the request template. */
    val type: String = PROFILE_AI,
    val endpoint: String = "",
    val model: String = "",
    val voice: String = "alloy",
    val encryptedApiKey: String = "",
    val method: String = "POST",
    val headersJson: String = "{}",
    val bodyTemplate: String = """{"model":"{{model}}","input":"{{text}}","voice":"{{voice}}","response_format":"mp3"}""",
    val responseType: String = "RawAudio",
    val responseJsonPath: String = "audio",
    val audioFormat: String = "mp3",
    val timeoutSeconds: Int = 90,
)

const val PROFILE_AI = "AI"
const val PROFILE_HTTP = "HTTP"
const val RESPONSE_JSON = "JsonText"
const val RESPONSE_RAW = "RawText"
const val NIUTRANS_ENDPOINT = "https://api.niutrans.com/NiuTransServer/translation"
const val NIUTRANS_BODY_TEMPLATE = """{"from":"{{sourceLanguage}}","to":"{{targetLanguage}}","apikey":"{{apiKey}}","src_text":"{{text}}"}"""
const val NIUTRANS_RESPONSE_PATH = "tgt_text"
const val DEFAULT_TRANSLATION_PROMPT =
    "Translate the source text into natural English. Preserve its meaning, URLs, numbers, names, punctuation, and formatting."
const val DEFAULT_HYMT2_MODEL = "hy-mt2:1.8b-q4km"
