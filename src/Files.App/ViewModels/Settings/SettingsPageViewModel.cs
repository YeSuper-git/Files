// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using Files.App.Controls;
using Microsoft.UI.Xaml;
using WinRT;

namespace Files.App.ViewModels.Settings
{
	public sealed partial class SettingsPageViewModel : ObservableObject
	{
		public ObservableCollection<SettingsNavigationItem> NavigationItems { get; } = [];

		// Wrapped projection of NavigationItems for binding to SidebarView.MenuItemsSource (which renders FlatSidebarItem rows). Settings is a flat list so every entry sits at Depth=0.
		public ObservableCollection<FlatSidebarItem> FlatNavigationItems { get; } = [];

		public ObservableCollection<SettingsSearchResult> SearchResults { get; } = [];

		private List<SettingsSearchResult>? _searchIndex;

		[ObservableProperty]
		public partial SettingsPageKind SelectedPage { get; set; } = SettingsPageKind.GeneralPage;

		[ObservableProperty]
		[NotifyPropertyChangedFor(nameof(IsSearchActive))]
		[NotifyPropertyChangedFor(nameof(HasNoSearchResults))]
		[NotifyPropertyChangedFor(nameof(SearchHeading))]
		public partial string SearchQuery { get; set; } = string.Empty;

		public bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchQuery);

		public bool HasNoSearchResults => IsSearchActive && SearchResults.Count == 0;

		public string SearchHeading => IsSearchActive
			? string.Format(Strings.SearchResultsFor.GetLocalizedResource(), SearchQuery)
			: string.Empty;

		public SettingsPageViewModel()
		{
			NavigationItems.Add(CreateNavigationItem(SettingsPageKind.GeneralPage, "SettingsItemGeneral", Strings.General.GetLocalizedResource(), "App.ThemedIcons.Settings.General"));
			NavigationItems.Add(CreateNavigationItem(SettingsPageKind.AppearancePage, "SettingsItemAppearance", Strings.Appearance.GetLocalizedResource(), "App.ThemedIcons.Settings.Appearance"));
			NavigationItems.Add(CreateNavigationItem(SettingsPageKind.LayoutPage, "SettingsItemLayout", Strings.Layout.GetLocalizedResource(), "App.ThemedIcons.Settings.Layout"));
			NavigationItems.Add(CreateNavigationItem(SettingsPageKind.FoldersPage, "SettingsItemFolders", Strings.FilesAndFolders.GetLocalizedResource(), "App.ThemedIcons.Settings.FilesFolders"));
			NavigationItems.Add(CreateNavigationItem(SettingsPageKind.ActionsPage, "SettingsItemActions", Strings.Actions.GetLocalizedResource(), "App.ThemedIcons.Settings.KeyboardActions"));
			NavigationItems.Add(CreateNavigationItem(SettingsPageKind.TagsPage, "SettingsItemTags", Strings.FileTags.GetLocalizedResource(), "App.ThemedIcons.Settings.Tags"));
			NavigationItems.Add(CreateNavigationItem(SettingsPageKind.VideoEditorPage, "SettingsItemVideoEditor", Strings.VideoEditorSettingsTitle.GetLocalizedResource(), "App.ThemedIcons.Settings.FilesFolders"));
			#if FILES_RESOURCE_MANAGER
			NavigationItems.Add(CreateNavigationItem(SettingsPageKind.AnimeLibraryPage, "SettingsItemAnimeLibrary", Strings.LibraryAnimeGroup.GetLocalizedResource(), "App.ThemedIcons.Settings.FilesFolders"));
			NavigationItems.Add(CreateNavigationItem(SettingsPageKind.ResourceManagerPage, "SettingsItemResourceManager", Strings.LibraryDramaGroup.GetLocalizedResource(), "App.ThemedIcons.Settings.FilesFolders"));
			#endif
			NavigationItems.Add(CreateNavigationItem(SettingsPageKind.DevToolsPage, "SettingsItemDevTools", Strings.DevTools.GetLocalizedResource(), "App.ThemedIcons.Settings.DevTools"));
			NavigationItems.Add(CreateNavigationItem(SettingsPageKind.AdvancedPage, "SettingsItemAdvanced", Strings.Advanced.GetLocalizedResource(), "App.ThemedIcons.Settings.Advanced"));
			NavigationItems.Add(CreateNavigationItem(SettingsPageKind.AboutPage, "SettingsItemAbout", Strings.About.GetLocalizedResource(), "App.ThemedIcons.Info"));

			foreach (var navItem in NavigationItems)
				FlatNavigationItems.Add(new FlatSidebarItem(navItem, 0));

			SetSelectedPage(SettingsPageKind.GeneralPage);
		}

		public void SetSelectedPage(SettingsPageKind pageKind)
		{
			SelectedPage = pageKind;

			foreach (var item in NavigationItems)
			{
				var isSelected = item.PageKind == pageKind;
				if (item.IconElement is ThemedIcon themedIcon)
				{
					themedIcon.IsFilled = isSelected;
					themedIcon.IconType = ThemedIconTypes.Outline;
				}
			}
		}

		public void UpdateSearchResults(string? query)
		{
			if (string.IsNullOrWhiteSpace(query))
			{
				ClearSearch();
				return;
			}

			_searchIndex ??= SettingsSearchIndexer.BuildIndex();
			var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

			SearchResults.Clear();
			foreach (var entry in _searchIndex)
			{
				if (terms.All(term => entry.Haystack.Contains(term, StringComparison.CurrentCultureIgnoreCase)))
					SearchResults.Add(entry);
			}

			SearchQuery = query;
		}

		public void ClearSearch()
		{
			SearchResults.Clear();
			SearchQuery = string.Empty;
		}

		[DynamicWindowsRuntimeCast(typeof(Style))]
		private static SettingsNavigationItem CreateNavigationItem(SettingsPageKind pageKind, string automationId, string text, string iconStyleKey)
		{
			var iconStyle = (Style)Application.Current.Resources[iconStyleKey];
			var iconElement = new ThemedIcon()
			{
				Width = 16,
				Height = 16,
				IconType = ThemedIconTypes.Outline,
				Style = iconStyle,
			};

            #if FILES_RESOURCE_MANAGER
            var glyph = pageKind switch
            {
                SettingsPageKind.AnimeLibraryPage => "\uE7F4",
                SettingsPageKind.ResourceManagerPage => "\uE8B7",
                SettingsPageKind.VideoEditorPage => "\uE714",
                _ => null
            };
            if (glyph is not null)
                return new SettingsNavigationItem(pageKind, automationId, text, new Microsoft.UI.Xaml.Controls.FontIcon { Width = 16, Height = 16, FontSize = 16, Glyph = glyph });
            #endif
			return new SettingsNavigationItem(pageKind, automationId, text, iconElement);
		}
	}

	public sealed partial class SettingsNavigationItem : ObservableObject, ISidebarItemModel, ISidebarItemPresentationModel
	{
		public SettingsPageKind PageKind { get; }
		public string AutomationId { get; }
		public string Text { get; }
		public FrameworkElement IconElement { get; }

		// ISidebarItemModel
		public object? Children => null;
		public string? Path => null;
		[ObservableProperty] public partial bool IsExpanded { get; set; }

		// Sidebar presentation
		public object? ToolTip => Text;
		public object? ItemDecorator => null;
		FrameworkElement ISidebarItemPresentationModel.IconElement => IconElement;
		FrameworkElement? ISidebarItemPresentationModel.ItemDecorator => null;

		public SettingsNavigationItem(SettingsPageKind pageKind, string automationId, string text, FrameworkElement iconElement)
		{
			PageKind = pageKind;
			AutomationId = automationId;
			Text = text;
			IconElement = iconElement;
		}
	}
}
