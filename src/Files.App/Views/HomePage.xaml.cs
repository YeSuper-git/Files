// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
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
		private TextBox homeAssistantInput = null!;
		private TextBlock homeAssistantStatusText = null!;
		private IShellPage AppInstance
			=> appInstance ?? throw new InvalidOperationException("The home page has not been initialized.");

		// Constructor

		public HomePage()
		{
			InitializeComponent();
			AttachHomeAssistantPanel();
		}

		private void AttachHomeAssistantPanel()
		{
			if (Content is not UIElement widgetsContent)
				return;

			var host = new Grid();
			host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

			Content = null;
			Grid.SetRow(widgetsContent, 0);
			host.Children.Add(widgetsContent);

			var assistantPanel = CreateHomeAssistantPanel();
			Grid.SetRow(assistantPanel, 1);
			host.Children.Add(assistantPanel);
			Content = host;
		}

		private Border CreateHomeAssistantPanel()
		{
			var stack = new StackPanel { Spacing = 10 };
			var heading = new StackPanel { Spacing = 2 };
			heading.Children.Add(new TextBlock
			{
				Text = "今天想看点什么？",
				FontSize = 16,
				FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
			});
			heading.Children.Add(new TextBlock
			{
				Text = "选一个提示词开始，或者直接描述你想看的作品。",
				Foreground = GetAppResource<Brush>("TextFillColorSecondaryBrush"),
				TextWrapping = TextWrapping.Wrap,
			});
			stack.Children.Add(heading);

			var promptButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
			AddPromptButton(promptButtons, "今天想看哪位演员？", "今天想看哪位演员的作品？");
			AddPromptButton(promptButtons, "推荐没看过的", "推荐没看过的作品");
			AddPromptButton(promptButtons, "想二刷已看过的", "想二刷已看过的作品");
			AddPromptButton(promptButtons, "直接随机推荐", "直接随机推荐几部作品");
			stack.Children.Add(new ScrollViewer
			{
				HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
				VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
				Content = promptButtons,
			});

			homeAssistantInput = new TextBox
			{
				MinHeight = 48,
				MaxHeight = 96,
				AcceptsReturn = true,
				PlaceholderText = "输入演员名、观看状态或想找的内容……",
				TextWrapping = TextWrapping.Wrap,
			};
			homeAssistantInput.KeyDown += HomeAssistantInput_KeyDown;

			var inputRow = new Grid { ColumnSpacing = 8 };
			inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			inputRow.Children.Add(homeAssistantInput);

			var sendButton = new Button
			{
				Content = "开始对话",
				MinWidth = 112,
				VerticalAlignment = VerticalAlignment.Bottom,
				Style = GetAppResource<Style>("AccentButtonStyle"),
			};
			sendButton.Click += SendHomeAssistant_Click;
			Grid.SetColumn(sendButton, 1);
			inputRow.Children.Add(sendButton);
			stack.Children.Add(inputRow);

			homeAssistantStatusText = new TextBlock
			{
				Foreground = GetAppResource<Brush>("SystemFillColorCriticalBrush"),
				TextWrapping = TextWrapping.Wrap,
				Visibility = Visibility.Collapsed,
			};
			stack.Children.Add(homeAssistantStatusText);

			return new Border
			{
				MaxWidth = 920,
				Margin = new Thickness(24, 8, 24, 16),
				Padding = new Thickness(18, 14),
				HorizontalAlignment = HorizontalAlignment.Center,
				Background = GetAppResource<Brush>("CardBackgroundFillColorDefaultBrush"),
				BorderBrush = GetAppResource<Brush>("CardStrokeColorDefaultBrush"),
				BorderThickness = new Thickness(1),
				CornerRadius = new CornerRadius(16),
				Child = stack,
			};
		}

		private void AddPromptButton(Panel panel, string label, string prompt)
		{
			var button = new Button { Content = label, Tag = prompt };
			button.Click += AssistantPromptButton_Click;
			panel.Children.Add(button);
		}

		private static T? GetAppResource<T>(string key) where T : class
		{
			try
			{
				return Application.Current.Resources[key] as T;
			}
			catch
			{
				return null;
			}
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
			=> await OpenVideoAssistantAsync(homeAssistantInput.Text);

		private async void HomeAssistantInput_KeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (e.Key != VirtualKey.Enter || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
				return;

			e.Handled = true;
			await OpenVideoAssistantAsync(homeAssistantInput.Text);
		}

		private async Task OpenVideoAssistantAsync(string? initialPrompt = null)
		{
			if (isVideoAssistantOpening)
				return;

			isVideoAssistantOpening = true;
			try
			{
				homeAssistantStatusText.Text = string.Empty;
				homeAssistantStatusText.Visibility = Visibility.Collapsed;
				var workspace = Ioc.Default.GetRequiredService<IResourceWorkspaceService>();
				var assistantView = new VideoAssistantChatView();
				var dialog = new ContentDialog
				{
					Content = assistantView,
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
				homeAssistantStatusText.Text = $"视频助手打开失败：{ex.Message}";
				homeAssistantStatusText.Visibility = Visibility.Visible;
			}
			finally
			{
				isVideoAssistantOpening = false;
				homeAssistantInput.Text = string.Empty;
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
				assistantView.ViewModel.ShowNotice($"视频助手初始化失败：{ex.Message}");
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
