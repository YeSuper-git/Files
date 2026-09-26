// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.DependencyInjection;
using Files.App.Data.Contracts;
using Files.App.Data.Enums;
using Files.App.Data.Models;
using Files.App.Data.Models.ResourceManager;
using Files.App.Services.ResourceManager;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;

namespace Files.App.ViewModels.ResourceManager;

public sealed partial class ResourceBrowserItemViewModel : ObservableObject
{
    private readonly IResourceWorkspaceService _workspace = Ioc.Default.GetRequiredService<IResourceWorkspaceService>();
    private readonly IFileTagsSettingsService _fileTagsSettings = Ioc.Default.GetRequiredService<IFileTagsSettingsService>();
    private readonly IAppSettingsService _appSettings = Ioc.Default.GetRequiredService<IAppSettingsService>();
    private readonly VideoAssistantSearchService _videoAssistantSearch = Ioc.Default.GetRequiredService<VideoAssistantSearchService>();

    public ResourceBrowserItemViewModel(ResourceBrowserItem model)
    {
        Model = model;
        if (model.Kind != ResourceBrowserItemKind.ActorFolder)
            UpdateTags(_workspace.GetResourceTagIds(model.Path));
    }

    public ResourceBrowserItem Model { get; }
    private int? _actorWorkCount;
    private double _actorCardWidth = 250;
    private string? _translatedTitle;
    private bool _isTranslatedTitleShown;
    public string? TranslatedTitle => _translatedTitle;
    public int? ActorWorkCount
    {
        get => _actorWorkCount;
        set
        {
            if (SetProperty(ref _actorWorkCount, value))
                OnPropertyChanged(nameof(ActorWorkCountText));
        }
    }

    public string Name
    {
        get
        {
            if (Kind == ResourceBrowserItemKind.VideoFolder && _isTranslatedTitleShown && !string.IsNullOrWhiteSpace(_translatedTitle))
                return _translatedTitle;

            if (Model.Kind != ResourceBrowserItemKind.ActorFolder)
                return Model.Name;

            var actorName = _workspace.GetActorDetails(Model.Path).Name;
            return string.IsNullOrWhiteSpace(actorName) ? Model.Name : actorName;
        }
    }
    public string Path => Model.Path;
    public ResourceBrowserItemKind Kind => Model.Kind;
    public string DescriptionText
    {
        get
        {
            if (Kind != ResourceBrowserItemKind.ActorFolder)
                return Subtitle;

            var biography = _workspace.GetActorDetails(Model.Path).Biography?.Trim();
            return string.IsNullOrWhiteSpace(biography) ? "暂无简介" : biography;
        }
    }
    public Visibility DescriptionVisibility => string.IsNullOrWhiteSpace(DescriptionText) ? Visibility.Collapsed : Visibility.Visible;
    public string ActorWorkCountText => ActorWorkCount is { } count ? $"{count} 部作品" : "作品统计中";
    public Visibility ActorWorkCountVisibility => Kind == ResourceBrowserItemKind.ActorFolder ? Visibility.Visible : Visibility.Collapsed;
    public Visibility KindLabelVisibility => Kind is ResourceBrowserItemKind.ActorFolder or ResourceBrowserItemKind.VideoFolder or ResourceBrowserItemKind.VideoFile
        ? Visibility.Collapsed
        : Visibility.Visible;
    public Stretch PosterStretch => Stretch.UniformToFill;
    public string VideoTitleTranslationLine
    {
        get
        {
            if (Kind is not (ResourceBrowserItemKind.VideoFolder or ResourceBrowserItemKind.VideoFile))
                return string.Empty;

            var translation = _workspace.GetVideoTitleTranslation(GetTranslationKey());
            if (translation is null)
                return string.Empty;

            var title = GetTranslationSourceTitle();
            return string.Equals(translation.SourceTitle, title, StringComparison.Ordinal)
                ? $"译名：{translation.TranslatedTitle}"
                : "译名已过期（标题已更改）";
        }
    }
    public Visibility VideoTitleTranslationVisibility => string.IsNullOrEmpty(VideoTitleTranslationLine) ? Visibility.Collapsed : Visibility.Visible;
    public string VideoTitleTranslationAction
    {
        get
        {
            if (Kind is not (ResourceBrowserItemKind.VideoFolder or ResourceBrowserItemKind.VideoFile))
                return string.Empty;

            var translation = _workspace.GetVideoTitleTranslation(GetTranslationKey());
            if (translation is null)
                return "翻译标题";

            return string.Equals(translation.SourceTitle, GetTranslationSourceTitle(), StringComparison.Ordinal)
                ? "查看译名"
                : "重新翻译";
        }
    }
    public Visibility VideoTitleTranslationActionVisibility => string.IsNullOrEmpty(VideoTitleTranslationAction) ? Visibility.Collapsed : Visibility.Visible;

    public void SetTranslatedTitle(string? translatedTitle, bool showTranslatedTitle)
    {
        var changed = !string.Equals(_translatedTitle, translatedTitle, StringComparison.Ordinal)
            || _isTranslatedTitleShown != showTranslatedTitle;
        _translatedTitle = translatedTitle;
        _isTranslatedTitleShown = showTranslatedTitle;
        if (changed)
            OnPropertyChanged(nameof(Name));
    }
    public ObservableCollection<string> Tags { get; } = [];
    public Visibility TagListVisibility => Tags.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public ObservableCollection<TagViewModel> FileTags { get; } = [];
    public Visibility FileTagsVisibility => FileTags.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public ObservableCollection<string> PosterTags { get; } = [];
    public Visibility PosterTagListVisibility => PosterTags.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    private BitmapImage? _poster;
    public BitmapImage? Poster
    {
        get => _poster;
        set
        {
            if (SetProperty(ref _poster, value))
            {
                OnPropertyChanged(nameof(PosterVisibility));
                OnPropertyChanged(nameof(FallbackVisibility));
            }
        }
    }
    public bool CanSetPoster => Kind is not ResourceBrowserItemKind.CategoryFolder;
    public Visibility PosterVisibility => Poster is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FallbackVisibility => Poster is null ? Visibility.Visible : Visibility.Collapsed;
    public string FallbackGlyph => Kind == ResourceBrowserItemKind.VideoFile ? "\uE7F4" : "\uE8B7";
    public string KindLabel => Kind switch
    {
        ResourceBrowserItemKind.ActorFolder => "演员",
        ResourceBrowserItemKind.CategoryFolder => "分类",
        ResourceBrowserItemKind.VideoFolder => "视频文件夹",
        ResourceBrowserItemKind.VideoFile => "视频",
        _ => string.Empty,
    };
    public string Subtitle
    {
        get
        {
            if (Kind == ResourceBrowserItemKind.ActorFolder)
                return "演员";

            return Kind is ResourceBrowserItemKind.VideoFolder or ResourceBrowserItemKind.VideoFile
                ? string.Empty
                : "文件夹";
        }
    }

    private string GetTranslationSourceTitle()
    {
        var itemName = Kind == ResourceBrowserItemKind.VideoFile
            ? System.IO.Path.GetFileNameWithoutExtension(Model.Name)
            : Model.Name;
        return _videoAssistantSearch.GetVideoTitle(itemName).Trim();
    }

    private string GetTranslationKey()
    {
        var itemName = Kind == ResourceBrowserItemKind.VideoFile
            ? System.IO.Path.GetFileNameWithoutExtension(Model.Name)
            : Model.Name;
        var provider = Enum.TryParse<ResourceManagerTranslationProvider>(_appSettings.ResourceManagerTranslationProvider, true, out var selectedProvider)
            ? selectedProvider
            : ResourceManagerTranslationProvider.AliyunMachineTranslation;
        return _videoAssistantSearch.GetVideoTitleTranslationKey(itemName, Model.Path, provider.ToString());
    }
    public double CardWidth => Kind switch
    {
        ResourceBrowserItemKind.ActorFolder or ResourceBrowserItemKind.VideoFolder => _actorCardWidth,
        ResourceBrowserItemKind.VideoFile => 244,
        _ => 176,
    };
    public double PosterHeight => Kind switch
    {
        ResourceBrowserItemKind.ActorFolder => CardWidth * 1.32,
        ResourceBrowserItemKind.VideoFolder => CardWidth * (9d / 16d),
        ResourceBrowserItemKind.CategoryFolder => 136,
        _ => 138,
    };

    public void SetAdaptiveCardWidth(double width)
    {
        if (Math.Abs(_actorCardWidth - width) < 0.5)
            return;

        _actorCardWidth = width;
        OnPropertyChanged(nameof(CardWidth));
        OnPropertyChanged(nameof(PosterHeight));
    }
    public void UpdateTags(IEnumerable<string>? tagIds)
    {
        Tags.Clear();
        foreach (var tag in _workspace.GetResourceTagsByIds(tagIds))
        {
            Tags.Add(tag.Name);
        }

        RefreshPosterTags();
        OnPropertyChanged(nameof(TagListVisibility));
    }

    public void UpdateFileTags(IEnumerable<string>? tagIds)
    {
        FileTags.Clear();
        foreach (var tag in _fileTagsSettings.GetTagsByIds(tagIds?.ToArray()) ?? [])
            FileTags.Add(tag);

        RefreshPosterTags();
        OnPropertyChanged(nameof(FileTagsVisibility));
    }

    private void RefreshPosterTags()
    {
        PosterTags.Clear();
        foreach (var tag in Tags.Concat(FileTags.Select(tag => tag.Name)).Distinct(StringComparer.CurrentCultureIgnoreCase))
            PosterTags.Add(tag);

        OnPropertyChanged(nameof(PosterTagListVisibility));
    }
}
