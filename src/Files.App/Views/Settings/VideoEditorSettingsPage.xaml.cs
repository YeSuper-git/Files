// Copyright (c) Files Community. Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.Logging;
using System.IO;

namespace Files.App.Views.Settings;

public sealed partial class VideoEditorSettingsPage : Page
{
	private readonly IAppSettingsService _settings = Ioc.Default.GetRequiredService<IAppSettingsService>();
	private readonly ICommonDialogService _dialogs = Ioc.Default.GetRequiredService<ICommonDialogService>();
	private bool _initializing = true;
	private bool _updatingFolderMode;

	public VideoEditorSettingsPage()
	{
		InitializeComponent();
		FolderModeComboBox.SelectedIndex = _settings.VideoEditorExportToSourceFolder ? 0 : 1;
		SourceFolderModeComboBox.SelectedIndex = FolderModeComboBox.SelectedIndex;
		FolderPathTextBox.Text = _settings.VideoEditorExportFolder;
		_initializing = false;
		UpdateFolderControls();
	}

	private void FolderModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_initializing || _updatingFolderMode || sender is not ComboBox comboBox || comboBox.SelectedIndex < 0)
			return;
		_updatingFolderMode = true;
		try
		{
			FolderModeComboBox.SelectedIndex = comboBox.SelectedIndex;
			SourceFolderModeComboBox.SelectedIndex = comboBox.SelectedIndex;
			_settings.VideoEditorExportToSourceFolder = comboBox.SelectedIndex == 0;
		}
		finally
		{
			_updatingFolderMode = false;
		}
		UpdateFolderControls();
	}

	private void UpdateFolderControls()
	{
		if (ChooseFolderButton is null || SourceLocationCard is null)
			return;
		var customFolder = FolderModeComboBox.SelectedIndex == 1;
		SourceLocationCard.Visibility = customFolder ? Visibility.Collapsed : Visibility.Visible;
		LocationExpander.Visibility = customFolder ? Visibility.Visible : Visibility.Collapsed;
		if (customFolder)
			LocationExpander.IsExpanded = true;
		FolderStatusText.Text = customFolder && string.IsNullOrWhiteSpace(_settings.VideoEditorExportFolder)
			? Strings.VideoEditorNoFolderSelected.GetLocalizedResource()
			: string.Empty;
	}

	private void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (!_dialogs.Open_FileOpenDialog(MainWindow.Instance.WindowHandle, true, [], Environment.SpecialFolder.Desktop, out var path))
				return;
			if (!Directory.Exists(path))
			{
				FolderStatusText.Text = string.Format(Strings.VideoEditorExportFolderUnavailable.GetLocalizedResource(), path);
				return;
			}
			_settings.VideoEditorExportFolder = path;
			FolderPathTextBox.Text = path;
			UpdateFolderControls();
		}
		catch (Exception ex)
		{
			App.Logger.LogError(ex, "Unable to choose the video export folder");
			FolderStatusText.Text = string.Format(Strings.VideoEditorChooseFolderFailed.GetLocalizedResource(), ex.Message);
		}
	}
}
