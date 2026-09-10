// Copyright (c) Files Community
// Licensed under the MIT License.

#if FILES_EXTERNAL_LOCATION_BUILD

using System.Net.Http.Headers;
using System.Text.Json;
using Files.App.Helpers;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Windows.Storage;

namespace Files.App.Services
{
	/// <summary>
	/// Updates an external-location installation through the same traditional
	/// installer that users run for a fresh installation.
	/// </summary>
	public sealed class InstallerUpdateService : ObservableObject, IUpdateService, IDisposable
	{
		private const string ReleasesApiUrl = "https://api.github.com/repos/YeSuper-git/Files/releases?per_page=20";

#if FILES_AV_MANAGER
		private const string ProductName = "Files AV Resource Manager";
		private const string InstallerAssetName = "Files-AV-Manager-Setup.exe";
		private const string VersionAssetName = "Files-AV-Manager-Setup.version.json";
#else
		private const string ProductName = "Files";
		private const string InstallerAssetName = "Files-Setup.exe";
		private const string VersionAssetName = "Files-Setup.version.json";
#endif

		private readonly HttpClient _client = new(new SocketsHttpHandler
		{
			PooledConnectionLifetime = TimeSpan.FromMinutes(3)
		});

		private UpdateInfo? _availableUpdate;
		private bool _isUpdateAvailable;
		private bool _isUpdating;
		private int _updateProgress;

		public InstallerUpdateService()
		{
			_client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Files", AppLifecycleHelper.AppVersion.ToString()));
			_client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
		}

		public bool IsUpdateAvailable
		{
			get => _isUpdateAvailable;
			private set => SetProperty(ref _isUpdateAvailable, value);
		}

		public bool IsUpdating
		{
			get => _isUpdating;
			private set => SetProperty(ref _isUpdating, value);
		}

		public int UpdateProgress
		{
			get => _updateProgress;
			private set => SetProperty(ref _updateProgress, value);
		}

		public bool IsAppUpdated => AppLifecycleHelper.IsAppUpdated;

		private bool _areReleaseNotesAvailable;
		public bool AreReleaseNotesAvailable
		{
			get => _areReleaseNotesAvailable;
			private set => SetProperty(ref _areReleaseNotesAvailable, value);
		}

		public async Task CheckForUpdatesAsync()
		{
			IsUpdateAvailable = false;
			_availableUpdate = null;

			try
			{
				using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
				using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
				response.EnsureSuccessStatusCode();

				await using var stream = await response.Content.ReadAsStreamAsync();
				using var releases = await JsonDocument.ParseAsync(stream);
				var currentVersion = AppLifecycleHelper.AppVersion;

				foreach (var release in releases.RootElement.EnumerateArray())
				{
					if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
						continue;
					if (release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
						continue;

					var metadataUri = FindAssetUri(release, VersionAssetName);
					var installerUri = FindAssetUri(release, InstallerAssetName);
					if (metadataUri is null || installerUri is null)
						continue;

					var metadata = await ReadMetadataAsync(metadataUri);
					if (metadata is null || metadata.Version <= currentVersion)
						continue;

					if (_availableUpdate is null || metadata.Version > _availableUpdate.Version)
						_availableUpdate = new UpdateInfo(metadata.Version, installerUri, metadata.Sha256);
				}

				if (_availableUpdate is not null)
				{
					App.Logger.LogInformation("{ProductName}: update {Version} is available.", ProductName, _availableUpdate.Version);
					MainWindow.Instance.DispatcherQueue.TryEnqueue(() => IsUpdateAvailable = true);
				}
			}
			catch (HttpRequestException ex)
			{
				App.Logger.LogDebug(ex, "{ProductName}: update check could not reach GitHub.", ProductName);
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "{ProductName}: update check failed.", ProductName);
			}
		}

		public async Task DownloadUpdatesAsync()
		{
			var update = _availableUpdate;
			if (update is null || IsUpdating)
				return;

			IsUpdating = true;
			var handoffStarted = false;
			var installerPath = Path.Combine(Path.GetTempPath(), $"{InstallerAssetName}-{update.Version}.exe");

			try
			{
				using var response = await _client.GetAsync(update.InstallerUri, HttpCompletionOption.ResponseHeadersRead);
				response.EnsureSuccessStatusCode();

				var contentLength = response.Content.Headers.ContentLength;
				await using var source = await response.Content.ReadAsStreamAsync();
				var buffer = new byte[64 * 1024];
				long totalRead = 0;
				int read;
				await using (var destination = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None))
				{
					while ((read = await source.ReadAsync(buffer)) > 0)
					{
						await destination.WriteAsync(buffer.AsMemory(0, read));
						totalRead += read;
						if (contentLength is > 0)
							UpdateProgress = (int)Math.Clamp(totalRead * 100 / contentLength.Value, 0, 100);
					}
					await destination.FlushAsync();
				}

				if (!string.IsNullOrWhiteSpace(update.Sha256))
				{
					await using var downloadedFile = File.OpenRead(installerPath);
					var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(downloadedFile));
					if (!string.Equals(actualHash, update.Sha256, StringComparison.OrdinalIgnoreCase))
						throw new InvalidDataException("The downloaded installer checksum does not match the release metadata.");
				}

				AppLifecycleHelper.SaveSessionTabs();
				App.AppModel.ForceProcessTermination = true;

				// Let the current process exit before Setup replaces Files.exe. The
				// detached cmd process survives the application's shutdown.
				var command = $"/c timeout /t 2 /nobreak >nul & start \"\" /wait \"{installerPath}\" /S";
				Process.Start(new ProcessStartInfo
				{
					FileName = "cmd.exe",
					Arguments = command,
					UseShellExecute = false,
					CreateNoWindow = true,
					WorkingDirectory = Path.GetTempPath()
				});

				handoffStarted = true;
				App.Current.Exit();
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "{ProductName}: installer update failed.", ProductName);
			}
			finally
			{
				if (!handoffStarted)
					App.AppModel.ForceProcessTermination = false;

				IsUpdating = false;
				IsUpdateAvailable = false;
				UpdateProgress = 0;
				if (!handoffStarted)
					SafetyExtensions.IgnoreExceptions(() => File.Delete(installerPath));
			}
		}

		public Task DownloadMandatoryUpdatesAsync() => Task.CompletedTask;

		public async Task CheckForReleaseNotesAsync()
		{
			try
			{
				using var response = await _client.GetAsync(Constants.ExternalUrl.ReleaseNotesUrl);
				AreReleaseNotesAvailable = response.IsSuccessStatusCode;
			}
			catch
			{
				AreReleaseNotesAvailable = false;
			}
		}

		public async Task CheckAndUpdateFilesLauncherAsync()
		{
			try
			{
				var destFolderPath = Path.Combine(UserDataPaths.GetDefault().LocalAppData, "Files");
				var destExeFilePath = Path.Combine(destFolderPath, "Files.App.Launcher.exe");
				var srcExeFilePath = AppPathHelper.GetInstallPath("Assets", "FilesOpenDialog", "Files.App.Launcher.exe");

				if (!File.Exists(destExeFilePath) || !File.Exists(srcExeFilePath))
					return;

				File.Copy(srcExeFilePath, destExeFilePath, overwrite: true);
				App.Logger.LogInformation("{ProductName}: Files.App.Launcher updated.", ProductName);
			}
			catch (Exception ex)
			{
				App.Logger.LogDebug(ex, "{ProductName}: launcher update skipped.", ProductName);
			}

			await Task.CompletedTask;
		}

		private async Task<UpdateMetadata?> ReadMetadataAsync(Uri metadataUri)
		{
			using var response = await _client.GetAsync(metadataUri);
			response.EnsureSuccessStatusCode();
			await using var stream = await response.Content.ReadAsStreamAsync();
			using var metadata = await JsonDocument.ParseAsync(stream);
			if (!metadata.RootElement.TryGetProperty("version", out var versionProperty))
				return null;

			if (!Version.TryParse(versionProperty.GetString(), out var version))
				return null;

			var sha256 = metadata.RootElement.TryGetProperty("sha256", out var hashProperty)
				? hashProperty.GetString()
				: null;
			return new UpdateMetadata(version, sha256);
		}

		private static Uri? FindAssetUri(JsonElement release, string assetName)
		{
			if (!release.TryGetProperty("assets", out var assets))
				return null;

			foreach (var asset in assets.EnumerateArray())
			{
				if (asset.TryGetProperty("name", out var name) &&
					string.Equals(name.GetString(), assetName, StringComparison.OrdinalIgnoreCase) &&
					asset.TryGetProperty("browser_download_url", out var url) &&
					Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri))
					return uri;
			}

			return null;
		}

		public void Dispose() => _client.Dispose();

		private sealed record UpdateMetadata(Version Version, string? Sha256);
		private sealed record UpdateInfo(Version Version, Uri InstallerUri, string? Sha256);
	}
}

#endif
