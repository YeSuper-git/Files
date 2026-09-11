// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;

namespace Files.App.Helpers
{
	/// <summary>
	/// Provides the path boundary used by the AV manager. The AV page is a
	/// scoped file browser: the configured library and its descendants are
	/// available, while navigating above or outside the library is rejected.
	/// </summary>
	public static class AvManagerPathScope
	{
		public static bool IsWithinLibrary(string? path, string? libraryPath)
		{
			if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(libraryPath))
				return false;

			try
			{
				var candidate = Normalize(path);
				var library = Normalize(libraryPath);

				return candidate.Equals(library, StringComparison.OrdinalIgnoreCase) ||
					candidate.StartsWith(library + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
					candidate.StartsWith(library + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
			}
			catch (ArgumentException)
			{
				return false;
			}
		}

		public static bool IsLibraryRoot(string? path, string? libraryPath)
		{
			if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(libraryPath))
				return false;

			try
			{
				return Normalize(path).Equals(Normalize(libraryPath), StringComparison.OrdinalIgnoreCase);
			}
			catch (ArgumentException)
			{
				return false;
			}
		}

		private static string Normalize(string path)
			=> Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
	}
}
