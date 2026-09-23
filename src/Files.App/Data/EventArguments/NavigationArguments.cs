// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

namespace Files.App.Data.EventArguments
{
	public class NavigationArguments
	{
		public bool FocusOnNavigation { get; set; } = false;

		public string? NavPathParam
		{
			get;
			set => field = value is null ? null : ShellHelpers.ResolveShellPath(value);
		}

		public IShellPage? AssociatedTabInstance { get; set; }

		public bool IsSearchResultPage { get; set; } = false;

		public string? SearchPathParam
		{
			get;
			set => field = value is null ? null : ShellHelpers.ResolveShellPath(value);
		}

		public string? SearchQuery { get; set; } = null;

		public bool IsLayoutSwitch { get; set; } = false;

		public IEnumerable<string>? SelectItems { get; set; }

		/// <summary>
		/// Keeps a native layout page inside the resource manager's configured library.
		/// </summary>
		public bool IsResourceManagerMode { get; set; } = false;

		public string? ResourceLibraryPath { get; set; }

		/// <summary>Marks a navigation entry as a virtual location inside the resource-library browser.</summary>
		public bool IsResourceLibraryPage { get; set; }

		public string[]? ResourceLocationPaths { get; set; }

		public string[]? ResourceLocationTitles { get; set; }

		public Files.App.Data.Models.ResourceManager.ResourceBrowserLocationKind[]? ResourceLocationKinds { get; set; }
	}
}
