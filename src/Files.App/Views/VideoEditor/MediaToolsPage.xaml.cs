// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using Windows.ApplicationModel.DataTransfer;
using Files.App.Services.ResourceManager;
using Files.App.Services.VideoEditor;
using Files.App.ViewModels.VideoEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Files.App.Views.VideoEditor;

public sealed record MuxTrackDisplay(string Title, string Details, string Glyph);

public sealed partial class MediaToolsPage : Page
{
	private readonly IResourceWorkspaceService _drama = Ioc.Default.GetRequiredService<IResourceWorkspaceService>();
	private readonly List<(string Path, TextBox Title, ComboBox Language)> _subtitles = [];
	private CancellationTokenSource? _inputCancellation;
	private MediaContainerInfo? _container;
	private int _inputVersion;
	private bool _readingInput;
	public SubtitleMuxQueueService Queue { get; } = Ioc.Default.GetRequiredService<SubtitleMuxQueueService>();
	private string? _video;
	private bool _archiveMode;
	private IShellPage? _shell;

	public static string GetTitle(string path) => (path == "ArchiveInbox" ? Strings.ArchiveInboxTitle : Strings.SubtitleMuxTitle).GetLocalizedResource();

	public MediaToolsPage()
	{
		InitializeComponent();
		NavigationCacheMode = NavigationCacheMode.Required;
		Loaded += Page_Loaded;
		Loaded += (_, _) => { Queue.Changed += Queue_Changed; UpdateQueueEmpty(); };
		Unloaded += (_, _) => Queue.Changed -= Queue_Changed;
	}

	protected override void OnNavigatedTo(NavigationEventArgs e)
	{
		_shell = (e.Parameter as NavigationArguments)?.AssociatedTabInstance;
		_archiveMode = (e.Parameter as NavigationArguments)?.NavPathParam == "ArchiveInbox";
		PageTitle.Text = (_archiveMode ? Strings.ArchiveInboxTitle : Strings.SubtitleMuxTitle).GetLocalizedResource();
		ArchivePanel.Visibility = _archiveMode ? Visibility.Visible : Visibility.Collapsed;
		MuxPanel.Visibility = _archiveMode ? Visibility.Collapsed : Visibility.Visible;
		StatusText.Text = string.Empty;
	}

	private void Page_Loaded(object sender, RoutedEventArgs e)
	{
		if (!_archiveMode) return;
		ArchivePath.Text = ApplicationData.Current.LocalSettings.Values.TryGetValue("ArchiveInboxPath", out var stored) ? stored as string ?? string.Empty : string.Empty;
		if (Directory.Exists(ArchivePath.Text) && _shell is not null) ArchiveInboxActions.Open(_shell, ArchivePath.Text);
	}

	private async Task<StorageFolder?> PickFolderAsync()
	{
		var picker = new FolderPicker();
		picker.FileTypeFilter.Add("*");
		InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);
		return await picker.PickSingleFolderAsync();
	}

	private async void ChooseArchiveFolder_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			var folder = await PickFolderAsync();
			if (folder is null) return;
			ApplicationData.Current.LocalSettings.Values["ArchiveInboxPath"] = folder.Path;
			ArchivePath.Text = folder.Path;
			if (Directory.Exists(ArchivePath.Text) && _shell is not null) ArchiveInboxActions.Open(_shell, ArchivePath.Text);
		}
		catch (Exception ex) { ShowError(ex); }
	}


	private async void ChooseMuxVideo_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			var picker = new FileOpenPicker();
			foreach (var extension in _drama.Settings.VideoExtensions) picker.FileTypeFilter.Add("." + extension);
			InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);
			if (await picker.PickSingleFileAsync() is { } video) await LoadMuxVideoAsync(video.Path);
		}
		catch (Exception ex) { ShowError(ex); }
	}

	private async Task LoadMuxVideoAsync(string path)
	{
		ResetWorkspace();
		var version = _inputVersion;
		using var operation = new CancellationTokenSource();
		_inputCancellation = operation;
		_readingInput = true;
		MuxInputLoading.Visibility = Visibility.Visible;
		MuxInputLoading.IsActive = true;
		try
		{
			var container = await Ioc.Default.GetRequiredService<VideoProbeService>().ReadContainerAsync(path, operation.Token);
			if (version != _inputVersion) return;
			if (!container.Streams.Any(track => track.Kind == "video")) throw new InvalidDataException(Strings.VideoEditorNoVideoStream.GetLocalizedResource());
			_video = path;
			_container = container;
			MuxVideoPath.Text = Path.GetFileName(path);
			Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(MuxVideoPath, path);
			SourceSummary.Text = $"{container.Format} · {container.Size.ToSizeString()} · " + TimeSpan.FromSeconds(container.Duration).ToString(@"hh\:mm\:ss");
			SourceTracks.ItemsSource = BuildContainerTracks(container, false);
			MuxWorkspaceEmpty.Visibility = Visibility.Collapsed;
			MuxOutputFolder.Text = Path.GetDirectoryName(path);
			MuxOutputName.Text = Path.GetFileNameWithoutExtension(path) + Strings.SubtitleMuxOutputSuffix.GetLocalizedResource() + ".mkv";
			StatusText.Text = string.Empty;
		}
		catch (OperationCanceledException) { }
		finally
		{
			if (version == _inputVersion)
			{
				_inputCancellation = null;
				_readingInput = false;
				MuxInputLoading.IsActive = false;
				MuxInputLoading.Visibility = Visibility.Collapsed;
				UpdateMuxInputs();
				UpdateOutputPreview();
			}
		}
	}

	private async void AddSubtitles_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (_video is null) throw new InvalidOperationException(Strings.SubtitleMuxSelectVideoFirst.GetLocalizedResource());
			var picker = new FileOpenPicker();
			foreach (var extension in new[] { ".srt", ".ass", ".ssa", ".vtt" }) picker.FileTypeFilter.Add(extension);
			InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);
			foreach (var file in await picker.PickMultipleFilesAsync()) AddSubtitlePath(file.Path);
		}
		catch (Exception ex) { ShowError(ex); }
	}

	private void AddSubtitlePath(string path)
	{
		if (_subtitles.Any(track => string.Equals(track.Path, path, StringComparison.OrdinalIgnoreCase))) return;
		AddSubtitleRow(path);
		UpdateDefaultTracks();
		UpdateMuxInputs();
		UpdateOutputPreview();
	}

	private void Mux_DragOver(object sender, DragEventArgs e)
	{
		if (e.DataView.Contains(StandardDataFormats.StorageItems))
		{
			e.AcceptedOperation = DataPackageOperation.Copy;
			e.DragUIOverride.Caption = Strings.SubtitleMuxDropHint.GetLocalizedResource();
		}
	}

	private async void Mux_Drop(object sender, DragEventArgs e)
	{
		var deferral = e.GetDeferral();
		try
		{
			if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
			var files = (await e.DataView.GetStorageItemsAsync()).OfType<StorageFile>().ToArray();
			var videos = files.Where(file => _drama.Settings.VideoExtensions.Contains(file.FileType.TrimStart('.'), StringComparer.OrdinalIgnoreCase)).ToArray();
			if (videos.Length > 1) throw new InvalidOperationException(Strings.SubtitleMuxMultipleVideos.GetLocalizedResource());
			if (videos.Length == 1) await LoadMuxVideoAsync(videos[0].Path);
			var subtitles = files.Where(file => new[] { ".srt", ".ass", ".ssa", ".vtt" }.Contains(file.FileType, StringComparer.OrdinalIgnoreCase)).ToArray();
			if (subtitles.Length > 0 && _video is null) throw new InvalidOperationException(Strings.SubtitleMuxSelectVideoFirst.GetLocalizedResource());
			foreach (var file in subtitles) AddSubtitlePath(file.Path);
		}
		catch (Exception ex) { ShowError(ex); }
		finally { deferral.Complete(); }
	}

	private void AddSubtitleRow(string path)
	{
		var row = new StackPanel { Spacing = 8, Padding = new Thickness(12), Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] };
		var header = new Grid();
		header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		header.Children.Add(new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
		var remove = new Button { Content = new FontIcon { Glyph = "\uE711", FontSize = 12 } };
		AutomationProperties.SetName(remove, Strings.SubtitleMuxRemoveTrack.GetLocalizedResource());
		Grid.SetColumn(remove, 1); header.Children.Add(remove); row.Children.Add(header);
		var controls = new Grid { ColumnSpacing = 12 };
		controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
		var title = new TextBox { Header = Strings.SubtitleMuxTrackName.GetLocalizedResource(), Text = Path.GetFileNameWithoutExtension(path) };
		var language = new ComboBox { Header = Strings.SubtitleMuxLanguage.GetLocalizedResource(), HorizontalAlignment = HorizontalAlignment.Stretch };
		foreach (var (label, tag) in new[] { (Strings.SubtitleMuxChinese.GetLocalizedResource(), "chi"), (Strings.SubtitleMuxEnglish.GetLocalizedResource(), "eng"), (Strings.SubtitleMuxJapanese.GetLocalizedResource(), "jpn"), (Strings.SubtitleMuxUnknown.GetLocalizedResource(), "und") })
			language.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
		language.SelectedIndex = 0;
		Grid.SetColumn(language, 1); controls.Children.Add(title); controls.Children.Add(language); row.Children.Add(controls);
		title.TextChanged += (_, _) => UpdateOutputPreview();
		language.SelectionChanged += (_, _) => UpdateOutputPreview();
		var track = (path, title, language);
		_subtitles.Add(track); SubtitleRows.Children.Add(row);
		remove.Click += (_, _) => { _subtitles.Remove(track); SubtitleRows.Children.Remove(row); UpdateDefaultTracks(); UpdateMuxInputs(); UpdateOutputPreview(); };
	}

	private void UpdateDefaultTracks()
	{
		var selected = DefaultSubtitle.SelectedItem as string;
		var names = _subtitles.Select(track => Path.GetFileName(track.Path)).ToArray();
		DefaultSubtitle.ItemsSource = names;
		DefaultSubtitle.SelectedIndex = Array.IndexOf(names, selected) is var index && index >= 0 ? index : names.Length > 0 ? 0 : -1;
	}

	private async void ChooseOutputFolder_Click(object sender, RoutedEventArgs e)
	{
		try { if (await PickFolderAsync() is { } folder) MuxOutputFolder.Text = folder.Path; }
		catch (Exception ex) { ShowError(ex); }
	}

	private void MuxInput_Changed(object sender, TextChangedEventArgs e) => UpdateMuxInputs();


	private void UpdateMuxInputs()
	{
		if (MergeButton is null) return;
		MergeButton.IsEnabled = !_readingInput && _video is not null && _subtitles.Count > 0
			&& !string.IsNullOrWhiteSpace(MuxOutputFolder.Text) && !string.IsNullOrWhiteSpace(MuxOutputName.Text);
		SubtitleEmpty.Visibility = _subtitles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		SubtitleSettings.Visibility = _subtitles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		AddSubtitleButton.IsEnabled = !_readingInput && _video is not null;
	}

	private List<MuxTrackDisplay> BuildContainerTracks(MediaContainerInfo container, bool output)
	{
		var tracks = container.Streams.Where(track => !output || track.Kind is "video" or "audio" or "subtitle" or "attachment")
			.Select(track => DisplayTrack(output && track.Kind == "subtitle" ? track with { IsDefault = false, Codec = track.Codec == "mov_text" ? "srt" : track.Codec } : track)).ToList();
		if (container.Chapters > 0) tracks.Add(new(Strings.SubtitleMuxChapter.GetLocalizedResource(), container.Chapters.ToString(), "\uE8A5"));
		if (container.HasMetadata) tracks.Add(new(Strings.SubtitleMuxMetadata.GetLocalizedResource(), string.Empty, "\uE8EC"));
		return tracks;
	}

	private static MuxTrackDisplay DisplayTrack(MediaStreamInfo track, bool added = false)
	{
		var (kind, icon) = track.Kind switch
		{
			"video" => (Strings.SubtitleMuxVideoTrack.GetLocalizedResource(), "\uE714"),
			"audio" => (Strings.SubtitleMuxAudioTrack.GetLocalizedResource(), "\uE8D6"),
			"subtitle" => (Strings.SubtitleMuxSubtitleTrack.GetLocalizedResource(), "\uE8F2"),
			"attachment" => (Strings.SubtitleMuxAttachment.GetLocalizedResource(), "\uE723"),
			_ => (track.Kind, "\uE8A5")
		};
		var parts = new List<string> { track.Codec.ToUpperInvariant() };
		if (!string.IsNullOrWhiteSpace(track.Title)) parts.Add(track.Title);
		if (track.Width > 0) parts.Add($"{track.Width} × {track.Height}");
		if (track.Kind is "audio" or "subtitle") parts.Add(track.Language);
		if (track.IsDefault) parts.Add(Strings.SubtitleMuxDefault.GetLocalizedResource());
		if (added) parts.Add(Strings.SubtitleMuxAdded.GetLocalizedResource());
		return new(kind, string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part))), icon);
	}

	private void UpdateOutputPreview()
	{
		if (OutputTracks is null) return;
		var tracks = _container is null ? [] : BuildContainerTracks(_container, true);
		for (var index = 0; index < _subtitles.Count; index++)
		{
			var item = _subtitles[index];
			var language = (item.Language.SelectedItem as ComboBoxItem)?.Tag as string ?? "und";
			var codec = Path.GetExtension(item.Path).TrimStart('.');
			if (codec == "ssa") codec = "ass";
			tracks.Add(DisplayTrack(new("subtitle", codec, item.Title.Text, language, index == DefaultSubtitle.SelectedIndex, 0, 0, 0), true));
		}
		OutputTracks.ItemsSource = tracks;
        MuxOutputEmpty.Visibility = _container is null ? Visibility.Visible : Visibility.Collapsed;
		OutputSummary.Text = _container is null ? string.Empty : string.Format(Strings.SubtitleMuxOutputSummary.GetLocalizedResource(), _subtitles.Count);
	}

	private void MuxSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateOutputPreview();

	private void MergeSubtitles_Click(object sender, RoutedEventArgs e)
	{
		if (_readingInput || _video is null || _subtitles.Count == 0) return;
		try
		{
			var name = MuxOutputName.Text.Trim();
			if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
				throw new ArgumentException(Strings.SubtitleMuxInvalidName.GetLocalizedResource());
			if (!name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException(Strings.SubtitleMuxMkvRequired.GetLocalizedResource());
			var folder = Path.GetFullPath(MuxOutputFolder.Text.Trim());
			if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
			var output = Path.Combine(folder, name);
			var tracks = _subtitles.Select(track => new SubtitleMuxTrack(track.Path, track.Title.Text.Trim(), ((ComboBoxItem)track.Language.SelectedItem).Tag as string ?? "und")).ToArray();
			var encoding = ((ComboBoxItem)SubtitleEncoding.SelectedItem).Tag as string ?? "UTF-8";
			Queue.Enqueue(new(_video, tracks, output, DefaultSubtitle.SelectedIndex, encoding));
			ResetWorkspace();
		}
		catch (Exception ex) { ShowError(ex); }
	}

	private void ResetMux_Click(object sender, RoutedEventArgs e) => ResetWorkspace();

	private void ResetWorkspace()
	{
		++_inputVersion;
		_inputCancellation?.Cancel();
		_inputCancellation = null;
		_readingInput = false;
		_video = null;
		_container = null;
		_subtitles.Clear();
		SubtitleRows.Children.Clear();
		SourceTracks.ItemsSource = null;
		OutputTracks.ItemsSource = null;
        MuxOutputEmpty.Visibility = Visibility.Visible;
		MuxVideoPath.Text = string.Empty;
		SourceSummary.Text = string.Empty;
		OutputSummary.Text = string.Empty;
		MuxOutputFolder.Text = string.Empty;
		MuxOutputName.Text = string.Empty;
		MuxWorkspaceEmpty.Visibility = Visibility.Visible;
		MuxInputLoading.Visibility = Visibility.Collapsed;
		MuxInputLoading.IsActive = false;
		StatusText.Text = string.Empty;
		UpdateDefaultTracks();
		UpdateMuxInputs();
	}

	private void Queue_Changed(object? sender, EventArgs e) => UpdateQueueEmpty();
	private void UpdateQueueEmpty()
	{
		MuxProcessingEmpty.Visibility = Queue.ProcessingJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		MuxCompletedEmpty.Visibility = Queue.CompletedJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	private void MuxJob_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
	{
		if (sender is not FrameworkElement { DataContext: SubtitleMuxJob job } target) return;
		var menu = new MenuFlyout();
		if (job.State is SubtitleMuxJobState.Waiting or SubtitleMuxJobState.Processing)
		{
			var cancel = new MenuFlyoutItem { Text = Strings.SubtitleMuxCancelJob.GetLocalizedResource(), Icon = new FontIcon { Glyph = "\uE711" } };
			cancel.Click += (_, _) => Queue.Cancel(job); menu.Items.Add(cancel);
		}
		else
		{
			if (job.State != SubtitleMuxJobState.Completed)
			{
				var retry = new MenuFlyoutItem { Text = Strings.VideoEditorRetry.GetLocalizedResource(), Icon = new FontIcon { Glyph = "\uE72C" } };
				retry.Click += (_, _) => { try { Queue.Enqueue(new(job.Source, job.Tracks, job.Output, job.DefaultTrack, job.Encoding)); Queue.Remove(job); } catch (Exception ex) { ShowError(ex); } };
				menu.Items.Add(retry);
			}
			if (job.State == SubtitleMuxJobState.Completed)
			{
				var open = new MenuFlyoutItem { Text = Strings.VideoEditorOpenFolder.GetLocalizedResource(), Icon = new FontIcon { Glyph = "\uE8B7" } };
				open.Click += async (_, _) => { try { await Windows.System.Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(job.Output)!)); } catch (Exception ex) { ShowError(ex); } };
				menu.Items.Add(open);
			}
			var remove = new MenuFlyoutItem { Text = Strings.SubtitleMuxRemoveJob.GetLocalizedResource() };
			remove.Click += (_, _) => Queue.Remove(job); menu.Items.Add(remove);
		}
		if (e.TryGetPosition(target, out var position)) menu.ShowAt(target, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = position });
		else menu.ShowAt(target);
		e.Handled = true;
	}

	private void ShowError(Exception ex) => StatusText.Text = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
}
