// Copyright (c) Files Community. Licensed under the MIT License.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Files.App.Helpers;

internal static class ResourceDialogPresentation
{
	public static ContentDialog Create(XamlRoot root, FrameworkElement content, double maximumWidth = 1200, double maximumHeight = 800)
	{
		var width = Math.Max(320, Math.Min(maximumWidth, root.Size.Width - 80));
		var height = Math.Max(240, Math.Min(maximumHeight, root.Size.Height - 100));
		content.Width = width - 64;
		content.MaxWidth = width - 64;
		content.HorizontalAlignment = HorizontalAlignment.Stretch;
		content.Height = height - 96;
		var dialog = new ContentDialog { XamlRoot = root, Content = content, FullSizeDesired = false };
		dialog.Resources["ContentDialogMinWidth"] = width;
		dialog.Resources["ContentDialogMaxWidth"] = width;
		dialog.Resources["ContentDialogMinHeight"] = height;
		dialog.Resources["ContentDialogMaxHeight"] = height;
		return dialog;
	}

	public static Button CreateIconButton(string glyph, string tooltip)
	{
		var button = new Button
		{
			Content = new FontIcon { Glyph = glyph, FontSize = 16 },
			Width = 36, Height = 36, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
			CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(0),
			Background = new SolidColorBrush(Color.FromArgb(150, 32, 32, 32)),
			Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
		};
		ToolTipService.SetToolTip(button, tooltip);
		Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip);
		return button;
	}
}
