using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Files.App.Helpers.Application
{
	internal static class AppToastNotificationHelper
	{
		private static readonly Uri ApplicationLogoUri = new("ms-appx:///Assets/AppTiles/Dev/Square44x44Logo.scale-100.png");

		public static void ShowUnhandledExceptionToast()
		{
			var toastContent = new AppNotificationBuilder()
					.AddText(Strings.ExceptionNotificationHeader.GetLocalizedResource())
					.AddText(Strings.ExceptionNotificationBody.GetLocalizedResource())
					.SetAppLogoOverride(ApplicationLogoUri)
					.AddButton(new AppNotificationButton(Strings.ExceptionNotificationReportButton.GetLocalizedResource())
						.SetInvokeUri(new Uri(Constants.ExternalUrl.BugReportUrl)))
					.BuildNotification();
			AppNotificationManager.Default.Show(toastContent);
		}

		public static void ShowBackgroundRunningToast()
		{
			var toastContent = new AppNotificationBuilder()
				.AddText(Strings.BackgroundRunningNotificationHeader.GetLocalizedResource())
				.AddText(Strings.BackgroundRunningNotificationBody.GetLocalizedResource())
				.SetAppLogoOverride(ApplicationLogoUri)
				.BuildNotification();
			AppNotificationManager.Default.Show(toastContent);
		}

		public static void ShowDriveEjectToast()
		{
			var toastContent = new AppNotificationBuilder()
				.AddText(Strings.EjectNotificationHeader.GetLocalizedResource())
				.AddText(Strings.EjectNotificationBody.GetLocalizedResource())
				.SetAppLogoOverride(ApplicationLogoUri)
				.SetAttributionText("SettingsAboutAppName".GetLocalizedResource())
				.BuildNotification();
			AppNotificationManager.Default.Show(toastContent);
		}
	}
}
