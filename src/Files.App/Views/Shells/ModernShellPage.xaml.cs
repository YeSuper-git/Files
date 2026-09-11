// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System.IO;
using Windows.System;
#if FILES_AV_MANAGER
using Files.App.Helpers;
using Files.App.Services.AvManager;
#endif

namespace Files.App.Views.Shells
{
	public sealed partial class ModernShellPage : BaseShellPage
	{
		protected override Frame ItemDisplay
			=> ItemDisplayFrame;

		private NavigationInteractionTracker _navigationInteractionTracker;

#if FILES_AV_MANAGER
		private readonly IAvWorkspaceService _avWorkspaceService = Ioc.Default.GetRequiredService<IAvWorkspaceService>();
#endif

		private NavigationParams? _NavParams;
		public NavigationParams? NavParams
		{
			get => _NavParams;
			set
			{
				if (value != _NavParams)
				{
					_NavParams = value;

					if (IsLoaded)
						OnNavigationParamsChanged();
				}
			}
		}

		public ModernShellPage() : base(new CurrentInstanceViewModel())
		{
			InitializeComponent();

			ShellViewModel = new ShellViewModel(InstanceViewModel.FolderSettings);
			ShellViewModel.WorkingDirectoryModified += ViewModel_WorkingDirectoryModified;
			ShellViewModel.ItemLoadStatusChanged += FilesystemViewModel_ItemLoadStatusChanged;
			ShellViewModel.DirectoryInfoUpdated += FilesystemViewModel_DirectoryInfoUpdated;
			ShellViewModel.PageTypeUpdated += FilesystemViewModel_PageTypeUpdated;
			ShellViewModel.OnSelectionRequestedEvent += FilesystemViewModel_OnSelectionRequestedEvent;
			ShellViewModel.GitDirectoryUpdated += FilesystemViewModel_GitDirectoryUpdated;
			ShellViewModel.FocusFilterHeader += ShellViewModel_FocusFilterHeader;

			ToolbarViewModel.PathControlDisplayText = Strings.Home.GetLocalizedResource();
			ToolbarViewModel.RefreshWidgetsRequested += ModernShellPage_RefreshWidgetsRequested;

			ContentChanged += ModernShellPage_ContentChanged;

			_navigationInteractionTracker = new NavigationInteractionTracker(this, BackIcon, ForwardIcon);
			_navigationInteractionTracker.NavigationRequested += OverscrollNavigationRequested;
		}

		private async void ShellViewModel_FocusFilterHeader(object? sender, EventArgs e)
		{
			// Delay to ensure the UI is ready for focus
			await Task.Delay(100);
			if (FilterTextBox?.IsLoaded ?? false)
				FilterTextBox.Focus(FocusState.Programmatic);
		}

		private void ModernShellPage_RefreshWidgetsRequested(object? sender, EventArgs e)
		{
			if (ItemDisplayFrame?.Content is HomePage currentPage)
				currentPage.ViewModel.RefreshWidgetList();
		}

		private void ModernShellPage_ContentChanged(object? sender, TabBarItemParameter e)
		{
			UpdateStatusBarProperties();
			NotifyPropertyChanged(nameof(IsStatusBarVisible));
		}

		private void UpdateStatusBarProperties()
		{
			var contentPage = SlimContentPage is ColumnsLayoutPage columnsLayoutPage
				? columnsLayoutPage.ActiveColumnShellPage?.SlimContentPage
				: SlimContentPage;

			StatusBar.StatusBarViewModel = contentPage?.StatusBarViewModel;
			StatusBar.SelectedItemsPropertiesViewModel = contentPage?.SelectedItemsPropertiesViewModel;

			// The view model's own triggers can all fire before this page's content is
			// assigned (e.g. restoring a session inside an archive), so re-evaluate the
			// ZIP encoding selector once the content page is wired up
			_ = contentPage?.StatusBarViewModel.UpdateZipEncodingStateAsync();
		}

		protected override void OnNavigatedTo(NavigationEventArgs eventArgs)
		{
			base.OnNavigatedTo(eventArgs);

			if (eventArgs.Parameter is string navPath)
				NavParams = new NavigationParams { NavPath = navPath };
			else if (eventArgs.Parameter is NavigationParams navParams)
				NavParams = navParams;
		}

		protected override void ShellPage_NavigationRequested(object sender, PathNavigationEventArgs e)
		{
			if (e.ItemPath is null)
				return;

#if FILES_AV_MANAGER
			if (InstanceViewModel.IsAvManagerMode &&
				!AvManagerPathScope.IsWithinLibrary(e.ItemPath, InstanceViewModel.AvLibraryPath))
				return;
#endif

			ItemDisplayFrame.Navigate(InstanceViewModel.FolderSettings.GetLayoutType(e.ItemPath), new NavigationArguments()
			{
				NavPathParam = e.ItemPath,
				IsAvManagerMode = InstanceViewModel.IsAvManagerMode,
				AvLibraryPath = InstanceViewModel.AvLibraryPath,
				AssociatedTabInstance = this
			},
			new SuppressNavigationTransitionInfo());
		}

		protected override void OnNavigationParamsChanged()
		{
			var navParams = NavParams;
			if (string.IsNullOrEmpty(navParams?.NavPath) || navParams.NavPath == "Home")
			{
				NavigateHome();
			}
			else if (navParams.NavPath == "ReleaseNotes")
			{
				NavigateToReleaseNotes();
			}
#if FILES_AV_MANAGER
			else if (navParams.NavPath == "AvManager")
			{
				NavigateToAvManager();
			}
			else if (navParams.NavPath == "AvManagerTools")
			{
				NavigateToAvManagerTools();
			}
#endif
			else if (navParams.NavPath == "Settings")
			{
				NavigateToSettings(navParams.SelectItem);
			}
			else
			{
				var isTagSearch = navParams.NavPath.StartsWith("tag:");

				ItemDisplayFrame.Navigate(
					InstanceViewModel.FolderSettings.GetLayoutType(navParams.NavPath),
					new NavigationArguments()
					{
						NavPathParam = navParams.NavPath,
						SelectItems = !string.IsNullOrWhiteSpace(navParams.SelectItem) ? (string[])[navParams.SelectItem] : null,
						IsSearchResultPage = isTagSearch,
						SearchPathParam = isTagSearch ? "Home" : null,
						SearchQuery = isTagSearch ? navParams.NavPath : null,
						IsAvManagerMode = false,
						AssociatedTabInstance = this
					});
			}
		}

		protected override async void ViewModel_WorkingDirectoryModified(object? sender, WorkingDirectoryModifiedEventArgs e)
		{
			if (e is null || string.IsNullOrWhiteSpace(e.Path))
				return;

			if (e.IsLibrary)
				await UpdatePathUIToWorkingDirectoryAsync(null, e.Name);
			else
				await UpdatePathUIToWorkingDirectoryAsync(e.Path);
		}

		private async void ItemDisplayFrame_Navigated(object sender, NavigationEventArgs e)
		{
			ContentPage = await GetContentOrNullAsync();

			ToolbarViewModel.UpdateAdditionalActions();
			if (ItemDisplayFrame.CurrentSourcePageType == typeof(DetailsLayoutPage) ||
				ItemDisplayFrame.CurrentSourcePageType == typeof(GridLayoutPage))
			{
				// Reset DataGrid Rows that may be in "cut" command mode
				ContentPage!.ResetItemOpacity();
			}

			var parameters = (e.Parameter as NavigationArguments)!;
			var isTagSearch = parameters.NavPathParam is not null && parameters.NavPathParam.StartsWith("tag:");
			TabBarItemParameter = new()
			{
				InitialPageType = typeof(ModernShellPage),
				NavigationParameter = parameters.IsSearchResultPage && !isTagSearch ? parameters.SearchPathParam : parameters.NavPathParam
			};

			if (parameters.IsLayoutSwitch)
				FilesystemViewModel_DirectoryInfoUpdated(sender, EventArgs.Empty);

			// Update the ShellViewModel with the current working directory
			// Fixes https://github.com/files-community/Files/issues/17469
			if (parameters.IsSearchResultPage == false)
				ShellViewModel!.IsSearchResults = false;

			_navigationInteractionTracker.CanNavigateBackward = CanNavigateBackward;
			_navigationInteractionTracker.CanNavigateForward = CanNavigateForward;
		}

		private void OverscrollNavigationRequested(object? sender, OverscrollNavigationEventArgs e)
		{
			switch (e)
			{
				case OverscrollNavigationEventArgs.Forward:
					Forward_Click();
					break;

				case OverscrollNavigationEventArgs.Back:
					Back_Click();
					break;
			}
		}

		public override void Back_Click()
		{
			ToolbarViewModel.CanGoBack = false;
			if (!ItemDisplayFrame.CanGoBack)
				return;

			base.Back_Click();
		}

		public override void Forward_Click()
		{
			ToolbarViewModel.CanGoForward = false;
			if (!ItemDisplayFrame.CanGoForward)
				return;

			base.Forward_Click();
		}

		public override void Up_Click()
		{
			if (!ToolbarViewModel.CanNavigateToParent)
				return;

			ToolbarViewModel.CanNavigateToParent = false;
			if (string.IsNullOrEmpty(ShellViewModel?.WorkingDirectory))
				return;

#if FILES_AV_MANAGER
			if (InstanceViewModel.IsAvManagerMode &&
				AvManagerPathScope.IsLibraryRoot(ShellViewModel.WorkingDirectory, InstanceViewModel.AvLibraryPath))
				return;
#endif

			bool isPathRooted = string.Equals(ShellViewModel.WorkingDirectory, PathNormalization.GetPathRoot(ShellViewModel.WorkingDirectory), StringComparison.OrdinalIgnoreCase);
			if (isPathRooted)
			{
				ItemDisplayFrame.Navigate(
					typeof(HomePage),
					new NavigationArguments()
					{
						NavPathParam = "Home",
						AssociatedTabInstance = this
					},
					new SuppressNavigationTransitionInfo());
			}
			else
			{
				string parentDirectoryOfPath = ShellViewModel.WorkingDirectory.TrimEnd('\\', '/');

				var lastSlashIndex = parentDirectoryOfPath.LastIndexOf('\\');
				if (lastSlashIndex == -1)
					lastSlashIndex = parentDirectoryOfPath.LastIndexOf('/');
				if (lastSlashIndex != -1)
					parentDirectoryOfPath = ShellViewModel.WorkingDirectory.Remove(lastSlashIndex);
				if (parentDirectoryOfPath.EndsWith(':'))
					parentDirectoryOfPath += '\\';

				SelectSidebarItemFromPath();
				ItemDisplayFrame.Navigate(
					InstanceViewModel.FolderSettings.GetLayoutType(parentDirectoryOfPath),
					new NavigationArguments()
					{
						NavPathParam = parentDirectoryOfPath,
						AssociatedTabInstance = this
					},
					new SuppressNavigationTransitionInfo());
			}
		}

		public override void Dispose()
		{
			Bindings.StopTracking();
			ContentChanged -= ModernShellPage_ContentChanged;
			ToolbarViewModel.RefreshWidgetsRequested -= ModernShellPage_RefreshWidgetsRequested;
			if (ShellViewModel is not null)
				ShellViewModel.FocusFilterHeader -= ShellViewModel_FocusFilterHeader;
			ItemDisplayFrame.Navigated -= ItemDisplayFrame_Navigated;
			_navigationInteractionTracker.NavigationRequested -= OverscrollNavigationRequested;
			_navigationInteractionTracker.Dispose();

			base.Dispose();
		}

		public override void NavigateHome()
		{
			InstanceViewModel.IsAvManagerMode = false;
			InstanceViewModel.AvLibraryPath = null;
			ItemDisplayFrame.Navigate(
				typeof(HomePage),
				new NavigationArguments()
				{
					NavPathParam = "Home",
					AssociatedTabInstance = this
				},
				new SuppressNavigationTransitionInfo());
		}

		public override void NavigateToReleaseNotes()
		{
			InstanceViewModel.IsAvManagerMode = false;
			InstanceViewModel.AvLibraryPath = null;
			ItemDisplayFrame.Navigate(
				typeof(ReleaseNotesPage),
				new NavigationArguments()
				{
					NavPathParam = "ReleaseNotes",
					AssociatedTabInstance = this
				},
				new SuppressNavigationTransitionInfo());
		}

#if FILES_AV_MANAGER
		public override void NavigateToAvManager()
		{
			var libraryPath = _avWorkspaceService.LibraryPath;
			if (!string.IsNullOrWhiteSpace(libraryPath) && Directory.Exists(libraryPath))
			{
				InstanceViewModel.IsAvManagerMode = true;
				InstanceViewModel.AvLibraryPath = libraryPath;
				ItemDisplayFrame.Navigate(
					InstanceViewModel.FolderSettings.GetLayoutType(libraryPath),
					new NavigationArguments()
					{
						NavPathParam = libraryPath,
						IsAvManagerMode = true,
						AvLibraryPath = libraryPath,
						AssociatedTabInstance = this
					},
					new SuppressNavigationTransitionInfo());
				return;
			}

			InstanceViewModel.IsAvManagerMode = false;
			InstanceViewModel.AvLibraryPath = null;
			ItemDisplayFrame.Navigate(
				typeof(AvManager.AvManagerPage),
				new NavigationArguments()
				{
					NavPathParam = "AvManager",
					AssociatedTabInstance = this
				},
				new SuppressNavigationTransitionInfo());
		}

		public override void NavigateToAvManagerTools()
		{
			InstanceViewModel.IsAvManagerMode = true;
			InstanceViewModel.AvLibraryPath = _avWorkspaceService.LibraryPath;
			ItemDisplayFrame.Navigate(
				typeof(AvManager.AvManagerPage),
				new NavigationArguments()
				{
					NavPathParam = "AvManagerTools",
					IsAvManagerMode = true,
					AvLibraryPath = _avWorkspaceService.LibraryPath,
					AssociatedTabInstance = this
				},
				new SuppressNavigationTransitionInfo());
		}
#endif

		public override void NavigateToSettings(string? selectItem = null)
		{
			InstanceViewModel.IsAvManagerMode = false;
			InstanceViewModel.AvLibraryPath = null;
			ItemDisplayFrame.Navigate(
				typeof(SettingsPage),
				new NavigationArguments()
				{
					NavPathParam = "Settings",
					SelectItems = !string.IsNullOrWhiteSpace(selectItem) ? new[] { selectItem } : null,
					AssociatedTabInstance = this
				},
				new SuppressNavigationTransitionInfo());
		}

		public override void NavigateToPath(string? navigationPath, Type? sourcePageType, NavigationArguments? navArgs = null)
		{
			var shellViewModel = ShellViewModel!;
			shellViewModel.FilesAndFoldersFilter = null;
			var isAvManagerMode = false;
			string? avLibraryPath = null;

#if FILES_AV_MANAGER
			isAvManagerMode = navArgs?.IsAvManagerMode == true || InstanceViewModel.IsAvManagerMode;
			avLibraryPath = navArgs?.AvLibraryPath ?? InstanceViewModel.AvLibraryPath;
			if (isAvManagerMode &&
				!AvManagerPathScope.IsWithinLibrary(navigationPath, avLibraryPath))
				return;

			if (isAvManagerMode)
			{
				navArgs ??= new NavigationArguments();
				navArgs.IsAvManagerMode = true;
				navArgs.AvLibraryPath = avLibraryPath;
			}
#endif

			if (sourcePageType is null && !string.IsNullOrEmpty(navigationPath))
				sourcePageType = InstanceViewModel.FolderSettings.GetLayoutType(navigationPath);

			if (navArgs is not null && navArgs.AssociatedTabInstance is not null)
			{
				ItemDisplayFrame.Navigate(
					sourcePageType,
					navArgs,
					new SuppressNavigationTransitionInfo());
			}
			else
			{
				if ((string.IsNullOrEmpty(navigationPath) ||
					string.IsNullOrEmpty(shellViewModel.WorkingDirectory) ||
					navigationPath.TrimEnd(Path.DirectorySeparatorChar).Equals(
						shellViewModel.WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar),
						StringComparison.OrdinalIgnoreCase)) &&
					(TabBarItemParameter?.NavigationParameter is not string navArg ||
					string.IsNullOrEmpty(navArg) ||
					!navArg.StartsWith("tag:"))) // Return if already selected
				{
					if (InstanceViewModel?.FolderSettings is LayoutPreferencesManager fsModel)
						fsModel.IsLayoutModeChanging = false;

					return;
				}

				if (string.IsNullOrEmpty(navigationPath))
					return;

				ItemDisplayFrame.Navigate(
					sourcePageType,
					new NavigationArguments()
					{
						NavPathParam = navigationPath,
						IsAvManagerMode = isAvManagerMode,
						AvLibraryPath = avLibraryPath,
						AssociatedTabInstance = this
					},
					new SuppressNavigationTransitionInfo());
			}

			ToolbarViewModel.PathControlDisplayText = shellViewModel.WorkingDirectory;
		}

		private void FilterTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (e.Key is VirtualKey.Escape &&
				SlimContentPage is BaseGroupableLayoutPage { IsLoaded: true } svb)
				SlimContentPage.ItemManipulationModel.FocusFileList();
		}
	}
}
