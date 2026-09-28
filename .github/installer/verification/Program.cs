using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FilesMax.Installer.Bootstrapper;

namespace FilesMax.Installer.Verification;

internal static class Program
{
	[STAThread]
	private static int Main(string[] args)
	{
		var output = Path.GetFullPath(args.Length == 1 ? args[0] : "installer-ui-review");
		Directory.CreateDirectory(output);
		var app = new Application();
		InstallerWindow? window = null;
		try
		{
			window = new InstallerWindow();
			window.Show();
			window.UpdateLayout();
			var outerFrame = Control<Border>(window, "OuterFrame");
			var nativeFrameApplied = outerFrame.BorderThickness.Equals(new Thickness(0));
			Require(nativeFrameApplied || outerFrame.Clip is RectangleGeometry, "The window must initialize either the native DWM frame or the rounded fallback.");
			Console.WriteLine(nativeFrameApplied
				? "Window frame policy: native DWM border and rounded-corner attributes accepted."
				: "Window frame policy: clipped rounded-corner fallback active.");
			window.SetVersion("1.0.15");
			Require(window.Title == "Files max 1.0.15 安装程序", "The native window title must include the current installer version.");
			Require(Control<TextBlock>(window, "InstallerTitleText").Text == "Files max 1.0.15 安装程序", "The visible title bar must include the current installer version.");
			Require(Control<TextBlock>(window, "ProgramDescriptionText").Text == "适用于 Windows 的现代化文件管理器", "The brand subtitle must describe Files max instead of calling it an installer.");
			var welcomeDescription = Control<TextBlock>(window, "WelcomeDescriptionText");
			Require(welcomeDescription.Text == "即将在这台电脑上安装Files max。\n请先确认许可条款。", "The welcome description must use the requested two-line copy.");
			Require(welcomeDescription.Margin.Top >= 20, "The welcome description must sit lower below the heading.");
			Require(Control<CheckBox>(window, "LicenseCheckBox").Margin.Top >= 20, "The license checkbox must follow the welcome copy with clear spacing.");
			Require(window.FindName("WelcomeVersionText") is null, "The separate welcome-page version label must be removed.");
			Require(window.FindName("StepText") is null, "The page step indicator must be removed.");
			window.SetInstallFolder(@"C:\Program Files\Files max");
			window.ShowWelcome();
			var next = Control<Button>(window, "NextButton");
			Require(!next.IsEnabled, "Next must be disabled before consent.");
			Capture(window, output, "01-welcome");
			Control<CheckBox>(window, "LicenseCheckBox").IsChecked = true;
			Require(next.IsEnabled, "Consent must enable Next.");
			var nextRaised = false;
			window.NextRequested += (_, _) => nextRaised = true;
			next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
			Require(nextRaised, "Next must invoke the existing installer flow.");
			Capture(window, output, "02-consent");
			window.ShowOptions();
			Require(Control<Button>(window, "InstallButton").IsEnabled, "A valid path and consent must enable Install.");
			Capture(window, output, "03-options");
			window.SetInstallFolder("");
			Require(!Control<Button>(window, "InstallButton").IsEnabled, "An empty path must disable Install.");
			window.SetInstallFolder(@"D:\软件与本地资源\Files max 的自定义安装目录\Files max");
			Capture(window, output, "04-long-path");
			window.ShowModify();
			Capture(window, output, "05-maintenance");
			window.ShowProgress("正在安装", "正在复制应用文件……");
			window.SetProgress(64, "正在复制应用文件……");
			WaitUntil(
				() => Control<TextBlock>(window, "ProgressPercentText").Text == "64%",
				TimeSpan.FromSeconds(3),
				"Progress text must reflect the engine percentage.");
			Capture(window, output, "06-progress");
			window.ShowComplete("安装完成", "Files max 已安装完成，可以开始使用了。", true);
			Capture(window, output, "07-installed");
			window.ShowComplete("卸载完成", "Files max 已从这台电脑移除。", false);
			Require(Control<Button>(window, "LaunchButton").Visibility == Visibility.Collapsed, "Uninstall must not offer Launch.");
			Require(Equals(Control<Button>(window, "CompleteCloseButton").Content, "关闭"), "A single final action must say Close.");
			Capture(window, output, "08-uninstalled");
			window.ShowComplete("修复完成", "Files max 已修复完成，可以重新启动应用。", true);
			Require(Equals(Control<Button>(window, "CompleteCloseButton").Content, "仅关闭"), "Repair must restore the secondary close label.");
			Capture(window, output, "09-repaired");
			window.ShowFailure("失败阶段：注册应用身份\n错误代码：0x80070005\n原因：访问被拒绝。\n\n请检查目标文件夹的访问权限后重试。\n\n" + new string('详', 1800), "安装未完成", "Files max 安装器诊断包\n诊断测试关键片段");
			var details = Control<TextBox>(window, "FailureMessageText");
			Require(details.IsReadOnly && details.VerticalScrollBarVisibility == ScrollBarVisibility.Auto, "Failure details must be selectable and scrollable.");
			Require(details.Text.StartsWith("失败阶段：", StringComparison.Ordinal), "Failure details must lead with the actionable stage, without repeating the headline.");
			Capture(window, output, "10-failure");
			var copyFailureDetailsButton = Control<Button>(window, "CopyFailureDetailsButton");
			Require(Equals(copyFailureDetailsButton.Content, "复制诊断信息"), "Failure page must clearly label the one-click diagnostics action.");
			copyFailureDetailsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
			Require(Equals(copyFailureDetailsButton.Content, "已复制诊断信息") || Equals(copyFailureDetailsButton.Content, "无法复制"), "Copy feedback must be visible even when clipboard access is unavailable.");
			if (Equals(copyFailureDetailsButton.Content, "已复制诊断信息"))
			{
				var clipboardText = System.Windows.Clipboard.GetText();
				Require(clipboardText.StartsWith("Files max 安装器诊断包", StringComparison.Ordinal), "Copied diagnostics must start with a shareable diagnostics package.");
				Require(clipboardText.Contains("诊断测试关键片段", StringComparison.Ordinal), "Copied diagnostics must include the collected log context.");
			}
			Console.WriteLine("Passed: frame policy, consent, navigation, path gating, progress, completion actions, failure clipboard content, fixed layout and visible control bounds. Captured 10 WPF pages.");
			return 0;
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine(exception);
			return 1;
		}
		finally
		{
			window?.AllowCloseAndClose();
			app.Shutdown();
		}
	}

	private static T Control<T>(InstallerWindow window, string name) where T : FrameworkElement =>
		window.FindName(name) as T ?? throw new InvalidOperationException($"Missing control: {name}");

	private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string failureMessage)
	{
		if (condition())
			return;

		var frame = new DispatcherFrame();
		var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
		var deadline = DateTime.UtcNow + timeout;
		timer.Tick += (_, _) =>
		{
			if (condition() || DateTime.UtcNow >= deadline)
			{
				timer.Stop();
				frame.Continue = false;
			}
		};
		timer.Start();
		Dispatcher.PushFrame(frame);
		Require(condition(), failureMessage);
	}

	private static void Require(bool condition, string message)
	{
		if (!condition)
			throw new InvalidOperationException(message);
	}

	private static void Capture(InstallerWindow window, string output, string name)
	{
		window.UpdateLayout();
		Require(window.Width == 720 && window.Height == 560 && window.ResizeMode == ResizeMode.NoResize,
			$"{name}: all pages must retain the same fixed window size.");
		var root = (FrameworkElement)window.Content;
		VerifyBounds(root, root, name);
		// These are client-area WPF renders, not screenshots of the DWM frame.
		var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
		bitmap.Render(root);
		var encoder = new PngBitmapEncoder();
		encoder.Frames.Add(BitmapFrame.Create(bitmap));
		using var file = File.Create(Path.Combine(output, name + ".png"));
		encoder.Save(file);
	}

	private static void VerifyBounds(FrameworkElement element, FrameworkElement root, string page)
	{
		if (!element.IsVisible)
			return;
		if (element is Button button)
		{
			var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
			Require(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1,
				$"{page}: button {button.Name} is outside the window.");
			if (button.Content is string label)
			{
				var text = new FormattedText(label, button.Language.GetSpecificCulture(), button.FlowDirection,
					new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch), button.FontSize, Brushes.Black,
					VisualTreeHelper.GetDpi(button).PixelsPerDip);
				Require(button.ActualWidth + 1 >= text.Width + button.Padding.Left + button.Padding.Right + 2,
					$"{page}: button {button.Name} clips its label horizontally.");
				Require(button.ActualHeight + 1 >= text.Height + button.Padding.Top + button.Padding.Bottom + 2,
					$"{page}: button {button.Name} clips its label vertically.");
			}
		}
		for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
		{
			if (VisualTreeHelper.GetChild(element, i) is FrameworkElement child)
				VerifyBounds(child, root, page);
		}
	}
}
