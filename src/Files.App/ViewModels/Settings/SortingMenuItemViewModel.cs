// Copyright (c) Files Community. Licensed under the MIT License.
namespace Files.App.ViewModels.Settings;

public sealed class SortingMenuItemViewModel : ObservableObject
{
    private readonly string _code;
    private readonly IAppearanceSettingsService _settings;
    public string Label { get; }

    public SortingMenuItemViewModel(string code, string label, IAppearanceSettingsService settings)
    {
        _code = code;
        Label = label;
        _settings = settings;
    }

    public int VisibilityMode
    {
        get => _settings.SortingMenuVisibility?.GetValueOrDefault(_code) ?? 0;
        set
        {
            if (value is < 0 or > 2 || value == VisibilityMode) return;
            var preferences = new Dictionary<string, int>(_settings.SortingMenuVisibility ?? []);
            if (value == 0) preferences.Remove(_code);
            else preferences[_code] = value;
            _settings.SortingMenuVisibility = preferences;
            OnPropertyChanged();
        }
    }
}
