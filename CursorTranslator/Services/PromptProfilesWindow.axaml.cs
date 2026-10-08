using Avalonia.Controls;
using Avalonia.Input;
using CursorTranslator.Models;

namespace CursorTranslator.Services;

public partial class PromptProfilesWindow : Window
{
    private readonly List<PromptProfile> _translationProfiles;
    private readonly List<PromptProfile> _analysisProfiles;
    private readonly List<PromptProfile> _selectedTranslationTermProfiles;
    private bool _loading = true;
    private int _currentKindIndex;
    private string _previousProfileId = "";
    private string _translationSelectedId = "";
    private string _analysisSelectedId = "";
    private string _selectedTranslationTermSelectedId = "";

    public IReadOnlyList<PromptProfile> TranslationProfiles => _translationProfiles;
    public IReadOnlyList<PromptProfile> AnalysisProfiles => _analysisProfiles;
    public IReadOnlyList<PromptProfile> SelectedTranslationTermProfiles => _selectedTranslationTermProfiles;

    private void WindowSurface_PointerPressed(object? sender, PointerPressedEventArgs e)
        => WindowChrome.BeginMoveDrag(this, e);

    public PromptProfilesWindow() : this([], "", [], "", [], "") { }

    public PromptProfilesWindow(
        IEnumerable<PromptProfile> translationProfiles,
        string activeTranslationProfileId,
        IEnumerable<PromptProfile> analysisProfiles,
        string activeAnalysisProfileId,
        IEnumerable<PromptProfile> selectedTranslationTermProfiles,
        string activeSelectedTranslationTermProfileId)
    {
        InitializeComponent();
        _translationProfiles = translationProfiles.Select(profile => profile.Copy()).ToList();
        _analysisProfiles = analysisProfiles.Select(profile => profile.Copy()).ToList();
        _selectedTranslationTermProfiles = selectedTranslationTermProfiles.Select(profile => profile.Copy()).ToList();
        if (_translationProfiles.Count == 0)
            _translationProfiles.Add(CreateDefaultProfile(0, 1));
        if (_analysisProfiles.Count == 0)
            _analysisProfiles.Add(CreateDefaultProfile(1, 1));
        if (_selectedTranslationTermProfiles.Count == 0)
            _selectedTranslationTermProfiles.Add(CreateDefaultProfile(2, 1));

        _translationSelectedId = GetExistingId(_translationProfiles, activeTranslationProfileId);
        _analysisSelectedId = GetExistingId(_analysisProfiles, activeAnalysisProfileId);
        _selectedTranslationTermSelectedId = GetExistingId(
            _selectedTranslationTermProfiles, activeSelectedTranslationTermProfileId);
        PromptKindComboBox.ItemsSource = new[] { "翻译提示词", "解析提示词", "选词提示词" };
        PromptKindComboBox.SelectedIndex = 0;
        _currentKindIndex = 0;
        RefreshProfileList(_translationSelectedId);
        LoadSelectedProfile();
        _loading = false;
    }

    private List<PromptProfile> CurrentProfiles => _currentKindIndex switch
    {
        0 => _translationProfiles,
        1 => _analysisProfiles,
        _ => _selectedTranslationTermProfiles
    };

    private string CurrentSelectedId => _currentKindIndex switch
    {
        0 => _translationSelectedId,
        1 => _analysisSelectedId,
        _ => _selectedTranslationTermSelectedId
    };

    private void SetCurrentSelectedId(string id)
    {
        switch (_currentKindIndex)
        {
            case 0:
                _translationSelectedId = id;
                break;
            case 1:
                _analysisSelectedId = id;
                break;
            default:
                _selectedTranslationTermSelectedId = id;
                break;
        }
    }

    private static string GetExistingId(IReadOnlyList<PromptProfile> profiles, string requestedId)
        => profiles.FirstOrDefault(profile => profile.Id == requestedId)?.Id ?? profiles[0].Id;

    private void PromptKind_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || PromptKindComboBox.SelectedIndex is not (0 or 1 or 2)
            || PromptKindComboBox.SelectedIndex == _currentKindIndex)
            return;

        var requestedKind = PromptKindComboBox.SelectedIndex;
        if (!SaveCurrentToModel())
        {
            _loading = true;
            PromptKindComboBox.SelectedIndex = _currentKindIndex;
            _loading = false;
            return;
        }

        _currentKindIndex = requestedKind;
        var selectedId = GetExistingId(CurrentProfiles, CurrentSelectedId);
        SetCurrentSelectedId(selectedId);
        RefreshProfileList(selectedId);
        LoadSelectedProfile();
    }

    private void ProfileList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileListBox.SelectedItem is not PromptProfile selected
            || selected.Id == _previousProfileId)
            return;

        if (!SaveCurrentToModel())
        {
            _loading = true;
            ProfileListBox.SelectedItem = CurrentProfiles.FirstOrDefault(profile => profile.Id == _previousProfileId);
            _loading = false;
            return;
        }

        LoadSelectedProfile();
    }

    private void LoadSelectedProfile()
    {
        if (ProfileListBox.SelectedItem is not PromptProfile profile) return;
        _loading = true;
        _previousProfileId = profile.Id;
        SetCurrentSelectedId(profile.Id);
        ProfileNameBox.Text = profile.Name;
        PromptBox.Text = profile.Prompt;
        ClearError();
        _loading = false;
    }

    private bool SaveCurrentToModel()
    {
        var profile = CurrentProfiles.FirstOrDefault(item => item.Id == _previousProfileId);
        if (profile is null) return true;

        var name = ProfileNameBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            SetError("请先填写提示词名称。");
            return false;
        }

        profile.Name = name;
        profile.Prompt = PromptBox.Text ?? "";
        ClearError();
        return true;
    }

    private void RefreshProfileList(string selectedId)
    {
        var wasLoading = _loading;
        _loading = true;
        ProfileListBox.ItemsSource = null;
        ProfileListBox.ItemsSource = CurrentProfiles;
        ProfileListBox.SelectedItem = CurrentProfiles.FirstOrDefault(profile => profile.Id == selectedId)
            ?? CurrentProfiles[0];
        _loading = wasLoading;
    }

    private void NewProfile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!SaveCurrentToModel()) return;
        var profile = CreateDefaultProfile(_currentKindIndex, CurrentProfiles.Count + 1);
        CurrentProfiles.Add(profile);
        SetCurrentSelectedId(profile.Id);
        RefreshProfileList(profile.Id);
        LoadSelectedProfile();
    }

    private async void DeleteProfile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ProfileListBox.SelectedItem is not PromptProfile target) return;
        var confirmed = await new ConfirmActionDialog(
            "删除提示词",
            $"确定删除“{target.Name}”吗？删除后将无法恢复。",
            "删除").ShowDialog<bool>(this);
        if (!confirmed) return;

        if (target.Id != _previousProfileId && !SaveCurrentToModel()) return;
        CurrentProfiles.RemoveAll(profile => profile.Id == target.Id);
        if (CurrentProfiles.Count == 0)
            CurrentProfiles.Add(CreateDefaultProfile(_currentKindIndex, 1));

        var nextId = target.Id == _previousProfileId
            ? CurrentProfiles[0].Id
            : _previousProfileId;
        SetCurrentSelectedId(nextId);
        RefreshProfileList(nextId);
        LoadSelectedProfile();
    }

    private static PromptProfile CreateDefaultProfile(int kindIndex, int number)
        => kindIndex switch
        {
            0 => new PromptProfile
            {
                Name = number == 1 ? "默认翻译提示词" : $"翻译提示词 {number}",
                Prompt = AppSettings.DefaultSystemPrompt
            },
            1 => new PromptProfile
            {
                Name = number == 1 ? "默认解析提示词" : $"解析提示词 {number}",
                Prompt = AppSettings.DefaultDeepAnalysisPrompt
            },
            _ => new PromptProfile
            {
                Name = number == 1 ? "默认选词提示词" : $"选词提示词 {number}",
                Prompt = AppSettings.DefaultSelectedTranslationTermPrompt
            }
        };

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
