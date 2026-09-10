// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;

namespace Files.App.Helpers;

/// <summary>
/// Provides paths for binaries and assets that are installed outside the
/// identity package. This keeps the app compatible with both packaged and
/// external-location deployments.
/// </summary>
public static class AppPathHelper
{
	/// <summary>
	/// Gets the directory containing the running Files executable.
	/// </summary>
	public static string InstallDirectory => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

	/// <summary>
	/// Resolves a path relative to the directory containing the running app.
	/// </summary>
	public static string GetInstallPath(params string[] segments)
	{
		return Path.Combine([InstallDirectory, .. segments]);
	}
}
