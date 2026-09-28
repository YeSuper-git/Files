// Copyright (c) Files Community
// Licensed under the MIT License.

using Windows.Storage;
#if FILES_RESOURCE_MANAGER
using Files.App.Data.Items.ResourceManager;
using Files.App.Views.Shells;
#endif

namespace Files.App.Actions
{
	internal abstract class BaseDeleteAction : BaseUIAction
	{
		private readonly IFoldersSettingsService settings;

		protected readonly IContentPageContext context;

		public virtual ActionCategory Category
			=> ActionCategory.FileSystem;

		public override bool IsExecutable
		{
			get
			{
				var canExecute = context.HasSelection &&
					context.ShellPage?.SlimContentPage?.IsRenamingItem is not true &&
					UIHelpers.CanShowDialog;
				if (!canExecute)
					return false;

#if FILES_RESOURCE_MANAGER
				if (context.ShellPage is ModernShellPage { CurrentResourceLibraryPage: { } resourcePage } &&
					context.SelectedItems.Any(item => item is ResourceActorListedItem) &&
					!resourcePage.CanDeleteSelectedActorFolders(context.SelectedItems))
					return false;
#endif
				return true;
			}
		}

		public BaseDeleteAction()
		{
			settings = Ioc.Default.GetRequiredService<IFoldersSettingsService>();
			context = Ioc.Default.GetRequiredService<IContentPageContext>();

			context.PropertyChanged += Context_PropertyChanged;
		}

		protected async Task DeleteItemsAsync(bool permanently)
		{
#if FILES_RESOURCE_MANAGER
			if (context.ShellPage is ModernShellPage { CurrentResourceLibraryPage: { } actorDeletionPage } &&
				await actorDeletionPage.TryDeleteSelectedActorFoldersAsync(context.SelectedItems))
				return;
#endif

			var items =
				context.SelectedItems.Select(item =>
					StorageHelpers.FromPathAndType(
						item.GetRequiredPath(),
						item.PrimaryItemAttribute is StorageItemTypes.File
							? FilesystemItemType.File
							: FilesystemItemType.Directory));

			if (context.ShellPage is { } shellPage)
			{
				await shellPage.FilesystemHelpers.DeleteItemsAsync(items, settings.DeleteConfirmationPolicy, permanently, true);
#if FILES_RESOURCE_MANAGER
				if (shellPage is ModernShellPage { CurrentResourceLibraryPage: { } resourceLibraryPage })
				{
					await resourceLibraryPage.RefreshAsync();
					return;
				}
#endif
				var shellViewModel = shellPage.GetRequiredShellViewModel();
				await shellViewModel.ApplyFilesAndFoldersChangesAsync();
			}
		}

		private void Context_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(IContentPageContext.HasSelection) or nameof(IContentPageContext.SelectedItems))
				OnPropertyChanged(nameof(IsExecutable));
		}
	}
}
