using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using CursorTranslator.Models;
using System.Globalization;
using System.Text.Json;

namespace CursorTranslator.Services;

public partial class TranslationHttpProfilesWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly List<TranslationHttpProfile> _profiles;
    private bool _loading = true;
    private bool _profileApiKeyVisible;
    private bool _profileApiSecretVisible;
    private int _lastTabIndex;
    private string _previousProfileId = "";

    private void WindowSurface_PointerPressed(object? sender, PointerPressedEventArgs e)
        => WindowChrome.BeginMoveDrag(this, e);

    public IReadOnlyList<TranslationHttpProfile> Profiles => _profiles;
    public string SelectedProfileId => (ProfileListBox.SelectedItem as TranslationHttpProfile)?.Id ?? "";

    public TranslationHttpProfilesWindow() : this([], "") { }

    public TranslationHttpProfilesWindow(IEnumerable<TranslationHttpProfile> profiles, string activeProfileId)
    {
        InitializeComponent();
        _profiles = profiles.Select(profile => profile.Copy()).ToList();
        if (_profiles.Count == 0) _profiles.Add(TranslationHttpProfile.CreateDefault());

        ProfileListBox.ItemsSource = _profiles;
        ProfileListBox.SelectedItem = _profiles.FirstOrDefault(profile => profile.Id == activeProfileId) ?? _profiles[0];
        LoadSelectedProfile();
    }

    private void ProfileList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileListBox.SelectedItem is not TranslationHttpProfile selected
            || selected.Id == _previousProfileId) return;

        if (!SaveCurrentToModel(showError: true, refreshList: false))
        {
            _loading = true;
            ProfileListBox.SelectedItem = _profiles.FirstOrDefault(profile => profile.Id == _previousProfileId);
            _loading = false;
            return;
        }

        RefreshProfileList(selected.Id);
        _previousProfileId = selected.Id;
        LoadSelectedProfile();
    }

    private void EditorTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || EditorTabs is null || EditorTabs.SelectedIndex == _lastTabIndex) return;
        if (_lastTabIndex == 0)
        {
            if (!ApplyBasicToModel(showError: true))
            {
                _loading = true;
                EditorTabs.SelectedIndex = _lastTabIndex;
                _loading = false;
                return;
            }
            ProfileJsonBox.Text = JsonSerializer.Serialize(GetCurrentProfile(), JsonOptions);
        }
        else
        {
            if (!ApplyJsonToModel(showError: true))
            {
                _loading = true;
                EditorTabs.SelectedIndex = _lastTabIndex;
                _loading = false;
                return;
            }
            if (GetCurrentProfile() is { } profile) LoadBasicFields(profile);
        }

        _lastTabIndex = EditorTabs.SelectedIndex;
        ClearError();
    }

    private void LoadSelectedProfile()
    {
        if (ProfileListBox.SelectedItem is not TranslationHttpProfile profile) return;
        _loading = true;
        _previousProfileId = profile.Id;
        _lastTabIndex = 0;
        EditorTabs.SelectedIndex = 0;
        PresetComboBox.SelectedIndex = 0;
        ProfileJsonBox.Text = JsonSerializer.Serialize(profile, JsonOptions);
        ProfileApiKeyBox.Text = profile.ApiKey;
        ProfileApiSecretBox.Text = profile.ApiSecret;
        _profileApiKeyVisible = false;
        ProfileApiKeyBox.PasswordChar = '●';
        ProfileApiKeyEyeSlash.IsVisible = false;
        Avalonia.Controls.ToolTip.SetTip(ProfileApiKeyVisibilityButton, "显示 API Key");
        _profileApiSecretVisible = false;
        ProfileApiSecretBox.PasswordChar = '●';
        ProfileApiSecretEyeSlash.IsVisible = false;
        Avalonia.Controls.ToolTip.SetTip(ProfileApiSecretVisibilityButton, "显示 API Secret");
        LoadBasicFields(profile);
        ClearError();
        _loading = false;
    }

    private void LoadBasicFields(TranslationHttpProfile profile)
    {
        ProfileNameBox.Text = profile.Name;
        EndpointBox.Text = profile.Request.Url;
        TimeoutBox.Text = profile.Request.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        SourceLanguageBox.Text = profile.SourceLanguage;
        TargetLanguageBox.Text = profile.TargetLanguage;
        AuthHeaderNameBox.Text = profile.Auth.HeaderName;
        AuthQueryNameBox.Text = profile.Auth.QueryName;
        AuthUsernameBox.Text = profile.Auth.Username;
        ResponseJsonPathBox.Text = profile.Response.JsonPath;
        SelectByTag(MethodComboBox, profile.Request.Method, "POST");
        SelectByTag(AuthTypeComboBox, profile.Auth.Type, "None");
        SelectByTag(ResponseTypeComboBox, profile.Response.Type, "JsonText");
    }

    private bool SaveCurrentToModel(bool showError, bool refreshList = true)
        => _lastTabIndex == 1
            ? ApplyJsonToModel(showError, refreshList)
            : ApplyBasicToModel(showError, refreshList);

    private bool ApplyBasicToModel(bool showError, bool refreshList = true)
    {
        var current = GetCurrentProfile();
        if (current is null) return true;
        var name = ProfileNameBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            if (showError) SetError("请给这个接口档案填写一个名称。");
            return false;
        }
        if (!int.TryParse(TimeoutBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout)
            || timeout is < 1 or > 600)
        {
            if (showError) SetError("超时秒数请填写 1 到 600 之间的整数。");
            return false;
        }

        current.Name = name;
        current.Request.Url = EndpointBox.Text?.Trim() ?? "";
        current.Request.Method = SelectedTag(MethodComboBox, "POST");
        current.Request.TimeoutSeconds = timeout;
        current.SourceLanguage = SourceLanguageBox.Text?.Trim() ?? "";
        current.TargetLanguage = TargetLanguageBox.Text?.Trim() ?? "";
        current.Auth.Type = SelectedTag(AuthTypeComboBox, "None");
        current.Auth.HeaderName = string.IsNullOrWhiteSpace(AuthHeaderNameBox.Text) ? "Authorization" : AuthHeaderNameBox.Text.Trim();
        current.Auth.QueryName = string.IsNullOrWhiteSpace(AuthQueryNameBox.Text) ? "api_key" : AuthQueryNameBox.Text.Trim();
        current.Auth.Username = AuthUsernameBox.Text?.Trim() ?? "";
        current.ApiKey = ProfileApiKeyBox.Text?.Trim() ?? "";
        current.ApiSecret = ProfileApiSecretBox.Text?.Trim() ?? "";
        current.Response.Type = SelectedTag(ResponseTypeComboBox, "JsonText");
        current.Response.JsonPath = ResponseJsonPathBox.Text?.Trim() ?? "";

        if (refreshList) RefreshProfileList(current.Id);
        return true;
    }

    private bool ApplyJsonToModel(bool showError, bool refreshList = true)
    {
        var current = GetCurrentProfile();
        if (current is null) return true;
        try
        {
            var parsed = JsonSerializer.Deserialize<TranslationHttpProfile>(ProfileJsonBox.Text ?? "", JsonOptions)
                ?? throw new InvalidOperationException("档案 JSON 不能为空。");
            Normalize(parsed);
            if (string.IsNullOrWhiteSpace(parsed.Name)) throw new InvalidOperationException("档案名称不能为空。");
            parsed.Id = current.Id;
            parsed.ApiKey = ProfileApiKeyBox.Text?.Trim() ?? "";
            parsed.ApiSecret = ProfileApiSecretBox.Text?.Trim() ?? "";
            CopyInto(current, parsed);
            if (refreshList) RefreshProfileList(current.Id);
            ProfileJsonBox.Text = JsonSerializer.Serialize(current, JsonOptions);
            ClearError();
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            AppLog.Warning("Translation HTTP profile", "Profile JSON validation failed.", ex);
            if (showError) SetError($"接口档案 JSON 有误：{ex.Message}");
            return false;
        }
    }

    private static void Normalize(TranslationHttpProfile profile)
    {
        profile.Id ??= Guid.NewGuid().ToString("N");
        profile.Name ??= "";
        profile.SourceLanguage ??= "auto";
        profile.TargetLanguage ??= "en";
        profile.Request ??= new SpeechHttpRequestProfile();
        profile.Request.Url ??= "";
        profile.Request.Method ??= "POST";
        profile.Request.Headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        profile.Request.Query ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        profile.Auth ??= new SpeechHttpAuthProfile();
        profile.Auth.Type ??= "None";
        profile.Workflow ??= new SpeechHttpWorkflowProfile();
        profile.Workflow.Type ??= "Sync";
        profile.Workflow.QueryRequest ??= new SpeechHttpRequestProfile();
        profile.Workflow.QueryRequest.Url ??= "";
        profile.Workflow.QueryRequest.Method ??= "POST";
        profile.Workflow.QueryRequest.Headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        profile.Workflow.QueryRequest.Query ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        profile.Response ??= new TranslationHttpResponseProfile();
        profile.Response.Type ??= "JsonText";
        profile.Response.JsonPath ??= "";
    }

    private static void CopyInto(TranslationHttpProfile target, TranslationHttpProfile source)
    {
        target.Name = source.Name;
        target.SourceLanguage = source.SourceLanguage;
        target.TargetLanguage = source.TargetLanguage;
        target.Request = source.Request;
        target.Auth = source.Auth;
        target.Workflow = source.Workflow;
        target.Response = source.Response;
        target.ApiKey = source.ApiKey;
        target.ApiSecret = source.ApiSecret;
    }

    private void RefreshProfileList(string selectedId)
    {
        _loading = true;
        ProfileListBox.ItemsSource = null;
        ProfileListBox.ItemsSource = _profiles;
        ProfileListBox.SelectedItem = _profiles.FirstOrDefault(profile => profile.Id == selectedId);
        _loading = false;
    }

    private TranslationHttpProfile? GetCurrentProfile() => _profiles.FirstOrDefault(profile => profile.Id == _previousProfileId);

    private void NewProfile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!SaveCurrentToModel(showError: true)) return;
        var profile = TranslationHttpProfile.CreateDefault();
        profile.Name = $"HTTP 翻译接口 {_profiles.Count + 1}";
        _profiles.Add(profile);
        RefreshProfileList(profile.Id);
        LoadSelectedProfile();
    }

    private async void DeleteProfile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var target = sender is Avalonia.Controls.Control control && control.DataContext is TranslationHttpProfile item
            ? item
            : ProfileListBox.SelectedItem as TranslationHttpProfile;
        if (target is null) return;
        var confirmed = await new ConfirmActionDialog(
            "删除接口档案",
            $"确定删除“{target.Name}”吗？删除后将无法恢复。",
            "删除").ShowDialog<bool>(this);
        if (!confirmed) return;
        if (target.Id != _previousProfileId && !SaveCurrentToModel(showError: true)) return;

        var activeId = _previousProfileId;
        _profiles.RemoveAll(profile => profile.Id == target.Id);
        if (_profiles.Count == 0) _profiles.Add(TranslationHttpProfile.CreateDefault());
        var activeStillExists = _profiles.Any(profile => profile.Id == activeId);
        if (!activeStillExists)
        {
            activeId = _profiles[0].Id;
            _previousProfileId = activeId;
        }
        RefreshProfileList(activeId);
        if (!activeStillExists) LoadSelectedProfile();
    }

    private async void PresetComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not Avalonia.Controls.ComboBox comboBox) return;
        var key = SelectedTag(comboBox, "");
        if (string.IsNullOrWhiteSpace(key)) return;
        var current = GetCurrentProfile();
        if (current is null) return;

        var apiKey = ProfileApiKeyBox.Text?.Trim() ?? current.ApiKey;
        var apiSecret = ProfileApiSecretBox.Text?.Trim() ?? current.ApiSecret;
        var originalName = ProfileNameBox.Text?.Trim() ?? current.Name;
        var preset = key switch
        {
            "GenericPost" => TranslationHttpProfile.CreateDefault(),
            "GenericGet" => CreateGenericGetPreset(),
            "NiuTrans" => TranslationHttpProfile.CreateNiuTrans(),
            _ => null
        };
        if (preset is null) return;

        var id = current.Id;
        var presetName = preset.Name;
        CopyInto(current, preset);
        current.Id = id;
        if (key is "GenericPost" or "GenericGet") current.Name = string.IsNullOrWhiteSpace(originalName) ? "通用 HTTP 翻译接口" : originalName;
        current.ApiKey = apiKey;
        current.ApiSecret = apiSecret;
        RefreshProfileList(current.Id);
        _loading = true;
        LoadBasicFields(current);
        ProfileApiKeyBox.Text = current.ApiKey;
        ProfileApiSecretBox.Text = current.ApiSecret;
        ProfileJsonBox.Text = JsonSerializer.Serialize(current, JsonOptions);
        PresetComboBox.SelectedIndex = 0;
        _loading = false;
        ClearError();
        var message = key switch
        {
            "NiuTrans" => "已填入小牛翻译预设，现在只需要你填入 API Key 即可使用。",
            "GenericGet" => "已填入通用 GET 参数预设；请按服务文档补充请求参数和响应字段。",
            _ => "已填入通用 POST JSON 预设；请按服务文档配置地址、请求体和响应字段。"
        };
        await new PresetAppliedDialog(message).ShowDialog(this);
    }

    private static TranslationHttpProfile CreateGenericGetPreset()
    {
        var profile = TranslationHttpProfile.CreateDefault();
        profile.Name = "通用 HTTP GET 接口";
        profile.Request.Url = "https://example.com/translate";
        profile.Request.Method = "GET";
        profile.Request.Body = null;
        profile.Request.Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["text"] = "{{text}}",
            ["source"] = "{{sourceLanguage}}",
            ["target"] = "{{targetLanguage}}"
        };
        return profile;
    }

    private async void CopyProfileJson_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) throw new InvalidOperationException("当前窗口没有可用的剪贴板。");
            await clipboard.SetTextAsync(ProfileJsonBox.Text ?? "");
            ClearError();
        }
        catch (Exception ex)
        {
            AppLog.Error("Translation HTTP profile", "Failed to copy a profile to the clipboard.", ex);
            SetError($"复制失败：{ex.Message}");
        }
    }

    private async void PasteProfileJson_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) throw new InvalidOperationException("当前窗口没有可用的剪贴板。");
            var text = await clipboard.TryGetTextAsync() ?? "";
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("剪贴板里没有 JSON 文本。");
            ProfileJsonBox.Text = text;
            ClearError();
        }
        catch (Exception ex)
        {
            AppLog.Error("Translation HTTP profile", "Failed to import a profile from the clipboard.", ex);
            SetError($"导入失败：{ex.Message}");
        }
    }

    private void Save_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!SaveCurrentToModel(showError: true)) return;
        Close(true);
    }

    private void Cancel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);
    private void ToggleProfileApiKeyVisibility_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _profileApiKeyVisible = !_profileApiKeyVisible;
        ProfileApiKeyBox.PasswordChar = _profileApiKeyVisible ? '\0' : '●';
        ProfileApiKeyEyeSlash.IsVisible = _profileApiKeyVisible;
        Avalonia.Controls.ToolTip.SetTip(ProfileApiKeyVisibilityButton, _profileApiKeyVisible ? "隐藏 API Key" : "显示 API Key");
    }

    private void ToggleProfileApiSecretVisibility_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _profileApiSecretVisible = !_profileApiSecretVisible;
        ProfileApiSecretBox.PasswordChar = _profileApiSecretVisible ? '\0' : '●';
        ProfileApiSecretEyeSlash.IsVisible = _profileApiSecretVisible;
        Avalonia.Controls.ToolTip.SetTip(ProfileApiSecretVisibilityButton, _profileApiSecretVisible ? "隐藏 API Secret" : "显示 API Secret");
    }

    private static string SelectedTag(Avalonia.Controls.ComboBox comboBox, string fallback)
        => (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static void SelectByTag(Avalonia.Controls.ComboBox comboBox, string tag, string fallback)
        => comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                string.Equals(item.Tag?.ToString(), fallback, StringComparison.OrdinalIgnoreCase));

    private void ClearError()
    {
        FooterErrorText.Text = "";
        FooterErrorText.IsVisible = false;
        ProfileErrorText.Text = "";
        ProfileErrorText.IsVisible = false;
    }

    private void SetError(string message)
    {
        FooterErrorText.Text = message;
        FooterErrorText.IsVisible = true;
        ProfileErrorText.Text = message;
        ProfileErrorText.IsVisible = EditorTabs.SelectedIndex == 1;
    }
}
