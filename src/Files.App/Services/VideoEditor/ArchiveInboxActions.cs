// Copyright (c) Files Community
// Licensed under the MIT License.
using System.IO;
using Files.App.Services.ResourceManager;
using Files.App.ViewModels.VideoEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.Mvvm.Input;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
namespace Files.App.Services.VideoEditor;

public sealed class ArchiveInboxActions(IShellPage shell, XamlRoot xamlRoot)
{
	private readonly IShellPage _shell = shell;
	private readonly XamlRoot XamlRoot = xamlRoot;
	private readonly IResourceWorkspaceService _drama = Ioc.Default.GetRequiredService<IResourceWorkspaceService>();
	private readonly IResourceWorkspaceService _anime = Ioc.Default.GetRequiredService<AnimeLibraryService>().Workspace;
	private readonly VideoEditorViewModel _editor = Ioc.Default.GetRequiredService<VideoEditorViewModel>();
	private readonly VideoCutQueueService _queue = Ioc.Default.GetRequiredService<VideoCutQueueService>();
	private bool _archiveBusy;

	public static void Open(IShellPage shell, string root)
	{
		shell.InstanceViewModel.IsResourceManagerMode = false;
		shell.InstanceViewModel.ResourceLibraryPath = null;
		var layout = shell.InstanceViewModel.FolderSettings.GetLayoutType(root);
		if (layout == typeof(Files.App.Views.Layouts.ColumnsLayoutPage)) layout = typeof(Files.App.Views.Layouts.DetailsLayoutPage);
		shell.NavigateWithArguments(layout, new NavigationArguments
		{
			NavPathParam = root, AssociatedTabInstance = shell,
			IsArchiveInboxPage = true, ArchiveInboxRoot = root
		});
	}

	public async Task ChooseFolderAsync()
	{
		try
		{
			if (await PickFolderAsync() is not { } folder) return;
			ApplicationData.Current.LocalSettings.Values["ArchiveInboxPath"] = folder.Path;
			Open(_shell, folder.Path);
		}
		catch (Exception ex) { await ShowNoticeAsync(ex.Message); }
	}

	public void AddCommands(List<ContextMenuFlyoutItemViewModel> items, List<ListedItem>? selection)
	{
		var extras = new List<ContextMenuFlyoutItemViewModel>();
		if (selection is { Count: 1 } && !selection[0].IsFolder)
		{
			var item = selection[0];
			var file = new ArchiveFileEntry(item.ItemPath!, item.FileSizeBytes, string.Empty,
				_drama.Settings.VideoExtensions.Contains(Path.GetExtension(item.ItemPath!).TrimStart('.'), StringComparer.OrdinalIgnoreCase));
			if (file.IsVideo)
			{
				extras.Add(new() { Text = Strings.VideoEditorTitle.GetLocalizedResource(), Glyph = "\uE70F",
					Command = new AsyncRelayCommand(async () =>
					{
						try
						{
							if (_queue.IsSourceReserved(file.Path)) throw new IOException(Strings.VideoEditorDuplicateVideo.GetLocalizedResource());
							if (!File.Exists(file.Path)) throw new FileNotFoundException(Strings.VideoEditorFileMissing.GetLocalizedResource());
							_editor.AddPendingVideo(file.Path);
						}
						catch (Exception ex) { await ShowNoticeAsync(ex.Message); }
					}) });
				extras.Add(new() { Text = Strings.ArchiveMove.GetLocalizedResource(), Glyph = "\uE8DE",
					Command = new AsyncRelayCommand(() => MoveFileAsync(file)) });
			}
		}
		if (selection is null)
			extras.Add(new() { Text = Strings.ArchiveChooseFolder.GetLocalizedResource(), Glyph = "\uE8B7",
				Command = new AsyncRelayCommand(ChooseFolderAsync) });
		if (extras.Count == 0) return;
		extras.Add(new() { ItemType = ContextMenuFlyoutItemType.Separator });
		items.InsertRange(0, extras);
	}

	private async Task ShowNoticeAsync(string message) => await new ContentDialog
	{
		XamlRoot = XamlRoot, Title = Strings.ArchiveInboxTitle.GetLocalizedResource(),
		Content = message, CloseButtonText = Strings.OK.GetLocalizedResource()
	}.ShowAsync();

	private static async Task<StorageFolder?> PickFolderAsync()
	{
		var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
		InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);
		return await picker.PickSingleFolderAsync();
	}
	private bool IsFileBusy(string path)
	{
		if (_queue.IsSourceReserved(path) || Ioc.Default.GetRequiredService<SubtitleMuxQueueService>().ProcessingJobs.Any(job => string.Equals(job.Source, path, StringComparison.OrdinalIgnoreCase)) || (_editor.HasVideo && string.Equals(_editor.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
		{
			_ = ShowNoticeAsync(Strings.ArchiveFileBusy.GetLocalizedResource());
			return true;
		}
		return false;
	}

	private async Task MoveFileAsync(ArchiveFileEntry file)
	{
		if (_archiveBusy || IsFileBusy(file.Path)) return;
		_archiveBusy = true;
		using var scan = new CancellationTokenSource();
		try
		{
			var library = new ComboBox { Header = Strings.ArchiveTargetLibrary.GetLocalizedResource(), HorizontalAlignment = HorizontalAlignment.Stretch };
			library.Items.Add(Strings.LibraryDramaGroup.GetLocalizedResource());
			library.Items.Add(Strings.LibraryAnimeGroup.GetLocalizedResource());
			var destination = new TextBox { Header = Strings.ArchiveDestinationPath.GetLocalizedResource(), TextWrapping = TextWrapping.Wrap };
			var suggestions = new ComboBox { Header = Strings.ArchiveRecognizedPaths.GetLocalizedResource(), HorizontalAlignment = HorizontalAlignment.Stretch };
			var hint = new TextBlock { TextWrapping = TextWrapping.Wrap };
			var choose = new Button { Content = Strings.ArchiveChooseDestination.GetLocalizedResource() };
			var content = new StackPanel { Spacing = 12, MinWidth = 480 };
			content.Children.Add(new TextBlock { Text = file.FileName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
			foreach (var child in new UIElement[] { library, suggestions, hint, destination, choose }) content.Children.Add(child);
			var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = Strings.ArchiveMove.GetLocalizedResource(), Content = content,
				PrimaryButtonText = Strings.ArchiveMove.GetLocalizedResource(), CloseButtonText = Strings.Cancel.GetLocalizedResource(), IsPrimaryButtonEnabled = false };
			var version = 0;
			library.SelectionChanged += async (_, _) =>
			{
				var current = ++version;
				dialog.IsPrimaryButtonEnabled = false;
				var workspace = library.SelectedIndex == 1 ? _anime : _drama;
				hint.Text = Strings.ArchiveRecognizing.GetLocalizedResource();
				try
				{
					var root = workspace.LibraryPath;
					var found = await Task.Run(() => ArchiveDestinationService.Find(file.Path, root, scan.Token), scan.Token);
					if (scan.IsCancellationRequested || current != version) return;
					suggestions.ItemsSource = found;
					suggestions.SelectedIndex = found.Count > 0 ? 0 : -1;
					destination.Text = found.FirstOrDefault()?.Path ?? root;
					hint.Text = (found.Count > 0 ? Strings.ArchiveRecognitionReady : Strings.ArchiveRecognitionEmpty).GetLocalizedResource();
				}
				catch (OperationCanceledException) { }
				catch (Exception ex) { if (!scan.IsCancellationRequested) hint.Text = ex.Message; }
			};
			suggestions.SelectionChanged += (_, _) => { if (suggestions.SelectedItem is ArchiveDestination item) destination.Text = item.Path; };
			destination.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(destination.Text);
			choose.Click += async (_, _) =>
			{
				try { if (await PickFolderAsync() is { } folder) { ++version; destination.Text = folder.Path; } }
				catch (Exception ex) { hint.Text = ex.Message; }
			};
			library.SelectedIndex = 0;
			var confirmed = await dialog.ShowAsync();
			scan.Cancel();
			if (confirmed != ContentDialogResult.Primary || IsFileBusy(file.Path)) return;
			var targetPath = Path.GetFullPath(destination.Text.Trim());
			if (string.Equals(targetPath.TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(file.Path)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
				throw new IOException(Strings.ArchiveSameFolder.GetLocalizedResource());
			var folder = await StorageFolder.GetFolderFromPathAsync(targetPath);
			var source = await StorageFile.GetFileFromPathAsync(file.Path);
			var movedPath = Path.Combine(folder.Path, file.FileName);
			var result = await _shell.FilesystemHelpers.MoveItemAsync(source, movedPath, true, true);
			if (result != ReturnResult.Success || File.Exists(file.Path)) return;
			if (File.Exists(movedPath)) RemapPending(file.Path, movedPath);
			var workspaceForTags = library.SelectedIndex == 1 ? _anime : _drama;
			workspaceForTags.NotifyNavigationChanged();

			await _shell.Refresh_Click();
		}
		catch (Exception ex) { await ShowNoticeAsync(ex.Message); }
		finally { scan.Cancel(); _archiveBusy = false; }
	}

	private void RemapPending(string source, string? target)
	{
		var old = _editor.PendingVideos.FirstOrDefault(video => string.Equals(video.SourcePath, source, StringComparison.OrdinalIgnoreCase));
		if (old is null) return;
		if (target is not null)
		{
			var replacement = _editor.AddPendingVideo(target);
			replacement.TrimStartSeconds = old.TrimStartSeconds;
			replacement.TrimEndSeconds = old.TrimEndSeconds;
			replacement.CurrentPositionSeconds = old.CurrentPositionSeconds;
			replacement.Segments = old.Segments;
		}
		_editor.PendingVideos.Remove(old);
	}

}
