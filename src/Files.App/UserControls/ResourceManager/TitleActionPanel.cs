// Copyright (c) Files Community. Licensed under the MIT License.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Files.App.UserControls.ResourceManager;

public sealed class TitleActionPanel : Panel
{
    private const double Spacing = 12;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 2) return new Size();
        var title = Children[0];
        var action = Children[1];
        action.Measure(availableSize);
        var gap = action.Visibility == Visibility.Visible ? Spacing : 0;
        title.Measure(new Size(Math.Max(0, availableSize.Width - action.DesiredSize.Width - gap), availableSize.Height));
        return new Size(Math.Min(availableSize.Width, title.DesiredSize.Width + gap + action.DesiredSize.Width),
            Math.Max(title.DesiredSize.Height, action.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count != 2) return finalSize;
        var title = Children[0];
        var action = Children[1];
        var actionWidth = Math.Min(finalSize.Width, action.DesiredSize.Width);
        var gap = action.Visibility == Visibility.Visible ? Spacing : 0;
        var titleWidth = Math.Max(0, finalSize.Width - actionWidth - gap);
        title.Measure(new Size(titleWidth, finalSize.Height));
        titleWidth = Math.Min(titleWidth, title.DesiredSize.Width);
        title.Arrange(new Rect(0, 0, titleWidth, finalSize.Height));
        action.Arrange(new Rect(titleWidth + gap, 0, actionWidth, Math.Min(finalSize.Height, action.DesiredSize.Height)));
        return finalSize;
    }
}
