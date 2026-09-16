using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Forms;

namespace FilesMax.Installer.Bootstrapper;

public partial class InstallerWindow : Window
{
    private const double WindowCornerRadius = 14;
    private bool allowClose;
    private bool suppressFolderChanged;

    public InstallerWindow()
    {
        InitializeComponent();
        InstallFolderTextBox.Text = string.Empty;
    }

    public event EventHandler? NextRequested;

    public event EventHandler? BackRequested;

    public event EventHandler? InstallRequested;

    public event EventHandler? RepairRequested;

    public event EventHandler? UninstallRequested;

    public event EventHandler? LaunchRequested;

    public event EventHandler? CancelRequested;

    public event EventHandler? LicenseChanged;

    public event EventHandler? FinalCloseRequested;

    public string InstallFolder => InstallFolderTextBox.Text.Trim();

    public bool LicenseAccepted => LicenseCheckBox.IsChecked == true;

    public void SetVersion(string version)
    {
        WelcomeVersionText.Text = $"版本 {version}";
    }

    public void SetInstallFolder(string path)
    {
        suppressFolderChanged = true;
        InstallFolderTextBox.Text = path;
        suppressFolderChanged = false;
        InstallButton.IsEnabled = LicenseAccepted && !string.IsNullOrWhiteSpace(path);
    }

    public void ShowWelcome()
    {
        ContentCard.Height = 249;
        SetPage(WelcomePage, WelcomeActions);
        LicenseCheckBox.IsChecked = false;
        NextButton.IsEnabled = false;
    }

    public void ShowOptions()
    {
        ContentCard.Height = 190;
        SetPage(OptionsPage, OptionsActions);
        InstallButton.IsEnabled = LicenseAccepted && !string.IsNullOrWhiteSpace(InstallFolder);
    }

    public void ShowModify()
    {
        ContentCard.Height = 190;
        SetPage(CompletePage, ModifyActions);
        CompleteHeaderText.Text = "修改安装";
        CompleteDescriptionText.Text = "请选择要执行的操作。";
    }

    public void ShowProgress(string header, string message)
    {
        ContentCard.Height = 190;
        SetPage(ProgressPage, ProgressActions);
        ProgressHeaderText.Text = string.IsNullOrWhiteSpace(header) ? "安装进度" : header;
        InstallProgressBar.Value = 0;
        ProgressMessageText.Text = string.IsNullOrWhiteSpace(message) ? "正在准备……" : message;
        ProgressActions.IsEnabled = true;
    }

    public void SetProgress(int percentage, string message)
    {
        InstallProgressBar.Value = Math.Clamp(percentage, 0, 100);
        if (!string.IsNullOrWhiteSpace(message))
            ProgressMessageText.Text = message;
    }

    public void ShowComplete(string header, string description, bool canLaunch)
    {
        ContentCard.Height = 190;
        SetPage(CompletePage, CompleteActions);
        CompleteHeaderText.Text = header;
        CompleteDescriptionText.Text = description;
        LaunchButton.Visibility = canLaunch ? Visibility.Visible : Visibility.Collapsed;
        LaunchButton.IsEnabled = canLaunch;
    }

    public void ShowFailure(string message, string? header = null)
    {
        ContentCard.Height = 249;
        SetPage(FailurePage, FailureActions);
        FailureHeaderText.Text = string.IsNullOrWhiteSpace(header) ? "安装失败" : header;
        FailureMessageText.Text = string.IsNullOrWhiteSpace(message)
            ? "安装程序遇到问题，请查看日志后重试。"
            : message;
    }

    public void SetBusy(bool busy)
    {
        WelcomeActions.IsEnabled = !busy;
        OptionsActions.IsEnabled = !busy;
        ModifyActions.IsEnabled = !busy;
        ProgressActions.IsEnabled = !busy;
        CompleteActions.IsEnabled = !busy;
        FailureActions.IsEnabled = !busy;
        if (!busy && ProgressPage.Visibility == Visibility.Visible)
            ProgressActions.IsEnabled = true;
    }

    public void AllowCloseAndClose()
    {
        allowClose = true;
        Close();
    }

    private void SetPage(FrameworkElement page, FrameworkElement actions)
    {
        WelcomePage.Visibility = Visibility.Collapsed;
        OptionsPage.Visibility = Visibility.Collapsed;
        ProgressPage.Visibility = Visibility.Collapsed;
        CompletePage.Visibility = Visibility.Collapsed;
        FailurePage.Visibility = Visibility.Collapsed;
        WelcomeActions.Visibility = Visibility.Collapsed;
        OptionsActions.Visibility = Visibility.Collapsed;
        ModifyActions.Visibility = Visibility.Collapsed;
        ProgressActions.Visibility = Visibility.Collapsed;
        CompleteActions.Visibility = Visibility.Collapsed;
        FailureActions.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
        actions.Visibility = Visibility.Visible;
        Height = page == WelcomePage || page == FailurePage ? 556 : 498;
    }

    private void LicenseCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        NextButton.IsEnabled = LicenseAccepted;
        InstallButton.IsEnabled = LicenseAccepted && !string.IsNullOrWhiteSpace(InstallFolder);
        LicenseChanged?.Invoke(this, EventArgs.Empty);
    }

    private void InstallFolderTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!suppressFolderChanged)
            InstallButton.IsEnabled = LicenseAccepted && !string.IsNullOrWhiteSpace(InstallFolder);
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 Files max 的安装位置",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = InstallFolder,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            SetInstallFolder(dialog.SelectedPath);
    }

    private void NextButton_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke(this, EventArgs.Empty);

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void InstallButton_Click(object sender, RoutedEventArgs e) => InstallRequested?.Invoke(this, EventArgs.Empty);

    private void RepairButton_Click(object sender, RoutedEventArgs e) => RepairRequested?.Invoke(this, EventArgs.Empty);

    private void UninstallButton_Click(object sender, RoutedEventArgs e) => UninstallRequested?.Invoke(this, EventArgs.Empty);

    private void LaunchButton_Click(object sender, RoutedEventArgs e) => LaunchRequested?.Invoke(this, EventArgs.Empty);

    private void CompleteCloseButton_Click(object sender, RoutedEventArgs e) => FinalCloseRequested?.Invoke(this, EventArgs.Empty);

    private void CancelButton_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        MaximizeIcon.Data = WindowState == WindowState.Maximized
            ? Geometry.Parse("M4,3 H10 V9 M8,11 H2 V5")
            : Geometry.Parse("M4,9 L10,3 M6,3 H10 V7 M2,6 V11 H7");
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = OuterFrame.ActualWidth;
        var height = OuterFrame.ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        OuterFrame.Clip = new RectangleGeometry(
            new Rect(0, 0, width, height),
            WindowCornerRadius,
            WindowCornerRadius);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            MaximizeButton_Click(sender, e);
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!allowClose)
        {
            e.Cancel = true;
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
