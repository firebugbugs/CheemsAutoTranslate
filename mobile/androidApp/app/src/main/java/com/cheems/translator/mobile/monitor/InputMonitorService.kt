package com.cheems.translator.mobile.monitor

import android.accessibilityservice.AccessibilityService
import android.content.ClipboardManager
import android.content.Context
import android.content.pm.ApplicationInfo
import android.graphics.Rect
import android.os.Build
import android.os.SystemClock
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import android.view.inputmethod.InputMethodManager
import com.cheems.translator.mobile.data.MobileSettings
import com.cheems.translator.mobile.data.SettingsRepository
import com.cheems.translator.mobile.data.SpeechClient
import com.cheems.translator.mobile.data.TranslationClient
import com.cheems.translator.mobile.data.TranslationException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch

/** Reads only editable controls in apps explicitly selected by the user. */
class InputMonitorService : AccessibilityService() {
    private data class CopySelectionSnapshot(
        val text: String,
        val packageName: String,
        val bounds: Rect,
        val capturedAt: Long,
    )

    private lateinit var repository: SettingsRepository
    private lateinit var overlay: TranslationOverlay
    private lateinit var serviceScope: CoroutineScope
    private lateinit var speechClient: SpeechClient
    private val translationClient = TranslationClient()

    @Volatile
    private var settings = MobileSettings()

    private var settingsJob: Job? = null
    private var debounceJob: Job? = null
    private var requestJob: Job? = null
    private var latestText: String = ""
    private var latestBounds: Rect = Rect()
    private var latestPackage: String? = null
    private var latestInputNodeId: String? = null
    private var currentWindowPackage: String? = null
    private var translatedText: String = ""
    private var requestId: Long = 0
    private var clipboardManager: ClipboardManager? = null
    private var clipboardListenerRegistered = false
    private var lastHandledClipboardText: String? = null
    private var lastHandledClipboardAt = 0L
    private var pendingCopySelection: CopySelectionSnapshot? = null
    private val clipboardListener = ClipboardManager.OnPrimaryClipChangedListener {
        if (::serviceScope.isInitialized) {
            serviceScope.launch(Dispatchers.Main.immediate) { handleClipboardChanged() }
        }
    }

    override fun onServiceConnected() {
        super.onServiceConnected()
        repository = SettingsRepository(applicationContext)
        overlay = TranslationOverlay(this)
        speechClient = SpeechClient(applicationContext)
        clipboardManager = getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager
        serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
        settingsJob = serviceScope.launch {
            repository.settings.collectLatest { updated ->
                settings = updated
                updateClipboardMonitoring(updated.translateAfterCopy)
                if (!isMonitoringAllowed() || currentWindowPackage?.let { it !in updated.targetAppPackages } == true) {
                    cancelPending()
                    overlay.hide()
                    latestText = ""
                    translatedText = ""
                    latestPackage = null
                    latestInputNodeId = null
                }
            }
        }
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        if (event == null || !::overlay.isInitialized || !::serviceScope.isInitialized) return
        val eventPackage = event.packageName?.toString()

        if (event.eventType == AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED) {
            if (eventPackage == null || !isTransientSystemPackage(eventPackage)) currentWindowPackage = eventPackage
            if (eventPackage != null && eventPackage !in settings.targetAppPackages && !isTransientSystemPackage(eventPackage)) {
                cancelPending()
                overlay.hide()
                latestText = ""
                latestPackage = null
                latestInputNodeId = null
                pendingCopySelection = null
                translatedText = ""
                return
            }
        }

        if (handleCopyButtonClick(event)) return
        if (!isMonitoringAllowed()) return
        val packageName = eventPackage ?: return
        if (packageName !in settings.targetAppPackages) return
        if (event.eventType == AccessibilityEvent.TYPE_VIEW_TEXT_SELECTION_CHANGED) {
            rememberCopySelection(event, packageName)
            return
        }
        if (event.eventType != AccessibilityEvent.TYPE_VIEW_TEXT_CHANGED &&
            event.eventType != AccessibilityEvent.TYPE_VIEW_FOCUSED
        ) return

        val node = event.source ?: rootInActiveWindow?.findFocus(AccessibilityNodeInfo.FOCUS_INPUT)
        if (node == null) return
        try {
            if (!node.isEditable || node.isPassword) return
            val nodePackage = node.packageName?.toString() ?: packageName
            if (nodePackage !in settings.targetAppPackages) return
            val content = node.text?.toString()?.take(MAX_INPUT_CHARS).orEmpty()
            val nodeId = "${node.windowId}:${node.viewIdResourceName ?: node.hashCode()}"
            val isNewInput = nodeId != latestInputNodeId || nodePackage != latestPackage
            if (!isNewInput && content == latestText) return
            val previousText = if (isNewInput) "" else latestText
            pendingCopySelection = null
            if (content.isBlank()) {
                cancelPending()
                overlay.hide()
                latestText = ""
                latestBounds = Rect()
                latestPackage = nodePackage
                latestInputNodeId = nodeId
                translatedText = ""
                return
            }

            val bounds = Rect().also(node::getBoundsInScreen)
            latestText = content
            latestBounds = bounds
            latestPackage = nodePackage
            latestInputNodeId = nodeId
            currentWindowPackage = nodePackage
            if (event.eventType == AccessibilityEvent.TYPE_VIEW_FOCUSED) {
                // Focus identifies the active field but does not mean the user changed its text.
                cancelPending()
                overlay.hide()
                translatedText = ""
                return
            }
            onInputChanged(previousText, content, bounds, nodePackage, isNewInput)
        } finally {
            node.recycleIfNeeded()
        }
    }

    private fun onInputChanged(previousText: String, text: String, bounds: Rect, packageName: String, isNewInput: Boolean) {
        cancelPending()
        overlay.hide()
        translatedText = ""

        when {
            settings.translateOnTextChange -> queueTranslation(text, bounds, packageName)
            settings.translateOnSentenceEnd &&
                (if (isNewInput) endsWithSentenceTerminator(text) else
                    containsSentenceTerminator(getChangedSegment(previousText, text)) || endsWithSentenceTerminator(text)) ->
                queueTranslation(text, bounds, packageName)
            settings.translateAfterInactivity &&
                (!settings.translateOnSentenceEnd || !endsWithSentenceTerminator(text)) ->
                queueTranslation(text, bounds, packageName, settings.inactivityDelayMs.toLong())
        }
    }

    private fun queueTranslation(text: String, bounds: Rect, packageName: String?, delayMs: Long = 0L) {
        if (!isMonitoringAllowed() || text.isBlank()) return
        if (::speechClient.isInitialized) speechClient.stopPlayback()
        cancelPending()
        val thisRequestId = ++requestId
        overlay.show(if (delayMs > 0) "等待输入…" else "翻译中…", bounds)
        debounceJob = serviceScope.launch {
            if (delayMs > 0) delay(delayMs)
            if (thisRequestId != requestId) return@launch
            overlay.show("翻译中…", bounds)
            requestJob = serviceScope.launch(Dispatchers.IO) {
                try {
                    val result = translationClient.translate(settings, text)
                    val speechProfile = settings.activeSpeechProfile
                    if (settings.realTimeSpeechEnabled && speechProfile != null) {
                        runCatching { speechClient.speak(speechProfile, result) }
                    }
                    launch(Dispatchers.Main.immediate) {
                        if (thisRequestId == requestId && isMonitoringAllowed() && currentWindowPackage == packageName) {
                            translatedText = result
                            overlay.show(result, bounds)
                        }
                    }
                } catch (cancelled: CancellationException) {
                    throw cancelled
                } catch (failure: Throwable) {
                    val message = (failure as? TranslationException)?.message ?: "翻译失败：${failure.message ?: "请检查网络和接口设置。"}"
                    launch(Dispatchers.Main.immediate) {
                        if (thisRequestId == requestId && isMonitoringAllowed() && currentWindowPackage == packageName) {
                            overlay.show(message, bounds, isError = true)
                        }
                    }
                }
            }
        }
    }

    private fun updateClipboardMonitoring(enabled: Boolean) {
        val manager = clipboardManager ?: return
        if (enabled == clipboardListenerRegistered) return
        runCatching {
            if (enabled) manager.addPrimaryClipChangedListener(clipboardListener)
            else manager.removePrimaryClipChangedListener(clipboardListener)
            clipboardListenerRegistered = enabled
        }
    }

    private fun handleClipboardChanged() {
        if (!settings.translateAfterCopy || !isMonitoringAllowed()) return
        val clip = runCatching { clipboardManager?.primaryClip }.getOrNull() ?: return
        if (clip.itemCount == 0) return
        val text = clip.getItemAt(0).text?.toString()?.trim()?.take(MAX_INPUT_CHARS).orEmpty()
        if (text.isBlank() || android.util.Patterns.WEB_URL.matcher(text).matches()) return

        val root = rootInActiveWindow
        val packageName = currentWindowPackage ?: root?.packageName?.toString() ?: latestPackage
        if (packageName !in settings.targetAppPackages) {
            root?.recycleIfNeeded()
            return
        }

        if (wasRecentlyHandled(text)) {
            root?.recycleIfNeeded()
            return
        }
        markHandled(text)

        val bounds = Rect()
        val focusedInput = root?.findFocus(AccessibilityNodeInfo.FOCUS_INPUT)
        if (focusedInput != null) {
            focusedInput.getBoundsInScreen(bounds)
            focusedInput.recycleIfNeeded()
        } else if (latestPackage == packageName && !latestBounds.isEmpty) {
            bounds.set(latestBounds)
        } else {
            root?.getBoundsInScreen(bounds)
        }
        root?.recycleIfNeeded()
        currentWindowPackage = packageName
        queueTranslation(text, bounds, packageName)
    }

    private fun rememberCopySelection(event: AccessibilityEvent, packageName: String) {
        val node = event.source ?: return
        try {
            if (node.isPassword) return
            val text = node.text?.toString().orEmpty()
            val start = minOf(event.fromIndex, event.toIndex).coerceIn(0, text.length)
            val end = maxOf(event.fromIndex, event.toIndex).coerceIn(0, text.length)
            if (end <= start) {
                pendingCopySelection = null
                return
            }
            val selectedText = text.substring(start, end).trim().take(MAX_INPUT_CHARS)
            if (selectedText.isBlank() || android.util.Patterns.WEB_URL.matcher(selectedText).matches()) {
                pendingCopySelection = null
                return
            }
            val bounds = Rect().also(node::getBoundsInScreen)
            pendingCopySelection = CopySelectionSnapshot(
                text = selectedText,
                packageName = packageName,
                bounds = bounds,
                capturedAt = SystemClock.elapsedRealtime(),
            )
        } finally {
            node.recycleIfNeeded()
        }
    }

    private fun handleCopyButtonClick(event: AccessibilityEvent): Boolean {
        if (event.eventType != AccessibilityEvent.TYPE_VIEW_CLICKED || !settings.translateAfterCopy || !isMonitoringAllowed()) return false
        val selection = pendingCopySelection ?: return false
        val now = SystemClock.elapsedRealtime()
        if (now - selection.capturedAt > COPY_SELECTION_VALID_MS) {
            pendingCopySelection = null
            return false
        }

        val source = event.source
        val labels = buildList {
            event.text.forEach { add(it.toString()) }
            source?.text?.toString()?.let(::add)
            source?.contentDescription?.toString()?.let(::add)
        }
        source?.recycleIfNeeded()
        val isCopyAction = labels.any { label ->
            label.contains("复制", ignoreCase = true) || COPY_LABEL_REGEX.containsMatchIn(label)
        }
        if (!isCopyAction) return false

        pendingCopySelection = null
        if (selection.packageName !in settings.targetAppPackages || wasRecentlyHandled(selection.text)) return true
        markHandled(selection.text)
        currentWindowPackage = selection.packageName
        queueTranslation(selection.text, selection.bounds, selection.packageName)
        return true
    }

    private fun wasRecentlyHandled(text: String): Boolean {
        val now = SystemClock.elapsedRealtime()
        return text == lastHandledClipboardText && now - lastHandledClipboardAt < DUPLICATE_CLIPBOARD_WINDOW_MS
    }

    private fun markHandled(text: String) {
        lastHandledClipboardText = text
        lastHandledClipboardAt = SystemClock.elapsedRealtime()
    }

    private fun isTransientSystemPackage(packageName: String): Boolean =
        isInputMethod(packageName) || packageName == "android" || packageName == "com.android.systemui"

    private fun isMonitoringAllowed(): Boolean =
        settings.isLoaded && settings.userConsented && settings.automaticTranslationEnabled &&
            settings.targetAppPackages.isNotEmpty()

    private fun cancelPending() {
        requestId++
        debounceJob?.cancel()
        requestJob?.cancel()
        debounceJob = null
        requestJob = null
    }

    private fun getChangedSegment(previous: String, current: String): String {
        var prefixLength = 0
        val commonLength = minOf(previous.length, current.length)
        while (prefixLength < commonLength && previous[prefixLength] == current[prefixLength]) prefixLength++
        var previousEnd = previous.length
        var currentEnd = current.length
        while (previousEnd > prefixLength && currentEnd > prefixLength && previous[previousEnd - 1] == current[currentEnd - 1]) {
            previousEnd--
            currentEnd--
        }
        return current.substring(prefixLength, currentEnd)
    }

    private fun containsSentenceTerminator(text: String): Boolean = text.indices.any { index ->
        val char = text[index]
        char in "\r\n。！？!?;；,，、:：…" ||
            (char == '.' && (index + 1 == text.length || text[index + 1].isWhitespace()))
    }

    private fun endsWithSentenceTerminator(text: String): Boolean {
        val last = text.trimEnd().lastOrNull() ?: return false
        return last in "\r\n。！？!?;；,，、:：…."
    }

    private fun isInputMethod(packageName: String): Boolean {
        val manager = getSystemService(Context.INPUT_METHOD_SERVICE) as? InputMethodManager ?: return false
        return manager.enabledInputMethodList.any { it.packageName == packageName }
    }

    override fun onInterrupt() {
        cancelPending()
        if (::overlay.isInitialized) overlay.hide()
    }

    override fun onDestroy() {
        if (clipboardListenerRegistered) {
            runCatching { clipboardManager?.removePrimaryClipChangedListener(clipboardListener) }
            clipboardListenerRegistered = false
        }
        cancelPending()
        if (::overlay.isInitialized) overlay.hide()
        if (::serviceScope.isInitialized) serviceScope.cancel()
        translationClient.close()
        if (::speechClient.isInitialized) speechClient.close()
        super.onDestroy()
    }

    private fun AccessibilityNodeInfo.recycleIfNeeded() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) recycle()
    }

    companion object {
        private const val MAX_INPUT_CHARS = 4_000
        private const val DUPLICATE_CLIPBOARD_WINDOW_MS = 900L
        private const val COPY_SELECTION_VALID_MS = 8_000L
        private val COPY_LABEL_REGEX = Regex("\\bcopy\\b", RegexOption.IGNORE_CASE)
    }
}
