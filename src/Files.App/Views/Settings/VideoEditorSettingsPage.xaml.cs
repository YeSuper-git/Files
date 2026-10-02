// Copyright (c) Files Community. Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.Logging;
using System.IO;
using Files.App.Services.VideoEditor;
using Microsoft.UI.Xaml.Input;
using Windows.System;

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
        BuildShortcutRows();
		UpdateFolderControls();
	}

    private void BuildShortcutRows()
    {
        ShortcutRows.Children.Clear();
        foreach (var binding in VideoEditorShortcuts.Load(_settings.VideoEditorShortcuts))
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < 3; i++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock { Text = ("VideoEditorShortcut" + binding.Action).GetLocalizedResource(), VerticalAlignment = VerticalAlignment.Center };
            var field = new TextBox { Text = VideoEditorShortcuts.Display(binding), Width = 230, IsReadOnly = true, IsEnabled = false };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(field, label.Text);
            var edit = new Button { Content = Strings.Edit.GetLocalizedResource() };
            var disable = new Button { Content = Strings.VideoEditorShortcutDisable.GetLocalizedResource() };
            edit.Click += (_, _) => { field.IsEnabled = true; field.Text = Strings.VideoEditorShortcutCapture.GetLocalizedResource(); field.Focus(FocusState.Programmatic); };
            field.LostFocus += (_, _) => { field.IsEnabled = false; field.Text = VideoEditorShortcuts.Display(VideoEditorShortcuts.Load(_settings.VideoEditorShortcuts).First(item => item.Action == binding.Action)); };
            field.KeyDown += (_, args) =>
            {
                args.Handled = true;
                if (args.Key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu) return;
                if (args.Key is VirtualKey.Escape or VirtualKey.Tab) { edit.Focus(FocusState.Programmatic); return; }
                var next = new VideoEditorShortcut(binding.Action, args.Key, VideoEditorShortcuts.CurrentModifiers());
                if (!VideoEditorShortcuts.IsSupported(next)) { ShortcutStatus.Text = Strings.VideoEditorShortcutReserved.GetLocalizedResource(); return; }
                var items = VideoEditorShortcuts.Load(_settings.VideoEditorShortcuts);
                if (items.Any(item => item.Action != next.Action && item.Key == next.Key && item.Modifiers == next.Modifiers))
                { ShortcutStatus.Text = Strings.VideoEditorShortcutConflict.GetLocalizedResource(); return; }
                items[items.FindIndex(item => item.Action == next.Action)] = next;
                _settings.VideoEditorShortcuts = VideoEditorShortcuts.Save(items);
                ShortcutStatus.Text = Strings.VideoEditorShortcutSaved.GetLocalizedResource();
                edit.Focus(FocusState.Programmatic);
            };
            disable.Click += (_, _) => { var items = VideoEditorShortcuts.Load(_settings.VideoEditorShortcuts); items[items.FindIndex(item => item.Action == binding.Action)] = binding with { Key = (VirtualKey)0, Modifiers = VirtualKeyModifiers.None }; _settings.VideoEditorShortcuts = VideoEditorShortcuts.Save(items); field.Text = Strings.VideoEditorShortcutDisabled.GetLocalizedResource(); };
            Grid.SetColumn(field, 1); Grid.SetColumn(edit, 2); Grid.SetColumn(disable, 3);
            row.Children.Add(label); row.Children.Add(field); row.Children.Add(edit); row.Children.Add(disable); ShortcutRows.Children.Add(row);
        }
    }
    private void RestoreShortcuts_Click(object sender, RoutedEventArgs e)
    { _settings.VideoEditorShortcuts = string.Empty; BuildShortcutRows(); ShortcutStatus.Text = Strings.VideoEditorShortcutSaved.GetLocalizedResource(); }

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
