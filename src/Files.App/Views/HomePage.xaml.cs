// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Extensions.Logging;
using Files.App.Data.Models;
using Files.App.Services.ResourceManager;
using Files.App.UserControls.Assistant;
using Files.App.ViewModels.Assistant;
using Windows.System;
using WinRT;

namespace Files.App.Views
{
	public sealed partial class HomePage : Page, IDisposable
	{
		// Dependency injections

		private readonly ICommandManager Commands = Ioc.Default.GetRequiredService<ICommandManager>();
		public HomeViewModel ViewModel { get; } = Ioc.Default.GetRequiredService<HomeViewModel>();

		// Properties

		private IShellPage? appInstance;
		private bool isVideoAssistantOpening;
		private IShellPage AppInstance
			=> appInstance ?? throw new InvalidOperationException("The home page has not been initialized.");

		// Constructor

		public HomePage()
		{
			InitializeComponent();
		}

		private void HomePage_Loaded(object sender, RoutedEventArgs e)
		{
			if (ViewModel.ReloadWidgetsCommand.CanExecute(e))
				ViewModel.ReloadWidgetsCommand.Execute(e);
		}

		private async void AssistantPromptButton_Click(object sender, RoutedEventArgs e)
		{
			if (sender is Button { Tag: string prompt })
				await OpenVideoAssistantAsync(prompt);
		}

		private async void SendHomeAssistant_Click(object sender, RoutedEventArgs e)
			=> await OpenVideoAssistantAsync(HomeAssistantInput.Text);

		private async void HomeAssistantInput_KeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (e.Key != VirtualKey.Enter || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
				return;

			e.Handled = true;
			await OpenVideoAssistantAsync(HomeAssistantInput.Text);
		}

		private async Task OpenVideoAssistantAsync(string? initialPrompt = null)
		{
			if (isVideoAssistantOpening)
				return;

			isVideoAssistantOpening = true;
			try
			{
				HomeAssistantStatusText.Text = string.Empty;
				HomeAssistantStatusText.Visibility = Visibility.Collapsed;
				var workspace = Ioc.Default.GetRequiredService<IResourceWorkspaceService>();
				var assistantView = new VideoAssistantChatView();
				var dialog = new ContentDialog
				{
					Content = assistantView,
					Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
					CloseButtonText = "关闭",
					DefaultButton = ContentDialogButton.Close,
					MaxWidth = 860,
					XamlRoot = XamlRoot,
				};
				assistantView.CloseRequested += (_, _) => dialog.Hide();
				dialog.Opened += (_, _) => _ = ConfigureVideoAssistantAsync(assistantView, workspace, initialPrompt, dialog);
				await dialog.ShowAsync();
			}
			catch (Exception ex)
			{
				App.Logger.LogError(ex, "Unable to open the video assistant from the home page");
				HomeAssistantStatusText.Text = $"小咪打开失败：{ex.Message}";
				HomeAssistantStatusText.Visibility = Visibility.Visible;
			}
			finally
			{
				isVideoAssistantOpening = false;
				HomeAssistantInput.Text = string.Empty;
			}
		}

		private async Task ConfigureVideoAssistantAsync(
			VideoAssistantChatView assistantView,
			IResourceWorkspaceService workspace,
			string? initialPrompt,
			ContentDialog dialog)
		{
			try
			{
				await assistantView.ConfigureAsync(new VideoAssistantContext
				{
					LibraryPath = workspace.LibraryPath,
					OpenResourceManagerAsync = () =>
					{
						dialog.Hide();
						AppInstance.NavigateToResourceManager();
						return Task.CompletedTask;
					},
					OpenLocationAsync = candidate =>
					{
						dialog.Hide();
						AppInstance.NavigateToResourceLibraryLocation(new NavigationArguments
						{
							NavPathParam = "ResourceManager",
							IsResourceLibraryPage = true,
							ResourceLibraryPath = workspace.LibraryPath,
							ResourceLocationPaths = candidate.LocationPaths.ToArray(),
							ResourceLocationKinds = candidate.LocationKinds.ToArray(),
							ResourceLocationTitles = candidate.LocationTitles.ToArray(),
						});
						return Task.CompletedTask;
					},
				});

				if (!string.IsNullOrWhiteSpace(initialPrompt) &&
					!initialPrompt.Contains("今天想看哪位演员", StringComparison.Ordinal))
					await assistantView.ViewModel.SubmitAsync(initialPrompt);
			}
			catch (Exception ex)
			{
				App.Logger.LogError(ex, "Unable to initialize the video assistant conversation");
				assistantView.ViewModel.ShowNotice($"小咪初始化失败：{ex.Message}");
			}
		}

		// Methods

		protected override async void OnNavigatedTo(NavigationEventArgs e)
		{
			if (e.Parameter is not NavigationArguments parameters)
				return;

			appInstance = parameters.AssociatedTabInstance!;
			var shellViewModel = AppInstance.GetRequiredShellViewModel();

			AppInstance.InstanceViewModel.IsPageTypeNotHome = false;
			AppInstance.InstanceViewModel.IsPageTypeSearchResults = false;
			AppInstance.InstanceViewModel.IsPageTypeMtpDevice = false;
			AppInstance.InstanceViewModel.IsPageTypeRecycleBin = false;
			AppInstance.InstanceViewModel.IsPageTypeCloudDrive = false;
			AppInstance.InstanceViewModel.IsPageTypeFtp = false;
			AppInstance.InstanceViewModel.IsPageTypeZipFolder = false;
			AppInstance.InstanceViewModel.IsPageTypeLibrary = false;
			AppInstance.InstanceViewModel.GitRepositoryPath = null;
			AppInstance.InstanceViewModel.IsGitRepository = false;
			AppInstance.InstanceViewModel.IsPageTypeReleaseNotes = false;
			AppInstance.InstanceViewModel.IsPageTypeSettings = false;
			AppInstance.ToolbarViewModel.CanRefresh = true;
			AppInstance.ToolbarViewModel.CanGoBack = AppInstance.CanNavigateBackward;
			AppInstance.ToolbarViewModel.CanGoForward = AppInstance.CanNavigateForward;
			AppInstance.ToolbarViewModel.CanNavigateToParent = false;

			// Set path of working directory empty
			await shellViewModel.SetWorkingDirectoryAsync("Home");
			shellViewModel.CheckForBackgroundImage();

			AppInstance.SlimContentPage?.StatusBarViewModel.UpdateGitInfo(false, string.Empty, null);

			AppInstance.ToolbarViewModel.PathComponents.Clear();

			string componentLabel =
				parameters?.NavPathParam == "Home"
					? Strings.Home.GetLocalizedResource()
					: parameters?.NavPathParam
				?? string.Empty;

			string tag = parameters?.NavPathParam ?? string.Empty;

			var item = new PathBoxItem()
			{
				Title = componentLabel,
				Path = tag,
				ChevronToolTip = string.Format(Strings.BreadcrumbBarChevronButtonToolTip.GetLocalizedResource(), componentLabel),
			};

			AppInstance.ToolbarViewModel.PathComponents.Add(item);

			base.OnNavigatedTo(e);
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private void ScrollViewer_RightTapped(object sender, RightTappedRoutedEventArgs e)
		{
			if (sender is FrameworkElement element)
				HomePageContextMenu.ShowAt(element, e.GetPosition(element));

			e.Handled = true;
		}

		protected override void OnNavigatedFrom(NavigationEventArgs e)
		{
			Dispose();
		}

		// Disposer

		public void Dispose()
		{
			ViewModel?.Dispose();
		}
	}
}
