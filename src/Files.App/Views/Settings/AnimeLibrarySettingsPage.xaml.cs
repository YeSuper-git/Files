// Copyright (c) Files Community
// Licensed under the MIT License.
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
        var settings = workspace.Settings.Clone();
        var path = new TextBlock { Text = workspace.LibraryPath, TextWrapping = TextWrapping.Wrap };
        var choose = new Button { Content = Strings.AnimeLibraryChooseRoot.GetLocalizedResource() };
        choose.Click += async (_, _) =>
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);
                picker.FileTypeFilter.Add("*");
                var folder = await picker.PickSingleFolderAsync();
                if (folder is null) return;
                workspace.SetLibraryPath(folder.Path);
                path.Text = folder.Path;
            }
            catch (Exception ex) { path.Text = ex.Message; }
        };
        SettingsContent.Children.Add(new SettingsCard { Header = Strings.AnimeLibraryChooseRoot.GetLocalizedResource(), Description = path, Content = choose, HeaderIcon = new FontIcon { Glyph = "\uE8B7" } });
        var prefix = new TextBox { Text = settings.AnimeImageWebsitePrefix, Width = 400, PlaceholderText = "https://example.org/anime/" };
        prefix.LostFocus += (_, _) => { settings.AnimeImageWebsitePrefix = prefix.Text.Trim(); workspace.UpdateSettings(settings); };
        SettingsContent.Children.Add(new SettingsCard { Header = Strings.AnimeImagesPrefix.GetLocalizedResource(), Description = Strings.AnimeImagesPrefixHelp.GetLocalizedResource(), Content = prefix });
        var includedNames = new TextBox { Text = string.Join(", ", settings.AnimeImageIncludedNames), Width = 400 };
        includedNames.LostFocus += (_, _) =>
        {
            settings.AnimeImageIncludedNames = includedNames.Text.Split(new[] { ',', '，', ';', '；', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).Where(value => value.Length > 0).ToList();
            workspace.UpdateSettings(settings);
        };
        SettingsContent.Children.Add(new SettingsCard { Header = Strings.AnimeImagesIncludedNames.GetLocalizedResource(), Description = Strings.AnimeImagesIncludedNamesHelp.GetLocalizedResource(), Content = includedNames });
        var flatten = new ToggleSwitch { IsOn = settings.AnimeFlattenSeasons };
        flatten.Toggled += (_, _) => { settings.AnimeFlattenSeasons = flatten.IsOn; workspace.UpdateSettings(settings); };
        SettingsContent.Children.Add(new SettingsCard { Header = Strings.AnimeLibraryFlattenSeasons.GetLocalizedResource(), Content = flatten });
        void AddNumber(string label, int value, int minimum, int maximum, Action<int> save)
        {
            var box = new NumberBox { Value = value, Minimum = minimum, Maximum = maximum, Width = 160, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            box.ValueChanged += (_, args) => { if (!double.IsNaN(args.NewValue)) { save((int)Math.Clamp(args.NewValue, minimum, maximum)); workspace.UpdateSettings(settings); } };
            SettingsContent.Children.Add(new SettingsCard { Header = label, Content = box });
        }
        AddNumber(Strings.AnimeLibraryPosterWidth.GetLocalizedResource(), settings.AnimePosterWidth, 100, 240, value => settings.AnimePosterWidth = value);
        AddNumber(Strings.AnimeLibraryPosterEpisodeLimit.GetLocalizedResource(), settings.AnimePosterEpisodeLimit, 0, 30, value => settings.AnimePosterEpisodeLimit = value);
        void AddExtensions(string label, List<string> values, Action<List<string>> save)
        {
            var box = new TextBox { Text = string.Join(", ", values), Width = 320 };
            box.LostFocus += (_, _) => { save(box.Text.Split(new[] { ',', '，', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList()); settings.Normalize(); workspace.UpdateSettings(settings); };
            SettingsContent.Children.Add(new SettingsCard { Header = label, Content = box });
        }
        AddExtensions(Strings.AnimeLibraryVideoExtensions.GetLocalizedResource(), settings.VideoExtensions, value => settings.VideoExtensions = value);
        AddExtensions(Strings.AnimeLibraryImageExtensions.GetLocalizedResource(), settings.ImageExtensions, value => settings.ImageExtensions = value);
    }
}
