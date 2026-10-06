// Copyright (c) Files Community. Licensed under the MIT License.
using Files.App.Services.ResourceManager;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.WinUI.Controls;

namespace Files.App.UserControls.ResourceManager;

public static class MediaRecognitionSettings
{
    public static void Build(StackPanel SettingsContent, IResourceWorkspaceService workspace, bool includeAnimeLibraryControls = false)
    {
        StackPanel AddSection(string title)
        {
            SettingsContent.Children.Add(new TextBlock { Text = title, Padding = new Thickness(0, 16, 0, 4), FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
            var panel = new StackPanel { Spacing = 8 };
            SettingsContent.Children.Add(panel);
            return panel;
        }
        var library = new StackPanel { Spacing = 8 };
        if (includeAnimeLibraryControls) SettingsContent.Children.Add(library);
        var display = includeAnimeLibraryControls ? AddSection(Strings.AnimeSettingsDisplaySection.GetLocalizedResource()) : new StackPanel();
        var images = AddSection(Strings.AnimeImagesImport.GetLocalizedResource());
        void Commit(Action<Files.App.Data.Models.ResourceManager.ResourceSettings> update)
        {
            var settings = workspace.Settings.Clone();
            update(settings);
            workspace.UpdateSettings(settings);
        }
        void AddEditable(StackPanel section, string header, FrameworkElement editor, Func<string> summary, Action reset, Func<bool> save, string? description = null)
        {
            var content = new StackPanel { Spacing = 8 };
            var view = new Grid { ColumnSpacing = 12 };
            view.ColumnDefinitions.Add(new ColumnDefinition());
            view.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new TextBlock { Text = summary(), TextWrapping = TextWrapping.Wrap, MaxWidth = 400, VerticalAlignment = VerticalAlignment.Center };
            var edit = new Button { Content = Strings.AnimeLibraryEditDetails.GetLocalizedResource() };
            view.Children.Add(text); Grid.SetColumn(edit, 1); view.Children.Add(edit);
            var editing = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
            editing.Children.Add(editor);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            var apply = new Button { Content = Strings.AnimeLibrarySave.GetLocalizedResource(), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            var cancel = new Button { Content = Strings.Cancel.GetLocalizedResource() };
            actions.Children.Add(apply); actions.Children.Add(cancel); editing.Children.Add(actions);
            void Finish() { text.Text = summary(); editing.Visibility = Visibility.Collapsed; view.Visibility = Visibility.Visible; }
            edit.Click += (_, _) => { reset(); view.Visibility = Visibility.Collapsed; editing.Visibility = Visibility.Visible; editor.Focus(FocusState.Programmatic); };
            apply.Click += (_, _) => { if (save()) Finish(); };
            cancel.Click += (_, _) => { reset(); Finish(); };
            content.Children.Add(view); content.Children.Add(editing);
            section.Children.Add(new SettingsCard { Header = header, Description = description ?? string.Empty, Content = content, HeaderIcon = new FontIcon { Glyph = header == Strings.MediaWebsitePrefixes.GetLocalizedResource() ? "\uE774" : "\uE8D2" } });
        }
        if (includeAnimeLibraryControls)
        {
        var rootCard = new SettingsCard { Header = Strings.AnimeLibraryChooseRoot.GetLocalizedResource(), Description = workspace.LibraryPath, HeaderIcon = new FontIcon { Glyph = "\uE8B7" } };
        var choose = new Button { Content = Strings.AnimeLibraryChooseRoot.GetLocalizedResource() };
        choose.Click += async (_, _) =>
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);
                picker.FileTypeFilter.Add("*");
                var folder = await picker.PickSingleFolderAsync();
                if (folder is not null) { workspace.SetLibraryPath(folder.Path); rootCard.Description = folder.Path; }
            }
            catch (Exception ex) { rootCard.Description = ex.Message; }
        };
        rootCard.Content = choose;
        library.Children.Add(rootCard);
        }
        ToggleSwitch AddToggle(StackPanel section, string header, Func<bool> read, Action<Files.App.Data.Models.ResourceManager.ResourceSettings, bool> write, string? description = null)
        {
            var toggle = new ToggleSwitch { IsOn = read() };
            toggle.Toggled += (_, _) => Commit(settings => write(settings, toggle.IsOn));
            section.Children.Add(new SettingsCard { Header = header, Description = description ?? string.Empty, Content = toggle, HeaderIcon = new FontIcon { Glyph = "\uE8A9" } });
            return toggle;
        }
        if (includeAnimeLibraryControls) AddToggle(display, Strings.AnimeLibraryFlattenSeasons.GetLocalizedResource(), () => workspace.Settings.AnimeFlattenSeasons, (settings, value) => settings.AnimeFlattenSeasons = value);
        var prefixes = new StackPanel { Spacing = 8, Width = 400 };
        var prefixRows = new StackPanel { Spacing = 8 };
        var prefixBoxes = new List<TextBox>();
        var prefixError = new TextBlock { Text = Strings.MediaInvalidPrefix.GetLocalizedResource(), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        void AddPrefix(string value)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new TextBox { Text = value, PlaceholderText = "https://example.org/" };
            var remove = new Button { Content = new FontIcon { Glyph = "\uE74D" } };
            ToolTipService.SetToolTip(remove, Strings.Delete.GetLocalizedResource());
            prefixBoxes.Add(box);
            remove.Click += (_, _) => { prefixBoxes.Remove(box); prefixRows.Children.Remove(row); };
            row.Children.Add(box); Grid.SetColumn(remove, 1); row.Children.Add(remove); prefixRows.Children.Add(row);
        }
        var addPrefix = new Button { Content = Strings.MediaAddPrefix.GetLocalizedResource(), HorizontalAlignment = HorizontalAlignment.Left };
        addPrefix.Click += (_, _) => AddPrefix(string.Empty);
        prefixes.Children.Add(prefixRows); prefixes.Children.Add(addPrefix); prefixes.Children.Add(prefixError);
        AddEditable(images, Strings.MediaWebsitePrefixes.GetLocalizedResource(), prefixes,
            () => string.Join("\n", workspace.Settings.MediaWebsitePrefixes),
            () => { prefixBoxes.Clear(); prefixRows.Children.Clear(); foreach (var value in workspace.Settings.MediaWebsitePrefixes) AddPrefix(value); if (prefixBoxes.Count == 0) AddPrefix(workspace.Settings.AnimeImageWebsitePrefix); prefixError.Visibility = Visibility.Collapsed; },
            () =>
            {
                var values = prefixBoxes.Select(box => box.Text.Trim()).Where(value => value.Length > 0).ToArray();
                if (values.Length == 0 || values.Any(value => !Uri.TryCreate(value, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")))
                { prefixError.Visibility = Visibility.Visible; return false; }
                Commit(settings => { settings.MediaWebsitePrefixes = values.ToList(); });
                return true;
            });
        var conditions = new StackPanel { Spacing = 8, Width = 400 };
        var keywordBoxes = new List<TextBox>();
        void AddCondition(string value)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new TextBox { Text = value, PlaceholderText = Strings.AnimeImagesKeywordPlaceholder.GetLocalizedResource() };
            keywordBoxes.Add(box);
            var remove = new Button { Content = new FontIcon { Glyph = "\uE74D" } };
            ToolTipService.SetToolTip(remove, Strings.Delete.GetLocalizedResource());
            remove.Click += (_, _) => { keywordBoxes.Remove(box); conditions.Children.Remove(row); };
            row.Children.Add(box); Grid.SetColumn(remove, 1); row.Children.Add(remove); conditions.Children.Add(row);
        }
        var add = new Button { Content = Strings.AnimeImagesAddCondition.GetLocalizedResource(), HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) => AddCondition(string.Empty);
        var filter = new StackPanel { Spacing = 8 }; filter.Children.Add(conditions); filter.Children.Add(add);
        var keywordSection = new StackPanel();
        AddEditable(keywordSection, Strings.MediaKeywordConditions.GetLocalizedResource(), filter,
            () => string.Join("\n", workspace.Settings.AnimeImageIncludedNames),
            () => { keywordBoxes.Clear(); conditions.Children.Clear(); foreach (var keyword in workspace.Settings.AnimeImageIncludedNames) AddCondition(keyword); if (keywordBoxes.Count == 0) AddCondition(string.Empty); },
            () => { Commit(settings => { settings.AnimeImageIncludedNames = keywordBoxes.SelectMany(box => box.Text.Split(new[] { ',', '\uFF0C', ';', '\uFF1B', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); }); return true; });
        var keywordCard = (SettingsCard)keywordSection.Children[0];
        keywordSection.Children.Clear();
        SettingsExpander SwitchExpander(string header, string glyph, SettingsCard child, Func<bool> read, Action<Files.App.Data.Models.ResourceManager.ResourceSettings, bool> write)
        {
            var toggle = new ToggleSwitch { IsOn = read() };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, header);
            var expander = new SettingsExpander { Header = header, HeaderIcon = new FontIcon { Glyph = glyph }, Content = toggle, IsExpanded = toggle.IsOn };
            child.ClearValue(SettingsCard.HeaderIconProperty);
            child.Visibility = toggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
            expander.Items.Add(child);
            toggle.Toggled += (_, _) =>
            {
                Commit(settings => write(settings, toggle.IsOn));
                child.Visibility = toggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
                expander.DispatcherQueue.TryEnqueue(() => expander.IsExpanded = toggle.IsOn);
            };
            expander.RegisterPropertyChangedCallback(SettingsExpander.IsExpandedProperty, (_, _) =>
            {
                if (!toggle.IsOn && expander.IsExpanded) expander.IsExpanded = false;
            });
            return expander;
        }
        images.Children.Add(SwitchExpander(Strings.MediaKeywordRecognition.GetLocalizedResource(), "\uE721", keywordCard, () => workspace.Settings.ImageKeywordRecognitionEnabled == true, (settings, value) => settings.ImageKeywordRecognitionEnabled = value));
        void AddNumber(StackPanel section, string label, Func<int> read, int minimum, int maximum, Action<Files.App.Data.Models.ResourceManager.ResourceSettings, int> write, string unit = "", string glyph = "\uE8FD")
        {
            var control = new Grid { Width = 180 };
            var box = new TextBox { Text = read().ToString(), InputScope = new Microsoft.UI.Xaml.Input.InputScope { Names = { new Microsoft.UI.Xaml.Input.InputScopeName { NameValue = Microsoft.UI.Xaml.Input.InputScopeNameValue.Number } } } };
            var deleteStyle = new Style(typeof(Button));
            deleteStyle.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
            box.Resources["TextBoxDeleteButtonStyle"] = deleteStyle;
            box.Loaded += (_, _) => HideClearButton(box);
            void HideClearButton(DependencyObject parent)
            {
                for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
                {
                    var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index);
                    if (child is FrameworkElement element && element.Name == "DeleteButton")
                        element.Visibility = Visibility.Collapsed;
                    else HideClearButton(child);
                }
            }
            box.BeforeTextChanging += (_, args) => args.Cancel = args.NewText.Any(character => !char.IsAsciiDigit(character));
            void ApplyValue(int value)
            {
                value = Math.Clamp(value, minimum, maximum);
                box.Text = value.ToString();
                Commit(settings => write(settings, value));
            }
            box.TextChanged += (_, _) =>
            {
                if (int.TryParse(box.Text, out var value) && value >= minimum && value <= maximum)
                    Commit(settings => write(settings, value));
            };
            box.LostFocus += (_, _) => ApplyValue(int.TryParse(box.Text, out var value) ? value : 0);
            control.Children.Add(box);
            if (unit.Length > 0)
            {
                box.Padding = new Thickness(12, 5, 42, 5);
                control.Children.Add(new TextBlock { Text = unit, Margin = new Thickness(0, 0, 12, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            }
            section.Children.Add(new SettingsCard { Header = label, Content = control, HeaderIcon = new FontIcon { Glyph = glyph } });
        }
        var videoSection = new StackPanel();
        AddNumber(videoSection, Strings.MediaVideoMinimumSecondsValue.GetLocalizedResource(), () => workspace.Settings.AnimeVideoMinimumSeconds, 0, 86400, (settings, value) => settings.AnimeVideoMinimumSeconds = value, Strings.MediaSecondsUnit.GetLocalizedResource(), "\uE916");
        var videoCard = (SettingsCard)videoSection.Children[0]; videoSection.Children.Clear();
        images.Children.Add(SwitchExpander(Strings.AnimeVideosMinimumSeconds.GetLocalizedResource(), "\uE916", videoCard, () => workspace.Settings.VideoLengthLimitEnabled == true, (settings, value) => settings.VideoLengthLimitEnabled = value));
        var dimensions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        FrameworkElement DimensionInput(int initial, string label, Action<Files.App.Data.Models.ResourceManager.ResourceSettings, int> write)
        {
            var box = new TextBox { Text = initial.ToString(), Width = 110, Padding = new Thickness(12, 5, 36, 5), InputScope = new Microsoft.UI.Xaml.Input.InputScope { Names = { new Microsoft.UI.Xaml.Input.InputScopeName { NameValue = Microsoft.UI.Xaml.Input.InputScopeNameValue.Number } } } };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, label);
            ToolTipService.SetToolTip(box, label);
            var clearStyle = new Style(typeof(Button)); clearStyle.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
            box.Resources["TextBoxDeleteButtonStyle"] = clearStyle;
            box.BeforeTextChanging += (_, args) => args.Cancel = args.NewText.Any(character => !char.IsAsciiDigit(character));
            box.TextChanged += (_, _) => { if (int.TryParse(box.Text, out var value) && value <= 65535) Commit(settings => write(settings, value)); };
            box.LostFocus += (_, _) =>
            {
                var value = int.TryParse(box.Text, out var parsed) ? Math.Clamp(parsed, 0, 65535) : 0;
                box.Text = value.ToString(); Commit(settings => write(settings, value));
            };
            var input = new Grid { Width = 110 };
            input.Children.Add(box);
            input.Children.Add(new TextBlock { Text = "px", Margin = new Thickness(0, 0, 10, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            return input;
        }
        dimensions.Children.Add(DimensionInput(workspace.Settings.AnimeImageMinimumWidth, Strings.AnimeImagesMinimumWidth.GetLocalizedResource(), (settings, value) => settings.AnimeImageMinimumWidth = value));
        dimensions.Children.Add(new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center });
        dimensions.Children.Add(DimensionInput(workspace.Settings.AnimeImageMinimumHeight, Strings.AnimeImagesMinimumHeight.GetLocalizedResource(), (settings, value) => settings.AnimeImageMinimumHeight = value));
        var dimensionCard = new SettingsCard { Header = Strings.MediaImageDimensionsValue.GetLocalizedResource(), Content = dimensions, HeaderIcon = new FontIcon { Glyph = "\uE91B" } };
        images.Children.Insert(1, SwitchExpander(Strings.MediaImageMinimumDimensions.GetLocalizedResource(), "\uE91B", dimensionCard, () => workspace.Settings.ImageSizeLimitEnabled == true, (settings, value) => settings.ImageSizeLimitEnabled = value));
        if (includeAnimeLibraryControls) AddNumber(display, Strings.AnimeLibraryPosterWidth.GetLocalizedResource(), () => workspace.Settings.AnimePosterWidth, 100, 240, (settings, value) => settings.AnimePosterWidth = value, "px", "\uE91B");
        if (includeAnimeLibraryControls) AddNumber(display, Strings.AnimeLibraryPosterEpisodeLimit.GetLocalizedResource(), () => workspace.Settings.AnimePosterEpisodeLimit, 0, 30, (settings, value) => settings.AnimePosterEpisodeLimit = value);
    }
}
