// Copyright (c) Files Community. Licensed under the MIT License.
using Files.App.Services.VideoEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Files.App.Views.VideoEditor;

public sealed class VideoDownloadTemplateSelector : DataTemplateSelector
{
    public DataTemplate? PendingTemplate { get; set; }
    public DataTemplate? JobTemplate { get; set; }
    protected override DataTemplate SelectTemplateCore(object item) => item is PendingVideo ? PendingTemplate! : JobTemplate!;
    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
