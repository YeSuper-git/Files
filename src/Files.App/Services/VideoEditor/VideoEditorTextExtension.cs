// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Extensions;
using Microsoft.UI.Xaml.Markup;

namespace Files.App.Services.VideoEditor;

[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed class VideoEditorTextExtension : MarkupExtension
{
	public string Name { get; set; } = string.Empty;

	protected override object ProvideValue() => Name.GetLocalizedResource();
}
