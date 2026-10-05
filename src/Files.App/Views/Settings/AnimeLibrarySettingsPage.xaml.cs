// Copyright (c) Files Community. Licensed under the MIT License.
using Files.App.Services.ResourceManager;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.WinUI.Controls;

namespace Files.App.Views.Settings;

public sealed partial class AnimeLibrarySettingsPage : Page
{
    public AnimeLibrarySettingsPage()
    {
        InitializeComponent();
        var workspace = Ioc.Default.GetRequiredService<AnimeLibraryService>().Workspace;
        StackPanel AddSection(string title)
        {
            SettingsContent.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 16, 0, 4), Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
            var panel = new StackPanel { Spacing = 8 };
            SettingsContent.Children.Add(panel);
            return panel;
        }
        var library = AddSection(Strings.AnimeSettingsLibrarySection.GetLocalizedResource());
        var display = AddSection(Strings.AnimeSettingsDisplaySection.GetLocalizedResource());
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
            section.Children.Add(new SettingsCard { Header = header, Description = description ?? string.Empty, Content = content });
        }
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
        void AddToggle(StackPanel section, string header, Func<bool> read, Action<Files.App.Data.Models.ResourceManager.ResourceSettings, bool> write, string? description = null)
        {
            var toggle = new ToggleSwitch { IsOn = read() };
            toggle.Toggled += (_, _) => Commit(settings => write(settings, toggle.IsOn));
            section.Children.Add(new SettingsCard { Header = header, Description = description ?? string.Empty, Content = toggle });
        }
        AddToggle(display, Strings.AnimeLibraryFlattenSeasons.GetLocalizedResource(), () => workspace.Settings.AnimeFlattenSeasons, (settings, value) => settings.AnimeFlattenSeasons = value);
        var prefix = new TextBox { Width = 400, PlaceholderText = "https://example.org/anime/" };
        AddEditable(images, Strings.AnimeImagesPrefix.GetLocalizedResource(), prefix, () => workspace.Settings.AnimeImageWebsitePrefix,
            () => prefix.Text = workspace.Settings.AnimeImageWebsitePrefix, () => { Commit(settings => settings.AnimeImageWebsitePrefix = prefix.Text.Trim()); return true; }, Strings.AnimeImagesPrefixHelp.GetLocalizedResource());
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
        var mode = new ComboBox { Width = 320 };
        mode.Items.Add(Strings.AnimeImagesMatchAny.GetLocalizedResource()); mode.Items.Add(Strings.AnimeImagesMatchAll.GetLocalizedResource());
        mode.SelectedIndex = workspace.Settings.AnimeImageMatchAllNames ? 1 : 0;
        mode.SelectionChanged += (_, _) => Commit(settings => settings.AnimeImageMatchAllNames = mode.SelectedIndex == 1);
        images.Children.Add(new SettingsCard { Header = Strings.AnimeImagesMatchMode.GetLocalizedResource(), Content = mode });
        var filter = new StackPanel { Spacing = 8 }; filter.Children.Add(conditions); filter.Children.Add(add);
        AddEditable(images, Strings.AnimeImagesIncludedNames.GetLocalizedResource(), filter,
            () => string.Join(", ", workspace.Settings.AnimeImageIncludedNames),
            () => { keywordBoxes.Clear(); conditions.Children.Clear(); foreach (var keyword in workspace.Settings.AnimeImageIncludedNames) AddCondition(keyword); if (keywordBoxes.Count == 0) AddCondition(string.Empty); },
            () => { Commit(settings => { settings.AnimeImageIncludedNames = keywordBoxes.SelectMany(box => box.Text.Split(new[] { ',', '\uFF0C', ';', '\uFF1B', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); }); return true; }, Strings.AnimeImagesIncludedNamesHelp.GetLocalizedResource());
        AddToggle(images, Strings.AnimeImagesGroupByPrefix.GetLocalizedResource(), () => workspace.Settings.AnimeImageGroupByPrefix, (settings, value) => settings.AnimeImageGroupByPrefix = value, Strings.AnimeImagesGroupByPrefixHelp.GetLocalizedResource());
        void AddNumber(StackPanel section, string label, Func<int> read, int minimum, int maximum, Action<Files.App.Data.Models.ResourceManager.ResourceSettings, int> write)
        {
            var control = new Grid { Width = 180, ColumnSpacing = 4 };
            control.ColumnDefinitions.Add(new ColumnDefinition());
            control.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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
            var spins = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            foreach (var step in new[] { 1, -1 })
            {
                var button = new Button { Content = new FontIcon { Glyph = step > 0 ? "\uE70E" : "\uE70D", FontSize = 10 }, Width = 28, Height = 16, MinHeight = 0, Padding = new Thickness(0) };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label + (step > 0 ? " +1" : " -1"));
                button.Click += (_, _) => ApplyValue((int.TryParse(box.Text, out var value) ? value : 0) + step);
                spins.Children.Add(button);
            }
            control.Children.Add(box);
            Grid.SetColumn(spins, 1); control.Children.Add(spins);
            section.Children.Add(new SettingsCard { Header = label, Content = control });
        }
        AddNumber(images, Strings.AnimeImagesMinimumWidth.GetLocalizedResource(), () => workspace.Settings.AnimeImageMinimumWidth, 0, 65535, (settings, value) => settings.AnimeImageMinimumWidth = value);
        AddNumber(images, Strings.AnimeImagesMinimumHeight.GetLocalizedResource(), () => workspace.Settings.AnimeImageMinimumHeight, 0, 65535, (settings, value) => settings.AnimeImageMinimumHeight = value);
        AddNumber(display, Strings.AnimeLibraryPosterWidth.GetLocalizedResource(), () => workspace.Settings.AnimePosterWidth, 100, 240, (settings, value) => settings.AnimePosterWidth = value);
        AddNumber(display, Strings.AnimeLibraryPosterEpisodeLimit.GetLocalizedResource(), () => workspace.Settings.AnimePosterEpisodeLimit, 0, 30, (settings, value) => settings.AnimePosterEpisodeLimit = value);
    }
}
