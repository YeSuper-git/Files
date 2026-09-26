// Copyright (c) Files Community
// Licensed under the MIT License.

#if FILES_RESOURCE_MANAGER
using Files.App.Views.Shells;
#endif

namespace Files.App.Actions
{
	[GeneratedRichCommand]
	internal sealed class SelectAllAction : IAction
	{
		private readonly IContentPageContext context;

		public string Label
			=> Strings.SelectAll.GetLocalizedResource();

		public string Description
			=> Strings.SelectAllDescription.GetLocalizedResource();

		public ActionCategory Category
			=> ActionCategory.Selection;

		public RichGlyph Glyph
			=> new(themedIconStyle: "App.ThemedIcons.SelectAll");

		public HotKey HotKey
			=> new(Keys.A, KeyModifiers.Ctrl);

		public bool IsExecutable
		{
			get
			{
				if (context.PageType is ContentPageTypes.Home)
					return false;

				var page = context.ShellPage;
				if (page is null)
					return false;

#if FILES_RESOURCE_MANAGER
				if (page is ModernShellPage { CurrentResourceLibraryPage: { } resourceLibraryPage })
					return resourceLibraryPage.BrowserItems.Count > context.SelectedItems.Count;
#endif

				var shellViewModel = page.GetRequiredShellViewModel();
				int itemCount = shellViewModel.FilesAndFolders.Count;
				int selectedItemCount = context.SelectedItems.Count;
				if (itemCount == selectedItemCount)
					return false;

				bool isRenaming = page.SlimContentPage?.IsRenamingItem ?? false;

				return !isRenaming;
			}
		}

		public SelectAllAction()
		{
			context = Ioc.Default.GetRequiredService<IContentPageContext>();
		}

		public Task ExecuteAsync(object? parameter = null)
		{
#if FILES_RESOURCE_MANAGER
			if (context.ShellPage is ModernShellPage { CurrentResourceLibraryPage: { } resourceLibraryPage })
			{
				resourceLibraryPage.SelectAllItems();
				return Task.CompletedTask;
			}
#endif
			context.ShellPage?.SlimContentPage?.ItemManipulationModel?.SelectAllItems();

			return Task.CompletedTask;
		}
	}
}
