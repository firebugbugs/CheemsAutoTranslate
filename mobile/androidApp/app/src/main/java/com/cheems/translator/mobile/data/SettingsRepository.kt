package com.cheems.translator.mobile.data

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.core.stringSetPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.catch
import kotlinx.coroutines.flow.map
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.json.Json
import java.io.IOException

private val Context.mobileDataStore by preferencesDataStore(name = "mobile_settings")
private val settingsJson = Json { ignoreUnknownKeys = true; encodeDefaults = true }
private const val DEFAULT_INACTIVITY_DELAY_MS = 2_000
private const val MIN_INACTIVITY_DELAY_MS = 500
private const val MAX_INACTIVITY_DELAY_MS = 60_000

data class MobileSettings(
    val isLoaded: Boolean = false,
    val userConsented: Boolean = false,
    val automaticTranslationEnabled: Boolean = false,
    val targetAppPackages: Set<String> = emptySet(),
    val translationProfiles: List<TranslationProfile> = emptyList(),
    val activeTranslationProfileId: String = "",
    val promptProfiles: List<PromptProfile> = emptyList(),
    val activePromptProfileId: String = "",
    val translationPromptEnabled: Boolean = true,
    val speechProfiles: List<SpeechProfile> = emptyList(),
    val activeSpeechProfileId: String = "",
    val realTimeSpeechEnabled: Boolean = false,
    val translateOnTextChange: Boolean = true,
    val translateOnSentenceEnd: Boolean = false,
    val translateAfterCopy: Boolean = false,
    val translateAfterInactivity: Boolean = false,
    val inactivityDelayMs: Int = DEFAULT_INACTIVITY_DELAY_MS,
) {
    val activeTranslationProfile: TranslationProfile?
        get() = translationProfiles.firstOrNull { it.id == activeTranslationProfileId }
    val activePromptProfile: PromptProfile?
        get() = promptProfiles.firstOrNull { it.id == activePromptProfileId }
    val activeSpeechProfile: SpeechProfile?
        get() = speechProfiles.firstOrNull { it.id == activeSpeechProfileId }
    val isTranslationConfigured: Boolean
        get() = activeTranslationProfile?.let { profile ->
            profile.endpoint.isNotBlank() &&
                (profile.type == PROFILE_HTTP || profile.model.isNotBlank()) &&
                (profile.type == PROFILE_HTTP || profile.endpoint.startsWith("http", true))
        } == true
}

class SettingsRepository(context: Context) {
    private val store: DataStore<Preferences> = context.applicationContext.mobileDataStore

    val settings: Flow<MobileSettings> = store.data
        .catch { error ->
            if (error is IOException) emit(androidx.datastore.preferences.core.emptyPreferences())
            else throw error
        }
        .map { preferences ->
            val translations = readTranslationProfiles(preferences)
            val translationId = preferences[Keys.ActiveTranslationProfileId]
                ?.takeIf { id -> translations.any { it.id == id } }
                ?: translations.first().id
            val prompts = readPromptProfiles(preferences)
            val promptId = preferences[Keys.ActivePromptProfileId]
                ?.takeIf { id -> prompts.any { it.id == id } }
                ?: prompts.first().id
            val speeches = readSpeechProfiles(preferences)
            val speechId = preferences[Keys.ActiveSpeechProfileId]
                ?.takeIf { id -> speeches.any { it.id == id } }
                ?: speeches.first().id
            val hasNewTriggerSettings = preferences.contains(Keys.TranslateOnTextChange)
                || preferences.contains(Keys.TranslateOnSentenceEnd)
                || preferences.contains(Keys.TranslateAfterCopy)
                || preferences.contains(Keys.TranslateAfterInactivity)
                || preferences.contains(Keys.InactivityDelayMs)
            val hasLegacyTriggerSettings = preferences.contains(Keys.PauseDelayMs)
                || preferences.contains(Keys.PunctuationTriggerEnabled)
            val legacyDelay = preferences[Keys.PauseDelayMs]
                ?.coerceIn(MIN_INACTIVITY_DELAY_MS, 3_000)
                ?: if (hasLegacyTriggerSettings) 900 else null

            MobileSettings(
                isLoaded = true,
                userConsented = preferences[Keys.UserConsented] ?: false,
                automaticTranslationEnabled = preferences[Keys.AutomaticTranslationEnabled] ?: false,
                targetAppPackages = preferences[Keys.TargetAppPackages] ?: emptySet(),
                translationProfiles = translations,
                activeTranslationProfileId = translationId,
                promptProfiles = prompts,
                activePromptProfileId = promptId,
                translationPromptEnabled = preferences[Keys.TranslationPromptEnabled] ?: true,
                speechProfiles = speeches,
                activeSpeechProfileId = speechId,
                realTimeSpeechEnabled = preferences[Keys.RealTimeSpeechEnabled] ?: false,
                translateOnTextChange = if (hasNewTriggerSettings) preferences[Keys.TranslateOnTextChange] ?: true else !hasLegacyTriggerSettings,
                translateOnSentenceEnd = if (hasNewTriggerSettings) preferences[Keys.TranslateOnSentenceEnd] ?: false else hasLegacyTriggerSettings && (preferences[Keys.PunctuationTriggerEnabled] ?: true),
                translateAfterCopy = preferences[Keys.TranslateAfterCopy] ?: false,
                translateAfterInactivity = if (hasNewTriggerSettings) preferences[Keys.TranslateAfterInactivity] ?: false else hasLegacyTriggerSettings,
                inactivityDelayMs = (preferences[Keys.InactivityDelayMs] ?: legacyDelay ?: DEFAULT_INACTIVITY_DELAY_MS)
                    .coerceIn(MIN_INACTIVITY_DELAY_MS, MAX_INACTIVITY_DELAY_MS),
            )
        }

    suspend fun saveConsent(enabled: Boolean) {
        store.edit { preferences ->
            preferences[Keys.UserConsented] = enabled
            if (!enabled) preferences[Keys.AutomaticTranslationEnabled] = false
        }
    }

    suspend fun setAutomaticTranslationEnabled(enabled: Boolean) {
        store.edit { preferences -> preferences[Keys.AutomaticTranslationEnabled] = enabled }
    }

    suspend fun setTargetAppPackages(packages: Set<String>) {
        store.edit { preferences -> preferences[Keys.TargetAppPackages] = packages }
    }

    suspend fun saveTranslationProfiles(profiles: List<TranslationProfile>, activeId: String) {
        require(profiles.isNotEmpty()) { "至少保留一个翻译接口。" }
        store.edit { preferences ->
            preferences[Keys.TranslationProfilesJson] = settingsJson.encodeToString(ListSerializer(TranslationProfile.serializer()), profiles)
            preferences[Keys.ActiveTranslationProfileId] = activeId.takeIf { id -> profiles.any { it.id == id } } ?: profiles.first().id
        }
    }

    suspend fun savePromptProfiles(profiles: List<PromptProfile>, activeId: String) {
        require(profiles.isNotEmpty()) { "至少保留一个提示词。" }
        store.edit { preferences ->
            preferences[Keys.PromptProfilesJson] = settingsJson.encodeToString(ListSerializer(PromptProfile.serializer()), profiles)
            preferences[Keys.ActivePromptProfileId] = activeId.takeIf { id -> profiles.any { it.id == id } } ?: profiles.first().id
        }
    }

    suspend fun setTranslationPromptEnabled(enabled: Boolean) {
        store.edit { preferences -> preferences[Keys.TranslationPromptEnabled] = enabled }
    }

    suspend fun saveSpeechProfiles(profiles: List<SpeechProfile>, activeId: String) {
        require(profiles.isNotEmpty()) { "至少保留一个语音模型。" }
        store.edit { preferences ->
            preferences[Keys.SpeechProfilesJson] = settingsJson.encodeToString(ListSerializer(SpeechProfile.serializer()), profiles)
            preferences[Keys.ActiveSpeechProfileId] = activeId.takeIf { id -> profiles.any { it.id == id } } ?: profiles.first().id
        }
    }

    suspend fun setRealTimeSpeechEnabled(enabled: Boolean) {
        store.edit { preferences -> preferences[Keys.RealTimeSpeechEnabled] = enabled }
    }

    suspend fun saveTriggerSettings(
        translateOnTextChange: Boolean,
        translateOnSentenceEnd: Boolean,
        translateAfterCopy: Boolean,
        translateAfterInactivity: Boolean,
        inactivityDelayMs: Int,
    ) {
        var immediate = translateOnTextChange
        var sentenceEnd = translateOnSentenceEnd
        var afterCopy = translateAfterCopy
        var afterInactivity = translateAfterInactivity
        if (immediate) {
            sentenceEnd = false
            afterInactivity = false
        }
        if (!immediate && !sentenceEnd && !afterCopy && !afterInactivity) {
            sentenceEnd = true
        }
        store.edit { preferences ->
            preferences[Keys.TranslateOnTextChange] = immediate
            preferences[Keys.TranslateOnSentenceEnd] = sentenceEnd
            preferences[Keys.TranslateAfterCopy] = afterCopy
            preferences[Keys.TranslateAfterInactivity] = afterInactivity
            preferences[Keys.InactivityDelayMs] = inactivityDelayMs.coerceIn(MIN_INACTIVITY_DELAY_MS, MAX_INACTIVITY_DELAY_MS)
        }
    }

    private fun readTranslationProfiles(preferences: Preferences): List<TranslationProfile> {
        preferences[Keys.TranslationProfilesJson]?.let { encoded ->
            runCatching { settingsJson.decodeFromString(ListSerializer(TranslationProfile.serializer()), encoded) }
                .getOrNull()?.takeIf { it.isNotEmpty() }?.let { return it }
        }
        // Migrate the original single-endpoint settings and its model selector into selectable profiles.
        val endpoint = preferences[Keys.ApiBaseUrl] ?: "https://api.openai.com/v1"
        val selectedModel = preferences[Keys.Model].orEmpty().ifBlank { DEFAULT_HYMT2_MODEL }
        val models = ((preferences[Keys.ModelOptions] ?: emptySet()) + selectedModel)
            .map(String::trim).filter(String::isNotEmpty).distinct()
        val key = preferences[Keys.EncryptedApiKey].orEmpty()
        return models.mapIndexed { index, model ->
            TranslationProfile(
                id = "migrated-ai-$index",
                name = if (models.size == 1) "默认 AI 接口" else model,
                type = PROFILE_AI,
                endpoint = endpoint,
                model = model,
                encryptedApiKey = key,
            )
        }.ifEmpty { listOf(TranslationProfile(id = "migrated-ai-default", endpoint = endpoint, model = selectedModel, encryptedApiKey = key)) }
    }

    private fun readPromptProfiles(preferences: Preferences): List<PromptProfile> {
        preferences[Keys.PromptProfilesJson]?.let { encoded ->
            runCatching { settingsJson.decodeFromString(ListSerializer(PromptProfile.serializer()), encoded) }
                .getOrNull()?.takeIf { it.isNotEmpty() }?.let { return it }
        }
        return listOf(
            PromptProfile(
                id = "migrated-default-prompt",
                name = "默认翻译提示词",
                prompt = preferences[Keys.LegacySystemPrompt]?.takeIf(String::isNotBlank) ?: DEFAULT_TRANSLATION_PROMPT,
            ),
        )
    }

    private fun readSpeechProfiles(preferences: Preferences): List<SpeechProfile> {
        preferences[Keys.SpeechProfilesJson]?.let { encoded ->
            runCatching { settingsJson.decodeFromString(ListSerializer(SpeechProfile.serializer()), encoded) }
                .getOrNull()?.takeIf { it.isNotEmpty() }?.let { return it }
        }
        return listOf(SpeechProfile(id = "default-speech"))
    }

    private object Keys {
        val UserConsented = booleanPreferencesKey("user_consented")
        val AutomaticTranslationEnabled = booleanPreferencesKey("automatic_translation_enabled")
        val TargetAppPackages = stringSetPreferencesKey("target_app_packages")
        val TranslationProfilesJson = stringPreferencesKey("translation_profiles_json")
        val ActiveTranslationProfileId = stringPreferencesKey("active_translation_profile_id")
        val PromptProfilesJson = stringPreferencesKey("prompt_profiles_json")
        val ActivePromptProfileId = stringPreferencesKey("active_prompt_profile_id")
        val TranslationPromptEnabled = booleanPreferencesKey("translation_prompt_enabled")
        val SpeechProfilesJson = stringPreferencesKey("speech_profiles_json")
        val ActiveSpeechProfileId = stringPreferencesKey("active_speech_profile_id")
        val RealTimeSpeechEnabled = booleanPreferencesKey("real_time_speech_enabled")
        val TranslateOnTextChange = booleanPreferencesKey("translate_on_text_change")
        val TranslateOnSentenceEnd = booleanPreferencesKey("translate_on_sentence_end")
        val TranslateAfterCopy = booleanPreferencesKey("translate_after_copy")
        val TranslateAfterInactivity = booleanPreferencesKey("translate_after_inactivity")
        val InactivityDelayMs = intPreferencesKey("inactivity_delay_ms")
        val PauseDelayMs = intPreferencesKey("pause_delay_ms")
        val PunctuationTriggerEnabled = booleanPreferencesKey("punctuation_trigger_enabled")

        // Read-only migration keys from the previous UI.
        val ApiBaseUrl = stringPreferencesKey("api_base_url")
        val Model = stringPreferencesKey("model")
        val ModelOptions = stringSetPreferencesKey("model_options")
        val EncryptedApiKey = stringPreferencesKey("encrypted_api_key")
        val LegacySystemPrompt = stringPreferencesKey("system_prompt")
    }
}
