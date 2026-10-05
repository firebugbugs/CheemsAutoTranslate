using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CursorTranslator.Models;

public sealed class PromptProfile : INotifyPropertyChanged
{
    private string _name = "默认提示词";
    private string _prompt = "";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ToString));
        }
    }
    public string Prompt
    {
        get => _prompt;
        set
        {
            if (_prompt == value) return;
            _prompt = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PromptProfile Copy() => new()
    {
        Id = Id,
        Name = Name,
        Prompt = Prompt
    };

    public override string ToString() => Name;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
