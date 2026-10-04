using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using CursorTranslator.Models;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CursorTranslator.Services;

public partial class SpeechHttpProfilesWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly List<SpeechHttpProfile> _profiles;
    // XAML controls raise SelectionChanged while InitializeComponent is still building the tree.
    private bool _loading = true;
    private bool _profileApiKeyVisible;
    private bool _profileApiSecretVisible;
    private int _lastTabIndex;
    private string _previousProfileId = "";

    public IReadOnlyList<SpeechHttpProfile> Profiles => _profiles;
    public string SelectedProfileId => (ProfileListBox.SelectedItem as SpeechHttpProfile)?.Id ?? "";

    public SpeechHttpProfilesWindow() : this([], "") { }

    public SpeechHttpProfilesWindow(IEnumerable<SpeechHttpProfile> profiles, string activeProfileId)
    {
        InitializeComponent();
        _profiles = profiles.Select(profile => profile.Copy()).ToList();
        if (_profiles.Count == 0) _profiles.Add(SpeechHttpProfile.CreateDefault());

        ProfileListBox.ItemsSource = _profiles;
        ProfileListBox.SelectedItem = _profiles.FirstOrDefault(profile => profile.Id == activeProfileId) ?? _profiles[0];
        LoadSelectedProfile();
    }

    private void ProfileList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileListBox.SelectedItem is not SpeechHttpProfile selected
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
            if (GetCurrentProfile() is { } profile)
                LoadBasicFields(profile);
        }

        _lastTabIndex = EditorTabs.SelectedIndex;
        ClearError();
    }

    private void LoadSelectedProfile()
    {
        if (ProfileListBox.SelectedItem is not SpeechHttpProfile profile) return;
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

    private void LoadBasicFields(SpeechHttpProfile profile)
    {
        ProfileNameBox.Text = profile.Name;
        EdgeTtsNoticeText.IsVisible = profile.IsEdgeTts;
        EndpointBox.Text = profile.Request.Url;
        ModelBox.Text = profile.Model;
        VoiceBox.Text = profile.Voice;
        TimeoutBox.Text = profile.Request.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        AuthHeaderNameBox.Text = profile.Auth.HeaderName;
        AuthQueryNameBox.Text = profile.Auth.QueryName;
        AuthUsernameBox.Text = profile.Auth.Username;
        ResponseJsonPathBox.Text = profile.Response.JsonPath;
        SelectByTag(MethodComboBox, profile.Request.Method, "POST");
        SelectByTag(AuthTypeComboBox, profile.Auth.Type, "None");
        SelectByTag(ResponseTypeComboBox, profile.Response.Type, "RawAudio");
        SelectByTag(ResponseFormatComboBox, profile.Response.Format, "Wav");
    }

    private bool SaveCurrentToModel(bool showError, bool refreshList = true)
    {
        if (_lastTabIndex == 1)
            return ApplyJsonToModel(showError, refreshList);
        return ApplyBasicToModel(showError, refreshList);
    }

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
        current.Model = ModelBox.Text?.Trim() ?? "";
        current.Voice = VoiceBox.Text?.Trim() ?? "";
        current.Request.Url = EndpointBox.Text?.Trim() ?? "";
        current.Request.Method = SelectedTag(MethodComboBox, "POST");
        current.Request.TimeoutSeconds = timeout;
        current.Auth.Type = SelectedTag(AuthTypeComboBox, "None");
        current.Auth.HeaderName = string.IsNullOrWhiteSpace(AuthHeaderNameBox.Text)
            ? "Authorization" : AuthHeaderNameBox.Text.Trim();
        current.Auth.QueryName = string.IsNullOrWhiteSpace(AuthQueryNameBox.Text)
            ? "api_key" : AuthQueryNameBox.Text.Trim();
        current.Auth.Username = AuthUsernameBox.Text?.Trim() ?? "";
        current.ApiKey = ProfileApiKeyBox.Text?.Trim() ?? "";
        current.ApiSecret = ProfileApiSecretBox.Text?.Trim() ?? "";
        current.Response.Type = SelectedTag(ResponseTypeComboBox, "RawAudio");
        current.Response.Format = SelectedTag(ResponseFormatComboBox, "Wav");
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
            var parsed = JsonSerializer.Deserialize<SpeechHttpProfile>(ProfileJsonBox.Text ?? "", JsonOptions)
                ?? throw new InvalidOperationException("档案 JSON 不能为空。");
            Normalize(parsed);
            if (string.IsNullOrWhiteSpace(parsed.Name))
                throw new InvalidOperationException("档案名称不能为空。");

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
            AppLog.Warning("Speech HTTP profile", "Profile JSON validation failed.", ex);
            if (showError) SetError($"接口档案 JSON 有误：{ex.Message}");
            return false;
        }
    }

    private static void Normalize(SpeechHttpProfile profile)
    {
        profile.Name ??= "";
        profile.Engine ??= "Http";
        profile.Model ??= "";
        profile.Voice ??= "";
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
        profile.Response ??= new SpeechHttpResponseProfile();
        profile.Response.Type ??= "RawAudio";
        profile.Response.Format ??= "Wav";
    }

    private static void CopyInto(SpeechHttpProfile target, SpeechHttpProfile source)
    {
        target.Name = source.Name;
        target.Engine = source.Engine;
        target.Model = source.Model;
        target.Voice = source.Voice;
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

    private SpeechHttpProfile? GetCurrentProfile() =>
        _profiles.FirstOrDefault(profile => profile.Id == _previousProfileId);

    private void NewProfile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!SaveCurrentToModel(showError: true)) return;
        var profile = SpeechHttpProfile.CreateDefault();
        profile.Name = $"HTTP 接口 {_profiles.Count + 1}";
        _profiles.Add(profile);
        RefreshProfileList(profile.Id);
        LoadSelectedProfile();
    }

    private async void DeleteProfile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var target = sender is Avalonia.Controls.Control control && control.DataContext is SpeechHttpProfile item
            ? item
            : ProfileListBox.SelectedItem as SpeechHttpProfile;
        if (target is null) return;

        var confirmed = await new ConfirmActionDialog(
            "删除接口档案",
            $"确定删除“{target.Name}”吗？删除后将无法恢复。",
            "删除").ShowDialog<bool>(this);
        if (!confirmed) return;
        if (target.Id != _previousProfileId && !SaveCurrentToModel(showError: true)) return;

        var activeProfileId = _previousProfileId;
        _profiles.RemoveAll(profile => profile.Id == target.Id);
        if (_profiles.Count == 0)
            _profiles.Add(SpeechHttpProfile.CreateDefault());

        var activeStillExists = _profiles.Any(profile => profile.Id == activeProfileId);
        if (!activeStillExists)
        {
            activeProfileId = _profiles[0].Id;
            _previousProfileId = activeProfileId;
        }

        RefreshProfileList(activeProfileId);
        if (!activeStillExists)
            LoadSelectedProfile();
    }

    private async void PresetComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not Avalonia.Controls.ComboBox comboBox) return;
        var key = SelectedTag(comboBox, "");
        if (string.IsNullOrWhiteSpace(key)) return;

        var current = GetCurrentProfile();
        if (current is null) return;
        var currentName = ProfileNameBox.Text?.Trim() ?? current.Name;
        var apiKey = ProfileApiKeyBox.Text?.Trim() ?? current.ApiKey;
        var apiSecret = ProfileApiSecretBox.Text?.Trim() ?? current.ApiSecret;

        SpeechHttpProfile preset;
        switch (key)
        {
            case "GenericPost":
                preset = new SpeechHttpProfile
                {
                    Model = "",
                    Voice = "",
                    Request = new SpeechHttpRequestProfile
                    {
                        Url = "http://127.0.0.1:8000/tts",
                        Method = "POST",
                        Body = JsonNode.Parse("""{"text":"{{text}}"}""")
                    },
                    Auth = new SpeechHttpAuthProfile { Type = "None" },
                    Response = new SpeechHttpResponseProfile { Type = "RawAudio", Format = "Wav" }
                };
                break;
            case "GenericGet":
                preset = new SpeechHttpProfile
                {
                    Request = new SpeechHttpRequestProfile
                    {
                        Url = "http://127.0.0.1:8000/tts",
                        Method = "GET",
                        Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["text"] = "{{text}}",
                            ["voice"] = "{{voice}}"
                        }
                    },
                    Auth = new SpeechHttpAuthProfile { Type = "ApiKeyHeader", HeaderName = "X-API-Key" },
                    Response = new SpeechHttpResponseProfile
                    {
                        Type = "JsonBase64OrUrl", Format = "Mp3", JsonPath = ""
                    }
                };
                break;
            case "Hewoyi":
                preset = new SpeechHttpProfile
                {
                    Engine = "Http",
                    Name = "合我意预设",
                    Model = "",
                    Voice = "",
                    Request = new SpeechHttpRequestProfile
                    {
                        Url = "https://api.hewoyi.com/api/ai/audio/speech",
                        Method = "GET",
                        TimeoutSeconds = 60,
                        Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["text"] = "{{text}}",
                            ["voice"] = "{{voice}}",
                            ["format"] = "",
                            ["speed"] = "",
                            ["model"] = "{{model}}",
                            ["type"] = "speech"
                        }
                    },
                    Auth = new SpeechHttpAuthProfile { Type = "ApiKeyQuery", QueryName = "key" },
                    Response = new SpeechHttpResponseProfile
                    {
                        Type = "RawAudio", Format = "Mp3", JsonPath = ""
                    }
                };
                break;
            case "EdgeTts":
                preset = new SpeechHttpProfile
                {
                    Name = "Microsoft Edge TTS",
                    Engine = "EdgeTts",
                    Voice = "zh-CN-XiaoxiaoNeural",
                    Request = new SpeechHttpRequestProfile
                    {
                        // Edge TTS uses its built-in WebSocket transport; this URL is intentionally empty.
                        Url = "",
                        Method = "GET",
                        TimeoutSeconds = 90
                    },
                    Auth = new SpeechHttpAuthProfile { Type = "None" },
                    Response = new SpeechHttpResponseProfile { Type = "RawAudio", Format = "Mp3" }
                };
                break;
            default:
                SetError("请先从列表中选择一个预设。");
                return;
        }

        current.Model = preset.Model;
        current.Voice = preset.Voice;
        current.Name = string.IsNullOrWhiteSpace(preset.Name) ? currentName : preset.Name;
        current.Engine = preset.Engine;
        current.Request = preset.Request;
        current.Auth = preset.Auth;
        current.Workflow = new SpeechHttpWorkflowProfile();
        current.Response = preset.Response;
        // Keep the user's credential values when a preset changes the protocol basics.
        current.ApiKey = apiKey;
        current.ApiSecret = apiSecret;
        RefreshProfileList(current.Id);
        _loading = true;
        LoadBasicFields(current);
        ProfileApiKeyBox.Text = current.ApiKey;
        ProfileApiSecretBox.Text = current.ApiSecret;
        ProfileJsonBox.Text = JsonSerializer.Serialize(current, JsonOptions);
        var message = key switch
        {
            "GenericPost" => "已填入通用 POST JSON / WAV 基础配置，请填写接口地址及所需鉴权信息。",
            "GenericGet" => "已填入通用 GET 参数 / JSON 音频基础配置，请填写接口地址及所需鉴权信息。",
            "Hewoyi" => "已填入合我意预设，现在只需要你填入API Key即可使用",
            "EdgeTts" => "已填入 Microsoft Edge TTS 预设，无需 API Key；默认音色为 zh-CN-XiaoxiaoNeural，可在音色栏修改。",
            _ => "选择预设即可自动填入基础配置；API Key 等账号信息由你自己填写。"
        };
        _loading = false;
        ClearError();
        await new PresetAppliedDialog(message).ShowDialog(this);
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
            AppLog.Error("Speech HTTP profile", "Failed to copy a profile to the clipboard.", ex);
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
            AppLog.Error("Speech HTTP profile", "Failed to import a profile from the clipboard.", ex);
            SetError($"导入失败：{ex.Message}");
        }
    }

    private void Save_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!SaveCurrentToModel(showError: true)) return;
        Close(true);
    }

    private void Cancel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);
    private void CloseWindow_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);
    private void Minimize_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ToggleProfileApiKeyVisibility_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _profileApiKeyVisible = !_profileApiKeyVisible;
        ProfileApiKeyBox.PasswordChar = _profileApiKeyVisible ? '\0' : '●';
        ProfileApiKeyEyeSlash.IsVisible = _profileApiKeyVisible;
        Avalonia.Controls.ToolTip.SetTip(
            ProfileApiKeyVisibilityButton,
            _profileApiKeyVisible ? "隐藏 API Key" : "显示 API Key");
    }

    private void ToggleProfileApiSecretVisibility_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _profileApiSecretVisible = !_profileApiSecretVisible;
        ProfileApiSecretBox.PasswordChar = _profileApiSecretVisible ? '\0' : '●';
        ProfileApiSecretEyeSlash.IsVisible = _profileApiSecretVisible;
        Avalonia.Controls.ToolTip.SetTip(
            ProfileApiSecretVisibilityButton,
            _profileApiSecretVisible ? "隐藏 API Secret" : "显示 API Secret");
    }

    private void WindowSurface_PointerPressed(object? sender, PointerPressedEventArgs e)
        => WindowChrome.BeginMoveDrag(this, e);

    private void Maximize_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        var maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.IsVisible = !maximized;
        RestoreIcon.IsVisible = maximized;
        Avalonia.Controls.ToolTip.SetTip(MaximizeButton, maximized ? "还原" : "最大化");
    }

    private static string SelectedTag(Avalonia.Controls.ComboBox comboBox, string fallback)
    {
        return (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;
    }

    private static void SelectByTag(Avalonia.Controls.ComboBox comboBox, string tag, string fallback)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            ?? comboBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), fallback, StringComparison.OrdinalIgnoreCase));
    }

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
