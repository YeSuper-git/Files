// Copyright (c) Files Community
// Licensed under the MIT License.

#if FILES_RESOURCE_MANAGER
using Files.App.Views.Shells;
#endif

namespace Files.App.Actions
{
	[GeneratedRichCommand]
	internal sealed partial class RenameAction : ObservableObject, IAction
	{
		private readonly IContentPageContext context;

		public string Label
			=> Strings.Rename.GetLocalizedResource();

		public string Description
			=> Strings.RenameDescription.GetLocalizedResource();

		public ActionCategory Category
			=> ActionCategory.FileSystem;

		public HotKey HotKey
			=> new(Keys.F2);

		public RichGlyph Glyph
			=> new(themedIconStyle: "App.ThemedIcons.Rename");

		public string AutomationId
			=> "InnerNavigationToolbarRenameButton";

		public string AccessKey
			=> "M";

		public bool IsExecutable =>
			context.ShellPage is not null &&
			IsPageTypeValid() &&
			(context.ShellPage.SlimContentPage is not null || IsSingleResourceSelection) &&
			context.HasSelection;

		private bool IsSingleResourceSelection
		{
			get
			{
#if FILES_RESOURCE_MANAGER
				return context.SelectedItems.Count == 1 &&
					context.ShellPage is ModernShellPage { CurrentResourceLibraryPage: not null };
#else
				return false;
#endif
			}
		}

		public RenameAction()
		{
			context = Ioc.Default.GetRequiredService<IContentPageContext>();

			context.PropertyChanged += Context_PropertyChanged;
		}

		public async Task ExecuteAsync(object? parameter = null)
		{
#if FILES_RESOURCE_MANAGER
			if (context.ShellPage is ModernShellPage { CurrentResourceLibraryPage: { } resourceLibraryPage })
			{
				await resourceLibraryPage.RenameSelectedItemAsync();
				return;
			}
#endif

			if (context.SelectedItems.Count > 1)
			{
				var viewModel = new BulkRenameDialogViewModel();
				var dialogService = Ioc.Default.GetRequiredService<IDialogService>();
				var result = await dialogService.ShowDialogAsync(viewModel);
			}
			else
			{
				context.ShellPage?.SlimContentPage?.ItemManipulationModel.StartRenameItem();
			}
		}

		private bool IsPageTypeValid()
		{
			return
				context.PageType != ContentPageTypes.None &&
				context.PageType != ContentPageTypes.Home &&
				context.PageType != ContentPageTypes.RecycleBin &&
				context.PageType != ContentPageTypes.ZipFolder &&
				context.PageType != ContentPageTypes.ReleaseNotes &&
				context.PageType != ContentPageTypes.Settings;
		}

		private void Context_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case nameof(IContentPageContext.ShellPage):
				case nameof(IContentPageContext.PageType):
				case nameof(IContentPageContext.HasSelection):
				case nameof(IContentPageContext.SelectedItems):
					OnPropertyChanged(nameof(IsExecutable));
					break;
			}
		}
	}
}
