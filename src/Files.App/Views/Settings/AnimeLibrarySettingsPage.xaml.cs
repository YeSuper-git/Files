// Copyright (c) Files Community. Licensed under the MIT License.
using Files.App.Services.ResourceManager;
using Files.App.UserControls.ResourceManager;
using Microsoft.UI.Xaml.Controls;

namespace Files.App.Views.Settings;

public sealed partial class AnimeLibrarySettingsPage : Page
{
    public AnimeLibrarySettingsPage()
    {
        InitializeComponent();
        MediaRecognitionSettings.Build(SettingsContent, Ioc.Default.GetRequiredService<AnimeLibraryService>().Workspace, true);
    }
}
