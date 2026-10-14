package com.cheems.translator.mobile

import android.app.Activity
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.cheems.translator.mobile.data.DEFAULT_HYMT2_MODEL
import com.cheems.translator.mobile.data.DEFAULT_TRANSLATION_PROMPT
import com.cheems.translator.mobile.data.MobileSettings
import com.cheems.translator.mobile.data.NIUTRANS_BODY_TEMPLATE
import com.cheems.translator.mobile.data.NIUTRANS_ENDPOINT
import com.cheems.translator.mobile.data.NIUTRANS_RESPONSE_PATH
import com.cheems.translator.mobile.data.PROFILE_AI
import com.cheems.translator.mobile.data.PROFILE_HTTP
import com.cheems.translator.mobile.data.PromptProfile
import com.cheems.translator.mobile.data.RESPONSE_JSON
import com.cheems.translator.mobile.data.RESPONSE_RAW
import com.cheems.translator.mobile.data.SecretStore
import com.cheems.translator.mobile.data.SettingsRepository
import com.cheems.translator.mobile.data.SpeechProfile
import com.cheems.translator.mobile.data.TranslationProfile
import com.cheems.translator.mobile.monitor.InputMonitorService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

private val PageBackground = Color(0xFFF4F6F9)
private val CardBackground = Color(0xFFFFFEFC)
private val BrandBlue = Color(0xFF176FC1)
private val BrandOrange = Color(0xFFA95032)
private val TextPrimary = Color(0xFF18212D)
private val TextSecondary = Color(0xFF667385)
private val Warning = Color(0xFFAA4C1D)

class MainActivity : ComponentActivity() {
    private var accessibilityServiceEnabled by mutableStateOf(false)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.statusBarColor = android.graphics.Color.rgb(244, 246, 249)
        window.navigationBarColor = android.graphics.Color.rgb(244, 246, 249)
        window.decorView.systemUiVisibility =
            android.view.View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR or android.view.View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR
        setContent {
            MaterialTheme(
                colorScheme = lightColorScheme(
                    primary = BrandBlue,
                    onPrimary = Color.White,
                    background = PageBackground,
                    surface = CardBackground,
                    onSurface = TextPrimary,
                ),
            ) {
                Surface(Modifier.fillMaxSize(), color = PageBackground) {
                    MobileSettingsApp(
                        repository = remember { SettingsRepository(applicationContext) },
                        accessibilityServiceEnabled = accessibilityServiceEnabled,
                        onOpenAccessibilitySettings = ::openAccessibilitySettings,
                        onExit = ::finish,
                    )
                }
            }
        }
        refreshAccessibilityServiceState()
    }

    override fun onResume() {
        super.onResume()
        refreshAccessibilityServiceState()
    }

    private fun openAccessibilitySettings() = startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))

    private fun refreshAccessibilityServiceState() {
        accessibilityServiceEnabled = isAccessibilityServiceEnabled(
            applicationContext,
            ComponentName(applicationContext, InputMonitorService::class.java),
        )
    }
}

private enum class MobilePage(val title: String) {
    HOME("Cheems 翻译"), APPS("监听的应用"), INTERFACES("翻译接口与模型"), PROMPTS("提示词管理"), SPEECH("语音模型"), TRIGGERS("翻译触发"), ABOUT("关于")
}

private data class LaunchableApp(val packageName: String, val label: String)

@Suppress("DEPRECATION")
private fun findUserApps(context: Context): List<LaunchableApp> {
    val intent = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
    return context.packageManager.queryIntentActivities(intent, PackageManager.MATCH_ALL)
        .asSequence()
        .mapNotNull { resolveInfo ->
            val activityInfo = resolveInfo.activityInfo ?: return@mapNotNull null
            val appInfo = activityInfo.applicationInfo ?: return@mapNotNull null
            if (appInfo.packageName == context.packageName) return@mapNotNull null
            val label = resolveInfo.loadLabel(context.packageManager)?.toString()?.trim()?.takeIf(String::isNotEmpty)
                ?: appInfo.packageName
            LaunchableApp(appInfo.packageName, label)
        }
        .distinctBy(LaunchableApp::packageName)
        .sortedWith(compareBy(String.CASE_INSENSITIVE_ORDER) { it.label })
        .toList()
}

private fun isAccessibilityServiceEnabled(context: Context, component: ComponentName): Boolean {
    val enabledServices = Settings.Secure.getString(context.contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES).orEmpty()
    val expected = component.flattenToString()
    return enabledServices.split(':').any { it.equals(expected, ignoreCase = true) }
}

@Composable
private fun MobileSettingsApp(
    repository: SettingsRepository,
    accessibilityServiceEnabled: Boolean,
    onOpenAccessibilitySettings: () -> Unit,
    onExit: () -> Unit,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val settings by repository.settings.collectAsState(initial = MobileSettings())
    var page by remember { mutableStateOf(MobilePage.HOME) }
    var showPrivacyDialog by remember { mutableStateOf(false) }
    var privacyWasAccepted by remember { mutableStateOf(false) }
    var availableApps by remember { mutableStateOf<List<LaunchableApp>>(emptyList()) }
    var appQuery by remember { mutableStateOf("") }
    var editingTranslation by remember { mutableStateOf<TranslationProfile?>(null) }
    var editingPrompt by remember { mutableStateOf<PromptProfile?>(null) }
    var editingSpeech by remember { mutableStateOf<SpeechProfile?>(null) }
    var addTranslationType by remember { mutableStateOf(false) }
    var addSpeechType by remember { mutableStateOf(false) }

    LaunchedEffect(Unit) { availableApps = withContext(Dispatchers.IO) { findUserApps(context) } }
    LaunchedEffect(settings.isLoaded, settings.userConsented) {
        if (settings.isLoaded && !settings.userConsented) {
            privacyWasAccepted = false
            showPrivacyDialog = true
        }
    }
    BackHandler(enabled = page != MobilePage.HOME) { page = MobilePage.HOME }

    Column(
        Modifier.fillMaxSize().background(PageBackground).padding(horizontal = 16.dp, vertical = 8.dp),
    ) {
        BrandHeader(
            title = page.title,
            showBack = page != MobilePage.HOME,
            onBack = { page = MobilePage.HOME },
            onAbout = { page = MobilePage.ABOUT },
        )
        Column(
            Modifier.weight(1f).fillMaxWidth().verticalScroll(rememberScrollState()).imePadding(),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            when (page) {
                MobilePage.HOME -> HomePage(
                    settings = settings,
                    accessibilityServiceEnabled = accessibilityServiceEnabled,
                    onOpenAccessibilitySettings = onOpenAccessibilitySettings,
                    onOpenPage = { page = it },
                    onAutomaticChanged = { enabled -> scope.launch { repository.setAutomaticTranslationEnabled(enabled) } },
                    onSpeechChanged = { enabled -> scope.launch { repository.setRealTimeSpeechEnabled(enabled) } },
                    onPrivacy = {
                        privacyWasAccepted = settings.userConsented
                        showPrivacyDialog = true
                    },
                )
                MobilePage.APPS -> AppsPage(
                    settings = settings,
                    apps = availableApps,
                    query = appQuery,
                    onQueryChange = { appQuery = it },
                    onToggleApp = { packageName, checked ->
                        val selected = if (checked) settings.targetAppPackages + packageName else settings.targetAppPackages - packageName
                        scope.launch { repository.setTargetAppPackages(selected) }
                    },
                )
                MobilePage.INTERFACES -> TranslationProfilesPage(
                    settings = settings,
                    onSelect = { id -> scope.launch { repository.saveTranslationProfiles(settings.translationProfiles, id) } },
                    onAdd = { addTranslationType = true },
                    onEdit = { editingTranslation = it },
                    onDelete = { profile ->
                        val next = settings.translationProfiles - profile
                        if (next.isNotEmpty()) {
                            val nextActive = if (profile.id == settings.activeTranslationProfileId) next.first().id else settings.activeTranslationProfileId
                            scope.launch { repository.saveTranslationProfiles(next, nextActive) }
                        }
                    },
                )
                MobilePage.PROMPTS -> PromptProfilesPage(
                    settings = settings,
                    onSelect = { id ->
                        scope.launch {
                            if (id == null) {
                                repository.setTranslationPromptEnabled(false)
                            } else {
                                repository.savePromptProfiles(settings.promptProfiles, id)
                                repository.setTranslationPromptEnabled(true)
                            }
                        }
                    },
                    onAdd = { editingPrompt = PromptProfile(name = "新建翻译提示词", prompt = DEFAULT_TRANSLATION_PROMPT) },
                    onEdit = { editingPrompt = it },
                    onDelete = { profile ->
                        val next = settings.promptProfiles - profile
                        if (next.isNotEmpty()) {
                            val nextActive = if (profile.id == settings.activePromptProfileId) next.first().id else settings.activePromptProfileId
                            scope.launch { repository.savePromptProfiles(next, nextActive) }
                        }
                    },
                )
                MobilePage.SPEECH -> SpeechProfilesPage(
                    settings = settings,
                    onSelect = { id -> scope.launch { repository.saveSpeechProfiles(settings.speechProfiles, id) } },
                    onAdd = { addSpeechType = true },
                    onEdit = { editingSpeech = it },
                    onDelete = { profile ->
                        val next = settings.speechProfiles - profile
                        if (next.isNotEmpty()) {
                            val nextActive = if (profile.id == settings.activeSpeechProfileId) next.first().id else settings.activeSpeechProfileId
                            scope.launch { repository.saveSpeechProfiles(next, nextActive) }
                        }
                    },
                    onRealtimeChanged = { enabled -> scope.launch { repository.setRealTimeSpeechEnabled(enabled) } },
                )
                MobilePage.TRIGGERS -> TriggerSettingsPage(
                    settings = settings,
                    onSettingsChanged = { onTextChange, onSentenceEnd, afterCopy, afterInactivity, delayMs ->
                        scope.launch {
                            repository.saveTriggerSettings(
                                onTextChange,
                                onSentenceEnd,
                                afterCopy,
                                afterInactivity,
                                delayMs,
                            )
                        }
                    },
                )
                MobilePage.ABOUT -> AboutPage(context)
            }
            // Keep the final card clear of the viewport edge when the page is scrolled to its end.
            Spacer(Modifier.height(18.dp))
        }
    }

    if (addTranslationType) {
        ProfileTypeDialog(
            title = "添加翻译接口",
            onDismiss = { addTranslationType = false },
            onSelect = { type ->
                addTranslationType = false
                editingTranslation = TranslationProfile(
                    name = if (type == PROFILE_AI) "新建 AI 接口" else "新建 HTTP 接口",
                    type = type,
                    authType = if (type == PROFILE_AI) "Bearer" else "None",
                    model = if (type == PROFILE_AI) DEFAULT_HYMT2_MODEL else "",
                    endpoint = if (type == PROFILE_AI) "https://api.openai.com/v1" else "",
                )
            },
        )
    }
    if (editingTranslation != null) {
        TranslationProfileEditorDialog(
            profile = editingTranslation!!,
            onDismiss = { editingTranslation = null },
            onSave = { updated ->
                val wasExisting = settings.translationProfiles.any { it.id == updated.id }
                val next = settings.translationProfiles.filterNot { it.id == updated.id } + updated
                val selectedId = if (wasExisting) settings.activeTranslationProfileId else updated.id
                scope.launch { repository.saveTranslationProfiles(next, selectedId) }
                editingTranslation = null
            },
        )
    }
    if (editingPrompt != null) {
        PromptEditorDialog(
            profile = editingPrompt!!,
            onDismiss = { editingPrompt = null },
            onSave = { updated ->
                val wasExisting = settings.promptProfiles.any { it.id == updated.id }
                val next = settings.promptProfiles.filterNot { it.id == updated.id } + updated
                val selectedId = if (wasExisting) settings.activePromptProfileId else updated.id
                scope.launch {
                    repository.savePromptProfiles(next, selectedId)
                    if (!wasExisting) repository.setTranslationPromptEnabled(true)
                }
                editingPrompt = null
            },
        )
    }
    if (addSpeechType) {
        ProfileTypeDialog(
            title = "添加语音模型",
            onDismiss = { addSpeechType = false },
            onSelect = { type ->
                addSpeechType = false
                editingSpeech = SpeechProfile(name = if (type == PROFILE_AI) "新建语音 AI" else "新建 HTTP 语音", type = type)
            },
        )
    }
    if (editingSpeech != null) {
        SpeechProfileEditorDialog(
            profile = editingSpeech!!,
            onDismiss = { editingSpeech = null },
            onSave = { updated ->
                val wasExisting = settings.speechProfiles.any { it.id == updated.id }
                val next = settings.speechProfiles.filterNot { it.id == updated.id } + updated
                val selectedId = if (wasExisting) settings.activeSpeechProfileId else updated.id
                scope.launch { repository.saveSpeechProfiles(next, selectedId) }
                editingSpeech = null
            },
        )
    }

    if (showPrivacyDialog) {
        PrivacyDialog(
            alreadyAccepted = privacyWasAccepted,
            onDismiss = {
                if (privacyWasAccepted) showPrivacyDialog = false
            },
            onAgree = {
                scope.launch { repository.saveConsent(true) }
                showPrivacyDialog = false
            },
            onDecline = {
                showPrivacyDialog = false
                onExit()
            },
        )
    }
}

@Composable
private fun BrandHeader(title: String, showBack: Boolean, onBack: () -> Unit, onAbout: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().padding(top = 4.dp, bottom = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(11.dp),
    ) {
        if (showBack) {
            TextButton(onClick = onBack, contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 5.dp, vertical = 6.dp)) {
                Text("‹", color = BrandBlue, fontSize = 30.sp)
            }
        }
        Image(
            painter = painterResource(R.drawable.translator_icon),
            contentDescription = "Cheems 翻译图标",
            modifier = Modifier.size(if (showBack) 40.dp else 48.dp),
            contentScale = ContentScale.Fit,
        )
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(1.dp)) {
            Text(title, color = TextPrimary, fontSize = 19.sp, fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(if (showBack) "Cheems 翻译设置" else "输入框实时翻译", color = TextSecondary, fontSize = 11.sp)
        }
        if (!showBack) TextButton(onClick = onAbout) { Text("关于", color = BrandBlue) }
    }
}

@Composable
private fun HomePage(
    settings: MobileSettings,
    accessibilityServiceEnabled: Boolean,
    onOpenAccessibilitySettings: () -> Unit,
    onOpenPage: (MobilePage) -> Unit,
    onAutomaticChanged: (Boolean) -> Unit,
    onSpeechChanged: (Boolean) -> Unit,
    onPrivacy: () -> Unit,
) {
    val activeTranslation = settings.activeTranslationProfile
    val activeSpeech = settings.activeSpeechProfile
    CardSurface {
        Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                    Text("输入框实时翻译", fontWeight = FontWeight.Bold, fontSize = 18.sp)
                    Text("只处理你选择的 App 中可访问的输入框", color = TextSecondary, fontSize = 12.sp)
                }
                Switch(
                    checked = settings.automaticTranslationEnabled,
                    enabled = settings.userConsented && settings.targetAppPackages.isNotEmpty() && settings.isTranslationConfigured,
                    onCheckedChange = onAutomaticChanged,
                )
            }
            DividerLine()
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                ServiceStatusDot(accessibilityServiceEnabled)
                Text(
                    if (accessibilityServiceEnabled) "无障碍服务已开启" else "需要开启无障碍服务",
                    modifier = Modifier.weight(1f), color = TextSecondary, fontSize = 13.sp,
                )
                OutlinedButton(onClick = onOpenAccessibilitySettings, enabled = settings.userConsented && settings.targetAppPackages.isNotEmpty()) {
                    Text(if (accessibilityServiceEnabled) "查看" else "去开启")
                }
            }
            if (settings.targetAppPackages.isEmpty()) HintText("先添加要监听的 App，再开启实时翻译。")
            else if (!settings.isTranslationConfigured) HintText("先配置并勾选一个可用的翻译接口。")
        }
    }

    SectionTitle("功能设置")
    SettingsEntry(
        title = "监听的应用",
        subtitle = "已选择 ${settings.targetAppPackages.size} 个 App",
        badge = "${settings.targetAppPackages.size}",
        onClick = { onOpenPage(MobilePage.APPS) },
    )
    SettingsEntry(
        title = "翻译接口与模型",
        subtitle = activeTranslation?.let { "${it.name} · ${if (it.type == PROFILE_AI) it.model else "HTTP 接口"}" } ?: "未配置",
        badge = activeTranslation?.type ?: "设置",
        onClick = { onOpenPage(MobilePage.INTERFACES) },
    )
    SettingsEntry(
        title = "翻译提示词",
        subtitle = if (settings.translationPromptEnabled) settings.activePromptProfile?.name ?: "未选择提示词" else "未启用提示词",
        badge = "${settings.promptProfiles.size}",
        onClick = { onOpenPage(MobilePage.PROMPTS) },
    )
    CardSurface {
        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("实时语音", fontWeight = FontWeight.SemiBold, fontSize = 15.sp)
                    Text("翻译完成后自动朗读译文", color = TextSecondary, fontSize = 12.sp)
                }
                Switch(
                    checked = settings.realTimeSpeechEnabled,
                    enabled = activeSpeech?.endpoint?.isNotBlank() == true && activeSpeech.model.isNotBlank(),
                    onCheckedChange = onSpeechChanged,
                )
            }
        }
    }
    SettingsEntry(
        title = "语音模型与接口",
        subtitle = activeSpeech?.let { "${it.name} · ${it.model.ifBlank { if (it.type == PROFILE_AI) it.voice else "HTTP 接口" }}" } ?: "尚未选择语音模型",
        badge = activeSpeech?.type ?: "设置",
        onClick = { onOpenPage(MobilePage.SPEECH) },
    )

    SectionTitle("更多设置")
    val triggerSummary = buildList {
        if (settings.translateOnTextChange) add("文字变化立即翻译")
        if (settings.translateOnSentenceEnd) add("常见标点后")
        if (settings.translateAfterInactivity) add("停顿 ${formatDelaySeconds(settings.inactivityDelayMs)} 秒后")
        if (settings.translateAfterCopy) add("复制后")
    }.joinToString(" · ").ifBlank { "未启用翻译触发" }
    SettingsEntry(
        title = "翻译触发",
        subtitle = triggerSummary,
        badge = "设置",
        onClick = { onOpenPage(MobilePage.TRIGGERS) },
    )
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.Center) {
        TextButton(onClick = onPrivacy) { Text("查看隐私说明", color = TextSecondary) }
    }
}

@Composable
private fun TriggerSettingsPage(
    settings: MobileSettings,
    onSettingsChanged: (Boolean, Boolean, Boolean, Boolean, Int) -> Unit,
) {
    var delaySeconds by remember { mutableStateOf(formatDelaySeconds(settings.inactivityDelayMs)) }
    val parsedDelayMs = delaySeconds.toDoubleOrNull()
        ?.takeIf { it in 0.5..60.0 }
        ?.let { (it * 1_000).toInt() }
    fun save(
        onTextChange: Boolean = settings.translateOnTextChange,
        onSentenceEnd: Boolean = settings.translateOnSentenceEnd,
        afterCopy: Boolean = settings.translateAfterCopy,
        afterInactivity: Boolean = settings.translateAfterInactivity,
        delayMs: Int = parsedDelayMs ?: settings.inactivityDelayMs,
    ) = onSettingsChanged(onTextChange, onSentenceEnd, afterCopy, afterInactivity, delayMs)

    Text("选择输入内容的提交时机。复制触发可以与输入触发同时使用。", color = TextSecondary, fontSize = 13.sp, lineHeight = 20.sp)
    CardSurface {
        Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text("输入触发方式", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
            TriggerToggleRow(
                title = "文字变化时立即翻译",
                description = "输入内容一变化就提交；开启后，标点和停顿条件暂停使用。",
                checked = settings.translateOnTextChange,
                onCheckedChange = { save(onTextChange = it) },
            )
            DividerLine()
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("按条件提交", fontWeight = FontWeight.SemiBold, fontSize = 14.sp, modifier = Modifier.weight(1f))
                Text("可组合", color = TextSecondary, fontSize = 11.sp)
            }
            DividerLine()
            TriggerToggleRow(
                title = "常见标点后翻译",
                description = "逗号、分号、冒号、句号、省略号等中英文标点均可触发。",
                checked = settings.translateOnSentenceEnd,
                enabled = !settings.translateOnTextChange,
                onCheckedChange = { save(onTextChange = false, onSentenceEnd = it) },
            )
            DividerLine()
            TriggerToggleRow(
                title = "输入停顿后翻译",
                description = "没有触发标点时，停顿达到设定时长后提交。",
                checked = settings.translateAfterInactivity,
                enabled = !settings.translateOnTextChange,
                onCheckedChange = { save(onTextChange = false, afterInactivity = it) },
            )
            OutlinedTextField(
                value = delaySeconds,
                onValueChange = { input ->
                    if (input.matches(Regex("\\d{0,2}(\\.\\d{0,3})?"))) {
                        delaySeconds = input
                        input.toDoubleOrNull()?.takeIf { it in 0.5..60.0 }?.let { seconds ->
                            save(delayMs = (seconds * 1_000).toInt())
                        }
                    }
                },
                modifier = Modifier.fillMaxWidth(),
                enabled = !settings.translateOnTextChange && settings.translateAfterInactivity,
                label = { Text("停顿延迟时间（0.5–60 秒）") },
                suffix = { Text("秒") },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                isError = delaySeconds.isNotEmpty() && parsedDelayMs == null,
            )
            if (delaySeconds.isNotEmpty() && parsedDelayMs == null) {
                Text("请输入 0.5 到 60 秒之间的时间。", color = Warning, fontSize = 11.sp)
            }
        }
    }
    CardSurface {
        TriggerToggleRow(
            title = "复制后自动翻译",
            description = "复制纯文本后触发翻译，可与上方任一输入方式同时使用。",
            checked = settings.translateAfterCopy,
            onCheckedChange = { save(afterCopy = it) },
        )
        if (settings.translateAfterCopy) {
            Text(
                "Android 会限制后台读取剪贴板。选中文本后点系统“复制”可尝试通过无障碍事件触发；其他复制方式取决于系统和设备。",
                color = TextSecondary,
                fontSize = 11.sp,
                lineHeight = 16.sp,
                modifier = Modifier.padding(top = 6.dp),
            )
        }
    }
}

@Composable
private fun TriggerToggleRow(
    title: String,
    description: String,
    checked: Boolean,
    enabled: Boolean = true,
    onCheckedChange: (Boolean) -> Unit,
) {
    Row(
        Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
            Text(title, fontWeight = FontWeight.Medium, fontSize = 14.sp)
            Text(description, color = TextSecondary, fontSize = 12.sp, lineHeight = 17.sp)
        }
        Switch(checked = checked, enabled = enabled, onCheckedChange = onCheckedChange)
    }
}

private fun formatDelaySeconds(delayMs: Int): String =
    if (delayMs % 1_000 == 0) (delayMs / 1_000).toString()
    else java.lang.String.format(java.util.Locale.ROOT, "%.3f", delayMs / 1_000.0).trimEnd('0').trimEnd('.')

@Composable
private fun SettingsEntry(title: String, subtitle: String, badge: String, onClick: () -> Unit) {
    Card(
        Modifier.fillMaxWidth().clickable(onClick = onClick),
        shape = RoundedCornerShape(18.dp),
        colors = CardDefaults.cardColors(containerColor = CardBackground),
        elevation = CardDefaults.cardElevation(defaultElevation = 1.dp),
    ) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 15.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                Text(title, color = TextPrimary, fontWeight = FontWeight.SemiBold, fontSize = 15.sp)
                Text(subtitle, color = TextSecondary, fontSize = 12.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            Surface(color = Color(0xFFE9F2FB), shape = RoundedCornerShape(30.dp)) {
                Text(badge, color = BrandBlue, fontWeight = FontWeight.SemiBold, fontSize = 11.sp, modifier = Modifier.padding(horizontal = 10.dp, vertical = 6.dp))
            }
            Text("›", color = TextSecondary, fontSize = 24.sp)
        }
    }
}

@Composable
private fun AppsPage(
    settings: MobileSettings,
    apps: List<LaunchableApp>,
    query: String,
    onQueryChange: (String) -> Unit,
    onToggleApp: (String, Boolean) -> Unit,
) {
    var showSelectedOnly by remember { mutableStateOf(false) }
    Text("勾选后，只有这些 App 的输入框会参与翻译。", color = TextSecondary, fontSize = 13.sp, lineHeight = 20.sp)
    CardSurface {
        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("已选择 ${settings.targetAppPackages.size} 个应用", fontWeight = FontWeight.SemiBold)
            OutlinedTextField(query, onQueryChange, modifier = Modifier.fillMaxWidth(), label = { Text("搜索已安装的 App") }, singleLine = true)
            FilterChip(
                selected = showSelectedOnly,
                onClick = { showSelectedOnly = !showSelectedOnly },
                label = { Text("只看已勾选") },
            )
            val filtered = remember(apps, query, settings.targetAppPackages, showSelectedOnly) {
                val normalized = query.trim()
                apps.filter { app ->
                    (!showSelectedOnly || app.packageName in settings.targetAppPackages) &&
                        (normalized.isEmpty() || app.label.contains(normalized, true) || app.packageName.contains(normalized, true))
                }
            }
            if (filtered.isEmpty()) {
                HintText(
                    when {
                        apps.isEmpty() -> "没有找到可选应用。"
                        showSelectedOnly && settings.targetAppPackages.isEmpty() -> "还没有勾选任何应用。"
                        else -> "没有匹配的应用。"
                    },
                )
            }
            else LazyColumn(Modifier.fillMaxWidth().heightIn(max = 530.dp)) {
                items(filtered, key = LaunchableApp::packageName) { app ->
                    Row(Modifier.fillMaxWidth().padding(vertical = 5.dp), verticalAlignment = Alignment.CenterVertically) {
                        Checkbox(app.packageName in settings.targetAppPackages, { onToggleApp(app.packageName, it) })
                        Column(Modifier.weight(1f)) {
                            Text(app.label, fontWeight = FontWeight.Medium)
                            Text(app.packageName, color = TextSecondary, fontSize = 10.sp)
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun TranslationProfilesPage(
    settings: MobileSettings,
    onSelect: (String) -> Unit,
    onAdd: () -> Unit,
    onEdit: (TranslationProfile) -> Unit,
    onDelete: (TranslationProfile) -> Unit,
) {
    Text("配置一次后，勾选当前使用的 AI 模型或 HTTP 翻译服务。", color = TextSecondary, fontSize = 13.sp, lineHeight = 20.sp)
    settings.translationProfiles.forEach { profile ->
        SelectableProfileCard(
            name = profile.name,
            description = if (profile.type == PROFILE_AI) "AI · ${profile.model.ifBlank { "未填写模型" }}" else "HTTP · ${profile.method} · ${profile.model.ifBlank { profile.endpoint }}",
            selected = profile.id == settings.activeTranslationProfileId,
            onSelect = { onSelect(profile.id) },
            onEdit = { onEdit(profile) },
            onDelete = { onDelete(profile) },
            canDelete = settings.translationProfiles.size > 1,
        )
    }
    Button(onClick = onAdd, modifier = Modifier.fillMaxWidth()) { Text("＋ 添加接口或模型") }
    CardSurface {
        HintText("HTTP 接口支持自定义请求方式、请求头、参数、JSON 请求体与响应字段；API Key 会在本机加密保存。")
    }
}

@Composable
private fun PromptProfilesPage(
    settings: MobileSettings,
    onSelect: (String?) -> Unit,
    onAdd: () -> Unit,
    onEdit: (PromptProfile) -> Unit,
    onDelete: (PromptProfile) -> Unit,
) {
    Text("选择用于翻译的提示词，也可以在这里添加、编辑或删除提示词。", color = TextSecondary, fontSize = 13.sp, lineHeight = 20.sp)
    SelectableProfileCard(
        name = "不使用提示词",
        description = "直接翻译输入内容，不追加系统提示词。",
        selected = !settings.translationPromptEnabled,
        onSelect = { onSelect(null) },
        onEdit = null,
        onDelete = null,
        canDelete = false,
    )
    settings.promptProfiles.forEach { profile ->
        SelectableProfileCard(
            name = profile.name,
            description = profile.prompt.replace('\n', ' ').take(72),
            selected = profile.id == settings.activePromptProfileId,
            onSelect = { onSelect(profile.id) },
            onEdit = { onEdit(profile) },
            onDelete = { onDelete(profile) },
            canDelete = settings.promptProfiles.size > 1,
        )
    }
    Button(onClick = onAdd, modifier = Modifier.fillMaxWidth()) { Text("＋ 新建提示词") }
}

@Composable
private fun SpeechProfilesPage(
    settings: MobileSettings,
    onSelect: (String) -> Unit,
    onAdd: () -> Unit,
    onEdit: (SpeechProfile) -> Unit,
    onDelete: (SpeechProfile) -> Unit,
    onRealtimeChanged: (Boolean) -> Unit,
) {
    CardSurface {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("翻译完成后自动朗读", fontWeight = FontWeight.SemiBold)
                Text("新播报会替换上一条语音。", color = TextSecondary, fontSize = 12.sp)
            }
            Switch(
                checked = settings.realTimeSpeechEnabled,
                enabled = settings.activeSpeechProfile?.let { it.endpoint.isNotBlank() && it.model.isNotBlank() } == true,
                onCheckedChange = onRealtimeChanged,
            )
        }
    }
    Text("支持 OpenAI 兼容语音接口与自定义 HTTP 音频服务。", color = TextSecondary, fontSize = 13.sp, lineHeight = 20.sp)
    settings.speechProfiles.forEach { profile ->
        SelectableProfileCard(
            name = profile.name,
            description = "${if (profile.type == PROFILE_AI) "AI" else "HTTP"} · ${profile.model.ifBlank { profile.voice.ifBlank { "尚未配置" } }}",
            selected = profile.id == settings.activeSpeechProfileId,
            onSelect = { onSelect(profile.id) },
            onEdit = { onEdit(profile) },
            onDelete = { onDelete(profile) },
            canDelete = settings.speechProfiles.size > 1,
        )
    }
    Button(onClick = onAdd, modifier = Modifier.fillMaxWidth()) { Text("＋ 添加语音模型") }
}

@Composable
private fun SelectableProfileCard(
    name: String,
    description: String,
    selected: Boolean,
    onSelect: () -> Unit,
    onEdit: (() -> Unit)?,
    onDelete: (() -> Unit)?,
    canDelete: Boolean,
) {
    Card(
        Modifier.fillMaxWidth().clickable(onClick = onSelect),
        shape = RoundedCornerShape(17.dp),
        colors = CardDefaults.cardColors(containerColor = if (selected) Color(0xFFFFF7F0) else CardBackground),
        border = androidx.compose.foundation.BorderStroke(1.dp, if (selected) Color(0xFFE2B99F) else Color(0xFFE6EAF0)),
    ) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 10.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
            RadioButton(selected = selected, onClick = onSelect)
            Column(Modifier.weight(1f).padding(start = 3.dp)) {
                Text(name, fontWeight = FontWeight.SemiBold, fontSize = 14.sp)
                Text(description, color = TextSecondary, fontSize = 11.sp, maxLines = 2, overflow = TextOverflow.Ellipsis)
            }
            if (onEdit != null) TextButton(onClick = onEdit) { Text("编辑") }
            if (canDelete && onDelete != null) TextButton(onClick = onDelete) { Text("删除", color = Warning) }
        }
    }
}

@Composable
private fun ProfileTypeDialog(title: String, onDismiss: () -> Unit, onSelect: (String) -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("选择这个档案接入的服务类型。", color = TextSecondary)
                OutlinedButton(onClick = { onSelect(PROFILE_AI) }, modifier = Modifier.fillMaxWidth()) { Text("AI 模型 · OpenAI 兼容接口") }
                OutlinedButton(onClick = { onSelect(PROFILE_HTTP) }, modifier = Modifier.fillMaxWidth()) { Text("HTTP · 自定义请求接口") }
            }
        },
        confirmButton = {},
        dismissButton = { TextButton(onClick = onDismiss) { Text("取消") } },
    )
}

@Composable
private fun TranslationProfileEditorDialog(profile: TranslationProfile, onDismiss: () -> Unit, onSave: (TranslationProfile) -> Unit) {
    var name by remember(profile.id) { mutableStateOf(profile.name) }
    var endpoint by remember(profile.id) { mutableStateOf(profile.endpoint) }
    var model by remember(profile.id) { mutableStateOf(profile.model) }
    var apiKey by remember(profile.id) { mutableStateOf(runCatching { SecretStore.decrypt(profile.encryptedApiKey) }.getOrDefault("")) }
    var apiKeyVisible by remember { mutableStateOf(false) }
    var method by remember(profile.id) { mutableStateOf(profile.method) }
    var authType by remember(profile.id) { mutableStateOf(profile.authType) }
    var authHeader by remember(profile.id) { mutableStateOf(profile.authHeader) }
    var authQuery by remember(profile.id) { mutableStateOf(profile.authQueryName) }
    var basicUser by remember(profile.id) { mutableStateOf(profile.basicUsername) }
    var headers by remember(profile.id) { mutableStateOf(profile.headersJson) }
    var query by remember(profile.id) { mutableStateOf(profile.queryJson) }
    var body by remember(profile.id) { mutableStateOf(profile.bodyTemplate) }
    var contentType by remember(profile.id) { mutableStateOf(profile.contentType) }
    var responseType by remember(profile.id) { mutableStateOf(profile.responseType) }
    var responsePath by remember(profile.id) { mutableStateOf(profile.responseJsonPath) }
    var sourceLanguage by remember(profile.id) { mutableStateOf(profile.sourceLanguage) }
    var targetLanguage by remember(profile.id) { mutableStateOf(profile.targetLanguage) }
    var timeout by remember(profile.id) { mutableStateOf(profile.timeoutSeconds.toString()) }
    var error by remember(profile.id) { mutableStateOf("") }
    var presetSelection by remember(profile.id) { mutableStateOf("选择一个预设…") }
    var niuTransPresetApplied by remember(profile.id) { mutableStateOf(false) }
    val isAi = profile.type == PROFILE_AI
    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Card(
            Modifier.fillMaxWidth().padding(horizontal = 18.dp),
            shape = RoundedCornerShape(24.dp),
            colors = CardDefaults.cardColors(containerColor = CardBackground),
        ) {
            Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(11.dp)) {
                Text(if (isAi) "AI 接口档案" else "HTTP 接口档案", fontSize = 20.sp, fontWeight = FontWeight.Bold)
                Column(Modifier.weight(1f, fill = false).heightIn(max = 620.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    if (!isAi) {
                        ChoiceField("快速预设", presetSelection, listOf("选择一个预设…", "小牛翻译 · POST JSON")) { selected ->
                            presetSelection = selected
                            if (selected == "小牛翻译 · POST JSON") {
                                name = "小牛翻译"
                                endpoint = NIUTRANS_ENDPOINT
                                model = ""
                                method = "POST"
                                authType = "None"
                                headers = "{}"
                                query = "{}"
                                body = NIUTRANS_BODY_TEMPLATE
                                contentType = "application/json"
                                responseType = RESPONSE_JSON
                                responsePath = NIUTRANS_RESPONSE_PATH
                                sourceLanguage = "auto"
                                targetLanguage = "en"
                                niuTransPresetApplied = true
                            }
                        }
                        if (niuTransPresetApplied) HintText("小牛翻译预设已填入，请填写 API Key 后保存。")
                    }
                    TextInput("档案名称", name, { name = it }, singleLine = true)
                    TextInput(if (isAi) "API 地址（OpenAI 兼容）" else "完整接口 URL", endpoint, { endpoint = it }, singleLine = true, keyboardType = KeyboardType.Uri)
                    if (isAi) {
                        TextInput("模型名称", model, { model = it }, singleLine = true, placeholder = DEFAULT_HYMT2_MODEL)
                        TextInput("API Key（可选）", apiKey, { apiKey = it }, singleLine = true,
                            visualTransformation = if (apiKeyVisible) VisualTransformation.None else PasswordVisualTransformation(),
                            trailing = { TextButton(onClick = { apiKeyVisible = !apiKeyVisible }) { Text(if (apiKeyVisible) "隐藏" else "显示") } })
                        Text("支持本地 Ollama 的 OpenAI 兼容地址。", color = TextSecondary, fontSize = 11.sp)
                    } else {
                        TextInput("服务或模型标识（可选）", model, { model = it }, singleLine = true)
                        ChoiceField("请求方式", method, listOf("GET", "POST", "PUT", "PATCH", "DELETE"), { method = it })
                        ChoiceField("鉴权方式", authType, listOf("None", "Bearer", "ApiKeyHeader", "ApiKeyQuery", "Basic"), { authType = it })
                        TextInput("API Key / 密码", apiKey, { apiKey = it }, singleLine = true,
                            visualTransformation = if (apiKeyVisible) VisualTransformation.None else PasswordVisualTransformation(),
                            trailing = { TextButton(onClick = { apiKeyVisible = !apiKeyVisible }) { Text(if (apiKeyVisible) "隐藏" else "显示") } })
                        when (authType) {
                            "ApiKeyHeader" -> TextInput("鉴权请求头名称", authHeader, { authHeader = it }, singleLine = true)
                            "ApiKeyQuery" -> TextInput("鉴权查询参数名", authQuery, { authQuery = it }, singleLine = true)
                            "Basic" -> TextInput("Basic 用户名", basicUser, { basicUser = it }, singleLine = true)
                        }
                        TextInput("请求头 JSON", headers, { headers = it }, minLines = 2, placeholder = "{\"X-Token\":\"{{apiKey}}\"}")
                        TextInput("查询参数 JSON", query, { query = it }, minLines = 2, placeholder = "{\"source\":\"{{sourceLanguage}}\"}")
                        TextInput("请求体模板", body, { body = it }, minLines = 3, placeholder = "使用 {{text}}、{{model}}、{{apiKey}}、{{prompt}} 等变量")
                        TextInput("Content-Type", contentType, { contentType = it }, singleLine = true)
                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            TextInput("源语言", sourceLanguage, { sourceLanguage = it }, Modifier.weight(1f), singleLine = true)
                            TextInput("目标语言", targetLanguage, { targetLanguage = it }, Modifier.weight(1f), singleLine = true)
                        }
                        ChoiceField("响应内容", responseType, listOf(RESPONSE_JSON, RESPONSE_RAW), { responseType = it })
                        if (responseType == RESPONSE_JSON) TextInput("译文 JSON 路径（留空自动识别）", responsePath, { responsePath = it }, singleLine = true, placeholder = "data.translation")
                        Text("HTTP 模板变量：{{text}}、{{model}}、{{apiKey}}、{{prompt}}、{{sourceLanguage}}、{{targetLanguage}}。", color = TextSecondary, fontSize = 11.sp, lineHeight = 16.sp)
                    }
                    TextInput("请求超时（秒）", timeout, { timeout = it.filter(Char::isDigit) }, singleLine = true, keyboardType = KeyboardType.Number)
                    if (error.isNotBlank()) Text(error, color = Warning, fontSize = 12.sp)
                }
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End, verticalAlignment = Alignment.CenterVertically) {
                    TextButton(onClick = onDismiss) { Text("取消") }
                    Button(onClick = {
                        val seconds = timeout.toIntOrNull()
                        if (name.isBlank() || endpoint.isBlank() || (isAi && model.isBlank())) {
                            error = "请填写档案名称、接口地址" + if (isAi) "和模型名称。" else "。"
                        } else if (!isAi && endpoint.trim() == NIUTRANS_ENDPOINT && apiKey.isBlank()) {
                            error = "小牛翻译需要填写 API Key。"
                        } else if (!isAi && authType in listOf("Bearer", "ApiKeyHeader", "ApiKeyQuery", "Basic") && apiKey.isBlank()) {
                            error = "当前鉴权方式需要填写 API Key 或密码。"
                        } else if (seconds == null || seconds !in 1..600) {
                            error = "请求超时需设置为 1 到 600 秒。"
                        } else {
                            val encrypted = runCatching { if (apiKey.isBlank()) "" else SecretStore.encrypt(apiKey.trim()) }
                                .getOrElse { error = "API Key 加密失败：${it.message}"; return@Button }
                            onSave(profile.copy(
                                name = name.trim(), endpoint = endpoint.trim(), model = model.trim(), encryptedApiKey = encrypted,
                                method = method, authType = authType, authHeader = authHeader.trim(), authQueryName = authQuery.trim(),
                                basicUsername = basicUser.trim(), headersJson = headers, queryJson = query, bodyTemplate = body,
                                contentType = contentType.ifBlank { "application/json" }, responseType = responseType,
                                responseJsonPath = responsePath.trim(), sourceLanguage = sourceLanguage.trim(),
                                targetLanguage = targetLanguage.trim(), timeoutSeconds = seconds,
                            ))
                        }
                    }) { Text("保存档案") }
                }
            }
        }
    }
}

@Composable
private fun PromptEditorDialog(profile: PromptProfile, onDismiss: () -> Unit, onSave: (PromptProfile) -> Unit) {
    var name by remember(profile.id) { mutableStateOf(profile.name) }
    var prompt by remember(profile.id) { mutableStateOf(profile.prompt) }
    var error by remember(profile.id) { mutableStateOf("") }
    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Card(Modifier.fillMaxWidth().padding(horizontal = 20.dp), shape = RoundedCornerShape(24.dp), colors = CardDefaults.cardColors(containerColor = CardBackground)) {
            Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text("编辑翻译提示词", fontSize = 20.sp, fontWeight = FontWeight.Bold)
                TextInput("提示词名称", name, { name = it }, singleLine = true)
                TextInput("提示词内容", prompt, { prompt = it }, minLines = 7, maxLines = 12)
                if (error.isNotBlank()) Text(error, color = Warning, fontSize = 12.sp)
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                    TextButton(onClick = onDismiss) { Text("取消") }
                    Button(onClick = {
                        if (name.isBlank() || prompt.isBlank()) error = "名称和提示词内容不能为空。"
                        else onSave(profile.copy(name = name.trim(), prompt = prompt.trim()))
                    }) { Text("保存") }
                }
            }
        }
    }
}

@Composable
private fun SpeechProfileEditorDialog(profile: SpeechProfile, onDismiss: () -> Unit, onSave: (SpeechProfile) -> Unit) {
    var name by remember(profile.id) { mutableStateOf(profile.name) }
    var endpoint by remember(profile.id) { mutableStateOf(profile.endpoint) }
    var model by remember(profile.id) { mutableStateOf(profile.model) }
    var voice by remember(profile.id) { mutableStateOf(profile.voice) }
    var key by remember(profile.id) { mutableStateOf(runCatching { SecretStore.decrypt(profile.encryptedApiKey) }.getOrDefault("")) }
    var keyVisible by remember { mutableStateOf(false) }
    var method by remember(profile.id) { mutableStateOf(profile.method) }
    var headers by remember(profile.id) { mutableStateOf(profile.headersJson) }
    var body by remember(profile.id) { mutableStateOf(profile.bodyTemplate) }
    var responseType by remember(profile.id) { mutableStateOf(profile.responseType) }
    var responsePath by remember(profile.id) { mutableStateOf(profile.responseJsonPath) }
    var format by remember(profile.id) { mutableStateOf(profile.audioFormat) }
    var timeout by remember(profile.id) { mutableStateOf(profile.timeoutSeconds.toString()) }
    var error by remember(profile.id) { mutableStateOf("") }
    val isAi = profile.type == PROFILE_AI
    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Card(Modifier.fillMaxWidth().padding(horizontal = 18.dp), shape = RoundedCornerShape(24.dp), colors = CardDefaults.cardColors(containerColor = CardBackground)) {
            Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text(if (isAi) "AI 语音档案" else "HTTP 语音档案", fontSize = 20.sp, fontWeight = FontWeight.Bold)
                Column(Modifier.weight(1f, fill = false).heightIn(max = 620.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(9.dp)) {
                    TextInput("档案名称", name, { name = it }, singleLine = true)
                    TextInput(if (isAi) "API 地址（OpenAI 兼容）" else "完整语音 URL", endpoint, { endpoint = it }, singleLine = true, keyboardType = KeyboardType.Uri)
                    TextInput("模型名称", model, { model = it }, singleLine = true, placeholder = "例如：tts-1、kokoro")
                    TextInput("音色", voice, { voice = it }, singleLine = true, placeholder = "例如：alloy、af_heart")
                    TextInput("API Key（可选）", key, { key = it }, singleLine = true,
                        visualTransformation = if (keyVisible) VisualTransformation.None else PasswordVisualTransformation(),
                        trailing = { TextButton(onClick = { keyVisible = !keyVisible }) { Text(if (keyVisible) "隐藏" else "显示") } })
                    if (!isAi) {
                        ChoiceField("请求方式", method, listOf("GET", "POST", "PUT"), { method = it })
                        TextInput("请求头 JSON", headers, { headers = it }, minLines = 2, placeholder = "{\"Authorization\":\"Bearer {{apiKey}}\"}")
                        TextInput("请求体模板", body, { body = it }, minLines = 3, placeholder = "使用 {{text}}、{{model}}、{{voice}}、{{apiKey}} 等变量")
                        ChoiceField("音频响应", responseType, listOf("RawAudio", "JsonBase64", "JsonUrl"), { responseType = it })
                        if (responseType != "RawAudio") TextInput("音频 JSON 路径", responsePath, { responsePath = it }, singleLine = true, placeholder = "data.audio")
                        ChoiceField("音频格式", format, listOf("mp3", "wav"), { format = it })
                        Text("HTTP 模板支持 {{text}}、{{model}}、{{voice}}、{{apiKey}}。", color = TextSecondary, fontSize = 11.sp)
                    } else {
                        ChoiceField("音频格式", format, listOf("mp3", "wav"), { format = it })
                    }
                    TextInput("请求超时（秒）", timeout, { timeout = it.filter(Char::isDigit) }, singleLine = true, keyboardType = KeyboardType.Number)
                    if (error.isNotBlank()) Text(error, color = Warning, fontSize = 12.sp)
                }
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                    TextButton(onClick = onDismiss) { Text("取消") }
                    Button(onClick = {
                        val seconds = timeout.toIntOrNull()
                        if (name.isBlank() || endpoint.isBlank() || model.isBlank() || voice.isBlank()) error = "请填写名称、地址、模型和音色。"
                        else if (seconds == null || seconds !in 1..600) error = "请求超时需设置为 1 到 600 秒。"
                        else {
                            val encrypted = runCatching { if (key.isBlank()) "" else SecretStore.encrypt(key.trim()) }
                                .getOrElse { error = "API Key 加密失败：${it.message}"; return@Button }
                            onSave(profile.copy(
                                name = name.trim(), endpoint = endpoint.trim(), model = model.trim(), voice = voice.trim(),
                                encryptedApiKey = encrypted, method = method, headersJson = headers, bodyTemplate = body,
                                responseType = responseType, responseJsonPath = responsePath.trim(), audioFormat = format,
                                timeoutSeconds = seconds,
                            ))
                        }
                    }) { Text("保存档案") }
                }
            }
        }
    }
}

@Composable
private fun AboutPage(context: Context) {
    Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
        CardSurface {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                Image(
                    painter = painterResource(R.drawable.translator_icon),
                    contentDescription = "Cheems 翻译图标",
                    modifier = Modifier.size(64.dp),
                    contentScale = ContentScale.Fit,
                )
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text("Cheems 翻译", fontSize = 20.sp, fontWeight = FontWeight.Bold)
                    Text("让表达更自然，让交流更轻松。", color = TextSecondary, fontSize = 13.sp)
                    Text("当前版本 v${BuildConfig.VERSION_NAME}", color = BrandOrange, fontSize = 12.sp, fontWeight = FontWeight.SemiBold)
                }
            }
        }
        SectionTitle("开发者")
        CardSurface {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Image(painterResource(R.drawable.developer_avatar), "开发者头像", Modifier.size(48.dp).clip(CircleShape), contentScale = ContentScale.Crop)
                Column {
                    Text("萤灬虫", fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
                    Text("Cheems 翻译开发者", color = TextSecondary, fontSize = 12.sp)
                }
            }
        }
        SectionTitle("主页与社区")
        AboutLink(context, "个人主页", "cheems.cn", R.drawable.developer_avatar, Color.Transparent, 48, "https://cheems.cn")
        AboutLink(context, "GitHub", "github.com/firebugbugs", R.drawable.about_github, Color(0xFFF7F1EB), 27, "https://github.com/firebugbugs")
        AboutLink(context, "Gitee", "gitee.com/unbengable", R.drawable.about_gitee, Color(0xFFF7F1EB), 27, "https://gitee.com/unbengable")
        AboutLink(context, "B 站", "哔哩哔哩主页", R.drawable.about_bilibili, Color(0xFFF7F1EB), 27, "https://space.bilibili.com/43096314")
        AboutLink(context, "QQ群", "1094431427 · 点击加入交流群", R.drawable.about_qq, Color(0xFFEAF4FF), 42, "https://qm.qq.com/q/VdUX0gnh86")
        Text("感谢使用 Cheems 翻译", color = TextSecondary, fontSize = 12.sp, modifier = Modifier.align(Alignment.CenterHorizontally).padding(vertical = 5.dp))
    }
}

@Composable
private fun AboutLink(context: Context, title: String, subtitle: String, iconRes: Int, iconBackground: Color, iconSize: Int, url: String) {
    Card(
        Modifier.fillMaxWidth().clickable { openExternalLink(context, url) },
        shape = RoundedCornerShape(16.dp),
        colors = CardDefaults.cardColors(containerColor = CardBackground),
        elevation = CardDefaults.cardElevation(defaultElevation = 1.dp),
    ) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 12.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Box(
                Modifier.size(48.dp).clip(CircleShape).background(iconBackground),
                contentAlignment = Alignment.Center,
            ) {
                Image(
                    painter = painterResource(iconRes),
                    contentDescription = "$title 图标",
                    modifier = Modifier.size(iconSize.dp),
                    contentScale = if (iconRes == R.drawable.developer_avatar) ContentScale.Crop else ContentScale.Fit,
                )
            }
            Column(Modifier.weight(1f)) {
                Text(title, color = TextPrimary, fontSize = 14.sp, fontWeight = FontWeight.SemiBold)
                Text(subtitle, color = TextSecondary, fontSize = 11.sp)
            }
            Text("打开  ›", color = BrandBlue, fontSize = 12.sp)
        }
    }
}

@Composable
private fun PrivacyDialog(
    alreadyAccepted: Boolean,
    onDismiss: () -> Unit,
    onAgree: () -> Unit,
    onDecline: () -> Unit,
) {
    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Card(Modifier.fillMaxWidth().padding(horizontal = 22.dp), shape = RoundedCornerShape(24.dp), colors = CardDefaults.cardColors(containerColor = CardBackground)) {
            Column(Modifier.padding(22.dp), verticalArrangement = Arrangement.spacedBy(13.dp)) {
                Text("隐私说明与使用授权", fontSize = 20.sp, fontWeight = FontWeight.Bold)
                Text("在你开启自动翻译并选择目标 App 后，Cheems 会通过 Android 无障碍服务读取这些 App 当前可访问的编辑框文字，并发送给你配置的翻译接口。开启实时语音时，译文也会发送给所选语音服务。", color = TextSecondary, lineHeight = 21.sp)
                Text("Cheems 不保存原文，不会修改或替你发送输入内容；密码输入框会跳过。无障碍权限本身可能允许系统服务读取其他界面内容，请勿在输入支付密码或其他敏感信息时启用。", color = TextSecondary, lineHeight = 21.sp)
                Text("只有在你同意并主动选定应用后，实时翻译才会运行。你可以在系统设置中关闭无障碍服务。", color = TextSecondary, lineHeight = 21.sp)
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End, verticalAlignment = Alignment.CenterVertically) {
                    if (alreadyAccepted) {
                        TextButton(onClick = onDismiss) { Text("关闭") }
                    } else {
                        TextButton(onClick = onDecline) { Text("不同意并退出", color = TextSecondary) }
                        Button(onClick = onAgree) { Text("我已阅读并同意") }
                    }
                }
            }
        }
    }
}

@Composable
private fun ChoiceField(label: String, selected: String, options: List<String>, onSelected: (String) -> Unit) {
    var expanded by remember(label) { mutableStateOf(false) }
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Text(label, color = TextSecondary, fontSize = 12.sp)
        Box {
            OutlinedButton(onClick = { expanded = true }, modifier = Modifier.fillMaxWidth()) {
                Text(selected, modifier = Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text("⌄")
            }
            DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
                options.forEach { option -> DropdownMenuItem(text = { Text(option) }, onClick = { onSelected(option); expanded = false }) }
            }
        }
    }
}

@Composable
private fun TextInput(
    label: String,
    value: String,
    onValueChange: (String) -> Unit,
    modifier: Modifier = Modifier,
    singleLine: Boolean = false,
    minLines: Int = 1,
    maxLines: Int = if (singleLine) 1 else 5,
    placeholder: String? = null,
    keyboardType: KeyboardType = KeyboardType.Text,
    visualTransformation: VisualTransformation = VisualTransformation.None,
    trailing: (@Composable () -> Unit)? = null,
) {
    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        modifier = modifier.fillMaxWidth(),
        label = { Text(label) },
        placeholder = placeholder?.let { text -> ({ Text(text, maxLines = 2) }) },
        singleLine = singleLine,
        minLines = minLines,
        maxLines = maxLines,
        keyboardOptions = KeyboardOptions(keyboardType = keyboardType),
        visualTransformation = visualTransformation,
        trailingIcon = trailing,
    )
}

@Composable
private fun SectionTitle(title: String) {
    Text(title, color = TextPrimary, fontWeight = FontWeight.Bold, fontSize = 17.sp)
}

@Composable
private fun CardSurface(content: @Composable ColumnScope.() -> Unit) {
    Card(
        Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(20.dp),
        colors = CardDefaults.cardColors(containerColor = CardBackground),
        elevation = CardDefaults.cardElevation(defaultElevation = 1.dp),
    ) { Column(Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp), content = content) }
}

@Composable
private fun ServiceStatusDot(enabled: Boolean) {
    Spacer(Modifier.size(10.dp).background(if (enabled) Color(0xFF34A66A) else Color(0xFFADB6C2), CircleShape))
}

@Composable
private fun DividerLine() {
    Spacer(Modifier.fillMaxWidth().height(1.dp).background(Color(0xFFE9EDF2)))
}

@Composable
private fun HintText(message: String) {
    Text(message, color = TextSecondary, fontSize = 12.sp, lineHeight = 18.sp)
}

private fun openExternalLink(context: Context, url: String) {
    runCatching { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
}
