using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Forms;
using System.Windows.Interop;

namespace FilesMax.Installer.Bootstrapper;

public partial class InstallerWindow : Window
{
    private const double WindowCornerRadius = 14;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowBorderColor = 34;
    private const uint DwmCornerRound = 2;
    private const uint DwmBorderColor = 0x00E6D9D1;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref uint value,
        uint valueSize);

    private bool allowClose;
    private bool suppressFolderChanged;

    public InstallerWindow()
    {
        InitializeComponent();
        InstallFolderTextBox.Text = string.Empty;
        ApplyDwmWindowPolicy();
    }

    public event EventHandler? NextRequested;

    public event EventHandler? BackRequested;

    public event EventHandler? InstallRequested;

    public event EventHandler? RepairRequested;

    public event EventHandler? UninstallRequested;

    public event EventHandler? LaunchRequested;

    public event EventHandler? CancelRequested;

    public event EventHandler? LicenseChanged;

    public event EventHandler? OpenErrorLogRequested;

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
        SetPage(WelcomePage, WelcomeActions, "第 1 步，共 3 步");
        LicenseCheckBox.IsChecked = false;
        NextButton.IsEnabled = false;
    }

    public void ShowOptions()
    {
        SetPage(OptionsPage, OptionsActions, "第 2 步，共 3 步");
        InstallButton.IsEnabled = LicenseAccepted && !string.IsNullOrWhiteSpace(InstallFolder);
    }

    public void ShowModify()
    {
        SetPage(ModifyPage, ModifyActions, "管理已安装的应用");
    }

    public void ShowProgress(string header, string message)
    {
        SetPage(ProgressPage, ProgressActions, "正在处理，请稍候");
        ProgressHeaderText.Text = string.IsNullOrWhiteSpace(header) ? "正在安装" : header;
        InstallProgressBar.BeginAnimation(RangeBase.ValueProperty, null);
        InstallProgressBar.Value = 0;
        ProgressPercentText.Text = "0%";
        ProgressMessageText.Text = string.IsNullOrWhiteSpace(message) ? "正在准备……" : message;
        ProgressActions.IsEnabled = true;
    }

    public void SetProgress(int percentage, string message)
    {
        var targetProgress = Math.Clamp(percentage, 0, 100);
        var animation = new DoubleAnimation
        {
            From = InstallProgressBar.Value,
            To = targetProgress,
            Duration = TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 220 : 0),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        InstallProgressBar.BeginAnimation(RangeBase.ValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
        ProgressPercentText.Text = $"{targetProgress}%";
        if (!string.IsNullOrWhiteSpace(message))
            ProgressMessageText.Text = message;
    }

    public void SetProgressMessage(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            ProgressMessageText.Text = message;
    }

    public void ShowComplete(string header, string description, bool canLaunch)
    {
        SetPage(CompletePage, CompleteActions, "操作已完成");
        CompleteHeaderText.Text = header;
        CompleteDescriptionText.Text = description;
        LaunchButton.Visibility = canLaunch ? Visibility.Visible : Visibility.Collapsed;
        LaunchButton.IsEnabled = canLaunch;
        CompleteCloseButton.Content = canLaunch ? "仅关闭" : "关闭";
        CompleteCloseButton.Style = (Style)FindResource(canLaunch ? "SecondaryButtonStyle" : "PrimaryButtonStyle");
    }

    public void ShowFailure(string message, string? header = null)
    {
        SetPage(FailurePage, FailureActions, "操作未完成");
        FailureHeaderText.Text = string.IsNullOrWhiteSpace(header) ? "安装失败" : header;
        CopyFailureDetailsButton.Content = "复制错误";
        CopyFailureDetailsButton.ToolTip = "复制完整错误详情及日志位置";
        FailureMessageText.Text = string.IsNullOrWhiteSpace(message)
            ? "安装程序遇到问题，请查看日志后重试。"
            : message;
    }

    private void CopyFailureDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var details = string.Join(Environment.NewLine, FailureHeaderText.Text, FailureMessageText.Text);
            System.Windows.Clipboard.SetText(details);
            CopyFailureDetailsButton.Content = "已复制";
            CopyFailureDetailsButton.ToolTip = "错误标题和完整详情已复制到剪贴板";
        }
        catch (Exception)
        {
            CopyFailureDetailsButton.Content = "无法复制";
            CopyFailureDetailsButton.ToolTip = "复制失败，请选择下方文字手动复制";
        }
    }

    private void OpenErrorLogButton_Click(object sender, RoutedEventArgs e)
    {
        OpenErrorLogRequested?.Invoke(this, EventArgs.Empty);
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

    private void SetPage(FrameworkElement page, FrameworkElement actions, string step)
    {
        WelcomePage.Visibility = Visibility.Collapsed;
        OptionsPage.Visibility = Visibility.Collapsed;
        ModifyPage.Visibility = Visibility.Collapsed;
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
        StepText.Text = step;
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

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = OuterFrame.ActualWidth;
        var height = OuterFrame.ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        UpdateWindowFrame();
    }

    private void UpdateWindowFrame()
    {
        var width = OuterFrame.ActualWidth;
        var height = OuterFrame.ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var nativeFrameApplied = ApplyDwmWindowPolicy();
        OuterFrame.BorderThickness = nativeFrameApplied ? new Thickness(0) : new Thickness(1);
        OuterFrame.Clip = nativeFrameApplied
            ? null
            : new RectangleGeometry(
                new Rect(0, 0, width, height),
                WindowCornerRadius,
                WindowCornerRadius);
    }

    private bool ApplyDwmWindowPolicy()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            var preference = DwmCornerRound;
            var cornerResult = DwmSetWindowAttribute(
                hwnd,
                DwmWindowCornerPreference,
                ref preference,
                sizeof(uint));

            // Let DWM draw the one-pixel border together with the rounded
            // corners. This keeps the radius and antialiasing on one layer.
            var borderColor = DwmBorderColor;
            var borderResult = DwmSetWindowAttribute(
                hwnd,
                DwmWindowBorderColor,
                ref borderColor,
                sizeof(uint));

            return cornerResult == 0 && borderResult == 0;
        }
        catch (DllNotFoundException)
        {
            // DWM is available on supported Windows desktop versions; keep a
            // graceful fallback for older or unusual hosts.
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            // The Win11-only attributes are unavailable on older hosts.
            return false;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            return;

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
