using Avalonia.Controls;
using Avalonia.Input;
using CursorTranslator.Models;

namespace CursorTranslator.Services;

public partial class AiConnectionProfilesWindow : Window
{
    private readonly List<AiConnectionProfile> _profiles;
    private bool _loading = true;
    private bool _apiKeyVisible;
    private string _previousProfileId = "";

    public IReadOnlyList<AiConnectionProfile> Profiles => _profiles;
    public string SelectedProfileId => (ProfileListBox.SelectedItem as AiConnectionProfile)?.Id ?? "";

    private void WindowSurface_PointerPressed(object? sender, PointerPressedEventArgs e)
        => WindowChrome.BeginMoveDrag(this, e);

    public AiConnectionProfilesWindow() : this([], "") { }

    public AiConnectionProfilesWindow(IEnumerable<AiConnectionProfile> profiles, string activeProfileId)
    {
        InitializeComponent();
        _profiles = profiles.Select(profile => profile.Copy()).ToList();
        if (_profiles.Count == 0) _profiles.Add(AiConnectionProfile.CreateDefault());

        ProfileListBox.ItemsSource = _profiles;
        ProfileListBox.SelectedItem = _profiles.FirstOrDefault(profile => profile.Id == activeProfileId) ?? _profiles[0];
        LoadSelectedProfile();
    }

    private void ProfileList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileListBox.SelectedItem is not AiConnectionProfile selected
            || selected.Id == _previousProfileId) return;

        if (!SaveCurrentToModel())
        {
            _loading = true;
            ProfileListBox.SelectedItem = _profiles.FirstOrDefault(profile => profile.Id == _previousProfileId);
            _loading = false;
            return;
        }

        RefreshProfileList(selected.Id);
        LoadSelectedProfile();
    }

    private void LoadSelectedProfile()
    {
        if (ProfileListBox.SelectedItem is not AiConnectionProfile profile) return;
        _loading = true;
        _previousProfileId = profile.Id;
        ProfileNameBox.Text = profile.Name;
        EndpointBox.Text = profile.Endpoint;
        ModelBox.Text = profile.Model;
        ProfileApiKeyBox.Text = profile.ApiKey;
        _apiKeyVisible = false;
        ProfileApiKeyBox.PasswordChar = '●';
        ApiKeyVisibilityButton.Content = "显示";
        ClearError();
        _loading = false;
    }

    private bool SaveCurrentToModel()
    {
        var profile = GetCurrentProfile();
        if (profile is null) return true;

        var name = ProfileNameBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            SetError("请先填写接口名称。");
            return false;
        }

        profile.Name = name;
        profile.Endpoint = EndpointBox.Text?.Trim() ?? "";
        profile.Model = ModelBox.Text?.Trim() ?? "";
        profile.ApiKey = ProfileApiKeyBox.Text?.Trim() ?? "";
        return true;
    }

    private void RefreshProfileList(string selectedId)
    {
        _loading = true;
        ProfileListBox.ItemsSource = null;
        ProfileListBox.ItemsSource = _profiles;
        ProfileListBox.SelectedItem = _profiles.FirstOrDefault(profile => profile.Id == selectedId);
        _loading = false;
    }

    private AiConnectionProfile? GetCurrentProfile()
        => _profiles.FirstOrDefault(profile => profile.Id == _previousProfileId);

    private void NewProfile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!SaveCurrentToModel()) return;
        var profile = AiConnectionProfile.CreateDefault();
        profile.Name = $"AI 接口 {_profiles.Count + 1}";
        profile.Model = "";
        profile.ApiKey = "";
        _profiles.Add(profile);
        RefreshProfileList(profile.Id);
        LoadSelectedProfile();
    }

    private async void DeleteProfile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ProfileListBox.SelectedItem is not AiConnectionProfile target) return;
        var confirmed = await new ConfirmActionDialog(
            "删除 AI 接口",
            $"确定删除“{target.Name}”吗？删除后将无法恢复。",
            "删除").ShowDialog<bool>(this);
        if (!confirmed) return;

        if (target.Id != _previousProfileId && !SaveCurrentToModel()) return;
        _profiles.RemoveAll(profile => profile.Id == target.Id);
        if (_profiles.Count == 0)
        {
            var replacement = AiConnectionProfile.CreateDefault();
            replacement.Name = "默认 AI 接口";
            _profiles.Add(replacement);
        }

        var nextProfileId = target.Id == _previousProfileId
            ? _profiles[0].Id
            : _previousProfileId;
        RefreshProfileList(nextProfileId);
        LoadSelectedProfile();
    }

    private void ToggleApiKeyVisibility_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _apiKeyVisible = !_apiKeyVisible;
        ProfileApiKeyBox.PasswordChar = _apiKeyVisible ? '\0' : '●';
        ApiKeyVisibilityButton.Content = _apiKeyVisible ? "隐藏" : "显示";
    }

    private void Save_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!SaveCurrentToModel()) return;
        Close(true);
    }

    private void Cancel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);
    private void SetError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void ClearError()
    {
        ErrorText.Text = "";
        ErrorText.IsVisible = false;
    }
}
