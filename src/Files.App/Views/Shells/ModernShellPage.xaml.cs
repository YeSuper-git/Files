// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System.IO;
using Windows.System;
#if FILES_RESOURCE_MANAGER
using Files.App.Data.Models.ResourceManager;
using Files.App.Helpers;
using Files.App.Services.ResourceManager;
#endif

namespace Files.App.Views.Shells
{
	public sealed partial class ModernShellPage : BaseShellPage
	{
		protected override Frame ItemDisplay
			=> ItemDisplayFrame;

		private NavigationInteractionTracker _navigationInteractionTracker;

#if FILES_RESOURCE_MANAGER
		private readonly IResourceWorkspaceService _resourceWorkspaceService = Ioc.Default.GetRequiredService<IResourceWorkspaceService>();
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

#if FILES_RESOURCE_MANAGER
		protected override async void ShellPage_NavigationRequested(object sender, PathNavigationEventArgs e)
#else
		protected override void ShellPage_NavigationRequested(object sender, PathNavigationEventArgs e)
#endif
		{
			if (e.ItemPath is null)
				return;

#if FILES_RESOURCE_MANAGER
			if (ItemDisplayFrame?.Content is ResourceManager.ResourceLibraryPage resourceLibraryPage &&
				await resourceLibraryPage.TryNavigateToResourcePathAsync(e.ItemPath))
				return;
#endif
			if (ItemDisplayFrame is not { } itemDisplayFrame)
				return;

#if FILES_RESOURCE_MANAGER
			if (InstanceViewModel.IsResourceManagerMode &&
				!ResourceManagerPathScope.IsWithinLibrary(e.ItemPath, InstanceViewModel.ResourceLibraryPath))
				return;
#endif

			itemDisplayFrame.Navigate(InstanceViewModel.FolderSettings.GetLayoutType(e.ItemPath), new NavigationArguments()
			{
				NavPathParam = e.ItemPath,
				IsResourceManagerMode = InstanceViewModel.IsResourceManagerMode,
				ResourceLibraryPath = InstanceViewModel.ResourceLibraryPath,
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
#if FILES_RESOURCE_MANAGER
			else if (navParams.NavPath == "ResourceManager")
			{
				NavigateToResourceManager();
			}
			else if (navParams.NavPath == "ResourceManagerTools")
			{
				NavigateToResourceManagerTools();
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
						IsResourceManagerMode = false,
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

#if FILES_RESOURCE_MANAGER
			if (parameters.IsResourceLibraryPage)
			{
				InstanceViewModel.IsResourceManagerMode = false;
				InstanceViewModel.ResourceLibraryPath = null;
				InstanceViewModel.IsPageTypeNotHome = true;
				InstanceViewModel.IsPageTypeRecycleBin = false;
				InstanceViewModel.IsPageTypeMtpDevice = false;
				InstanceViewModel.IsPageTypeFtp = false;
				InstanceViewModel.IsPageTypeZipFolder = false;
				InstanceViewModel.IsPageTypeLibrary = false;
				InstanceViewModel.IsPageTypeCloudDrive = false;
				InstanceViewModel.IsPageTypeSearchResults = false;
				InstanceViewModel.IsPageTypeReleaseNotes = false;
				InstanceViewModel.IsPageTypeSettings = false;
				ToolbarViewModel.SelectedItems = null;
				UpdateResourceAddressBar(parameters);
			}
#endif

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
#if FILES_RESOURCE_MANAGER
			if (ItemDisplayFrame?.Content is ResourceManager.ResourceLibraryPage resourceLibraryPage)
			{
				resourceLibraryPage.NavigateToParentLocation();
				return;
			}
#endif
			if (!ToolbarViewModel.CanNavigateToParent)
				return;

			ToolbarViewModel.CanNavigateToParent = false;
			var workingDirectory = ShellViewModel?.WorkingDirectory;
			if (string.IsNullOrEmpty(workingDirectory))
				return;

#if FILES_RESOURCE_MANAGER
			if (InstanceViewModel.IsResourceManagerMode &&
				ResourceManagerPathScope.IsLibraryRoot(workingDirectory, InstanceViewModel.ResourceLibraryPath))
				return;
#endif

			bool isPathRooted = string.Equals(workingDirectory, PathNormalization.GetPathRoot(workingDirectory), StringComparison.OrdinalIgnoreCase);
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
				string parentDirectoryOfPath = workingDirectory.TrimEnd('\\', '/');

				var lastSlashIndex = parentDirectoryOfPath.LastIndexOf('\\');
				if (lastSlashIndex == -1)
					lastSlashIndex = parentDirectoryOfPath.LastIndexOf('/');
				if (lastSlashIndex != -1)
					parentDirectoryOfPath = workingDirectory.Remove(lastSlashIndex);
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
			InstanceViewModel.IsResourceManagerMode = false;
			InstanceViewModel.ResourceLibraryPath = null;
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
			InstanceViewModel.IsResourceManagerMode = false;
			InstanceViewModel.ResourceLibraryPath = null;
			ItemDisplayFrame.Navigate(
				typeof(ReleaseNotesPage),
				new NavigationArguments()
				{
					NavPathParam = "ReleaseNotes",
					AssociatedTabInstance = this
				},
				new SuppressNavigationTransitionInfo());
		}

#if FILES_RESOURCE_MANAGER
		public override void NavigateToResourceManager()
		{
			var libraryPath = _resourceWorkspaceService.LibraryPath;
			InstanceViewModel.IsResourceManagerMode = false;
			InstanceViewModel.ResourceLibraryPath = null;
			ToolbarViewModel.PathControlDisplayText = "资源管理";
			ToolbarViewModel.CanNavigateToParent = false;
			if (!string.IsNullOrWhiteSpace(libraryPath) && Directory.Exists(libraryPath))
			{
				// The resource library is a dedicated application view. It must not
				// reuse the drive-backed native layout, otherwise the configured path
				// is shown as a normal child of its drive.
				ItemDisplayFrame.Navigate(
					typeof(ResourceManager.ResourceLibraryPage),
					new NavigationArguments()
					{
						NavPathParam = "ResourceManager",
						IsResourceManagerMode = false,
						ResourceLibraryPath = libraryPath,
						IsResourceLibraryPage = true,
						ResourceLocationPaths = [libraryPath],
						ResourceLocationTitles = [libraryPath],
						ResourceLocationKinds = [ResourceBrowserLocationKind.LibraryRoot],
						AssociatedTabInstance = this
					},
					new SuppressNavigationTransitionInfo());
				return;
			}

			InstanceViewModel.IsResourceManagerMode = false;
			InstanceViewModel.ResourceLibraryPath = null;
			ItemDisplayFrame.Navigate(
				typeof(ResourceManager.ResourceManagerPage),
				new NavigationArguments()
				{
					NavPathParam = "ResourceManager",
					AssociatedTabInstance = this
				},
				new SuppressNavigationTransitionInfo());
		}

		public override void NavigateToResourceManagerTools()
		{
			InstanceViewModel.IsResourceManagerMode = true;
			InstanceViewModel.ResourceLibraryPath = _resourceWorkspaceService.LibraryPath;
			ItemDisplayFrame.Navigate(
				typeof(ResourceManager.ResourceManagerPage),
				new NavigationArguments()
				{
					NavPathParam = "ResourceManagerTools",
					IsResourceManagerMode = true,
					ResourceLibraryPath = _resourceWorkspaceService.LibraryPath,
					AssociatedTabInstance = this
				},
				new SuppressNavigationTransitionInfo());
		}

		public override void NavigateToResourceLibraryLocation(NavigationArguments arguments)
		{
			if (ItemDisplayFrame is not { } itemDisplayFrame)
				return;

			arguments.NavPathParam = "ResourceManager";
			arguments.IsResourceLibraryPage = true;
			arguments.IsResourceManagerMode = false;
			arguments.ResourceLibraryPath ??= _resourceWorkspaceService.LibraryPath;
			arguments.AssociatedTabInstance = this;
			ToolbarViewModel.SelectedItems = null;
			itemDisplayFrame.Navigate(
				typeof(ResourceManager.ResourceLibraryPage),
				arguments,
				new SuppressNavigationTransitionInfo());
		}
#endif

		public override void NavigateToSettings(string? selectItem = null)
		{
			InstanceViewModel.IsResourceManagerMode = false;
			InstanceViewModel.ResourceLibraryPath = null;
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

#if FILES_RESOURCE_MANAGER
		public override async void NavigateToPath(string? navigationPath, Type? sourcePageType, NavigationArguments? navArgs = null)
#else
		public override void NavigateToPath(string? navigationPath, Type? sourcePageType, NavigationArguments? navArgs = null)
#endif
		{
#if FILES_RESOURCE_MANAGER
			if (ItemDisplayFrame?.Content is ResourceManager.ResourceLibraryPage resourceLibraryPage &&
				!string.IsNullOrWhiteSpace(navigationPath) &&
				await resourceLibraryPage.TryNavigateToResourcePathAsync(navigationPath))
				return;
#endif
			if (ItemDisplayFrame is not { } itemDisplayFrame)
				return;

			var shellViewModel = ShellViewModel!;
			shellViewModel.FilesAndFoldersFilter = null;
			var isResourceManagerMode = false;
			string? resourceLibraryPath = null;

#if FILES_RESOURCE_MANAGER
			isResourceManagerMode = navArgs?.IsResourceManagerMode == true || InstanceViewModel.IsResourceManagerMode;
			resourceLibraryPath = navArgs?.ResourceLibraryPath ?? InstanceViewModel.ResourceLibraryPath;
			if (isResourceManagerMode &&
				!ResourceManagerPathScope.IsWithinLibrary(navigationPath, resourceLibraryPath))
				return;

			if (isResourceManagerMode)
			{
				navArgs ??= new NavigationArguments();
				navArgs.IsResourceManagerMode = true;
				navArgs.ResourceLibraryPath = resourceLibraryPath;
			}
#endif

			if (sourcePageType is null && !string.IsNullOrEmpty(navigationPath))
				sourcePageType = InstanceViewModel.FolderSettings.GetLayoutType(navigationPath);

			if (navArgs is not null && navArgs.AssociatedTabInstance is not null)
			{
				itemDisplayFrame.Navigate(
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

				itemDisplayFrame.Navigate(
					sourcePageType,
					new NavigationArguments()
					{
						NavPathParam = navigationPath,
						IsResourceManagerMode = isResourceManagerMode,
					ResourceLibraryPath = resourceLibraryPath,
						AssociatedTabInstance = this
					},
					new SuppressNavigationTransitionInfo());
			}

			ToolbarViewModel.PathControlDisplayText = shellViewModel.WorkingDirectory;
		}

#if FILES_RESOURCE_MANAGER
		private void UpdateResourceAddressBar(NavigationArguments arguments)
		{
			var paths = arguments.ResourceLocationPaths;
			var titles = arguments.ResourceLocationTitles;
			if (paths is null || paths.Length == 0)
				return;

			try
			{
				ToolbarViewModel.PathComponents.Clear();
				for (var index = 0; index < paths.Length; index++)
				{
					var title = titles is not null && index < titles.Length && !string.IsNullOrWhiteSpace(titles[index])
						? titles[index]
						: paths[index];
					ToolbarViewModel.PathComponents.Add(new PathBoxItem
					{
						Path = paths[index],
						Title = title,
						ChevronToolTip = title,
						ChevronVisibilityOverride = false,
					});
				}

				ToolbarViewModel.PathControlDisplayText = paths[^1];
				ToolbarViewModel.CanRefresh = true;
				ToolbarViewModel.CanNavigateToParent = paths.Length > 1;
				ToolbarViewModel.CanGoBack = ItemDisplayFrame.CanGoBack;
				ToolbarViewModel.CanGoForward = ItemDisplayFrame.CanGoForward;
			}
			catch (NullReferenceException)
			{
				// A breadcrumb subscriber can be detached while the frame changes pages.
			}
		}
#endif

		private void FilterTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (e.Key is VirtualKey.Escape &&
				SlimContentPage is BaseGroupableLayoutPage { IsLoaded: true } svb)
				SlimContentPage.ItemManipulationModel.FocusFileList();
		}
	}
}
