// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Items;

namespace Files.App.Data.Items.ResourceManager;

public sealed partial class ResourceVideoFolderListedItem : ListedItem
{
    public string? PosterPath { get; set; }

    private string _displayTitle = string.Empty;
    public string DisplayTitle
    {
        get => _displayTitle;
        set => SetProperty(ref _displayTitle, value);
    }

    private bool _isTranslatedTitleShown;
    public bool IsTranslatedTitleShown
    {
        get => _isTranslatedTitleShown;
        set
        {
            if (SetProperty(ref _isTranslatedTitleShown, value))
                OnPropertyChanged(nameof(TitleActionText));
        }
    }

    private bool _hasTranslatedTitle;
    public bool HasTranslatedTitle
    {
        get => _hasTranslatedTitle;
        set
        {
            if (SetProperty(ref _hasTranslatedTitle, value))
                OnPropertyChanged(nameof(TitleActionText));
        }
    }

    public string TitleActionText => IsTranslatedTitleShown ? "显示原名" : HasTranslatedTitle ? "显示译名" : "翻译标题";
    public Func<Task>? ToggleTitleAsync { get; set; }
}
