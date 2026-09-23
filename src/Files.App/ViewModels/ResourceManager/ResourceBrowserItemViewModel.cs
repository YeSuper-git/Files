// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.DependencyInjection;
using Files.App.Data.Models.ResourceManager;
using Files.App.Services.ResourceManager;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Files.App.ViewModels.ResourceManager;

public sealed partial class ResourceBrowserItemViewModel : ObservableObject
{
    private readonly IResourceWorkspaceService _workspace = Ioc.Default.GetRequiredService<IResourceWorkspaceService>();

    public ResourceBrowserItemViewModel(ResourceBrowserItem model)
    {
        Model = model;
        UpdateTags(_workspace.GetResourceTagIds(model.Path));
    }

    public ResourceBrowserItem Model { get; }
    public string Name
    {
        get
        {
            if (Model.Kind != ResourceBrowserItemKind.ActorFolder)
                return Model.Name;

            var actorName = _workspace.GetActorDetails(Model.Path).Name;
            return string.IsNullOrWhiteSpace(actorName) ? Model.Name : actorName;
        }
    }
    public string Path => Model.Path;
    public ResourceBrowserItemKind Kind => Model.Kind;
    public ObservableCollection<string> Tags { get; } = [];
    public Visibility TagListVisibility => Tags.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public BitmapImage? Poster { get; set; }
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

            return Kind == ResourceBrowserItemKind.VideoFile ? "视频文件" : "文件夹";
        }
    }
    public double CardWidth => Kind is ResourceBrowserItemKind.VideoFolder or ResourceBrowserItemKind.VideoFile ? 244 : 176;
    public double PosterHeight => Kind == ResourceBrowserItemKind.ActorFolder ? 224 : Kind == ResourceBrowserItemKind.CategoryFolder ? 136 : 138;
    public void UpdateTags(IEnumerable<string>? tagIds)
    {
        Tags.Clear();
        foreach (var tag in _workspace.GetResourceTagsByIds(tagIds))
        {
            Tags.Add(tag.Name);
        }

        OnPropertyChanged(nameof(TagListVisibility));
    }
}
