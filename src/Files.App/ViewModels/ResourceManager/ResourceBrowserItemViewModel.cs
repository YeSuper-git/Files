// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Models.ResourceManager;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Files.App.ViewModels.ResourceManager;

public sealed class ResourceBrowserItemViewModel
{
    public ResourceBrowserItemViewModel(ResourceBrowserItem model)
    {
        Model = model;
    }

    public ResourceBrowserItem Model { get; }
    public string Name => Model.Name;
    public string Path => Model.Path;
    public ResourceBrowserItemKind Kind => Model.Kind;
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
    public string Subtitle => Kind == ResourceBrowserItemKind.VideoFile ? "视频文件" : "文件夹";
    public double CardWidth => Kind is ResourceBrowserItemKind.VideoFolder or ResourceBrowserItemKind.VideoFile ? 244 : 176;
    public double PosterHeight => Kind == ResourceBrowserItemKind.ActorFolder ? 224 : Kind == ResourceBrowserItemKind.CategoryFolder ? 136 : 138;
}
