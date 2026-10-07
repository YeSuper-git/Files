// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Text.RegularExpressions;
using Files.App.Services.ResourceManager;

namespace Files.App.Services.VideoEditor;

public sealed record ArchiveFileEntry(string Path, long Size, string Folder, bool IsVideo)
{
	public string FileName => System.IO.Path.GetFileName(Path);
	public string SizeText => Size.ToSizeString();
	public string Glyph => IsVideo ? "\uE714" : "\uE8A5";
}

public sealed record ArchiveDestination(string Path, string RelativePath, int Score)
{
	public override string ToString() => RelativePath;
}

public static class ArchiveDestinationService
{
	public static IReadOnlyList<ArchiveFileEntry> ReadFiles(string root, bool recursive, IReadOnlyCollection<string> extensions, CancellationToken token)
	{
		var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.ReparsePoint };
		var files = new List<ArchiveFileEntry>();
		foreach (var path in Directory.EnumerateFiles(root, "*", options))
		{
			token.ThrowIfCancellationRequested();
			try
			{
				var file = new FileInfo(path);
				files.Add(new(path, file.Length, System.IO.Path.GetRelativePath(root, file.DirectoryName!),
					extensions.Contains(file.Extension.TrimStart('.'), StringComparer.OrdinalIgnoreCase)));
			}
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}
		return files.OrderBy(file => file.FileName, StringComparer.CurrentCultureIgnoreCase).ToArray();
	}

	public static IReadOnlyList<ArchiveDestination> Find(string videoPath, string libraryRoot, CancellationToken token)
	{
		if (!Directory.Exists(libraryRoot)) return [];
		var parser = new ResourceCodeParser();
		var filename = System.IO.Path.GetFileNameWithoutExtension(videoPath);
		var code = parser.ParseCode(filename);
		var title = Normalize(Regex.Replace(Regex.Replace(filename, @"\[[^\]]*\]", " "),
			@"[-_\s]*(?:(?:EP?|第)\s*)?\d{1,3}(?:话|話|集)?(?:v\d)?$", "", RegexOptions.IgnoreCase));
		var pending = new Queue<(string Path, int Depth)>();
		pending.Enqueue((libraryRoot, 0));
		var matches = new List<ArchiveDestination>();
		var visited = 0;
		while (pending.Count > 0 && visited++ < 20000)
		{
			token.ThrowIfCancellationRequested();
			var (path, depth) = pending.Dequeue();
			try
			{
				if (depth > 0)
				{
					var name = System.IO.Path.GetFileName(path);
					var directoryCode = parser.ParseCode(name);
					var normalized = Normalize(name);
					var score = code is not null && directoryCode is not null && code.NoZero.Equals(directoryCode.NoZero, StringComparison.OrdinalIgnoreCase)
						? 100 : normalized.Length >= 4 && title.Length >= 4 && (title.Contains(normalized, StringComparison.OrdinalIgnoreCase) || normalized.Contains(title, StringComparison.OrdinalIgnoreCase))
						? 60 + Math.Min(25, normalized.Length) : 0;
					if (score > 0) matches.Add(new(path, System.IO.Path.GetRelativePath(libraryRoot, path), score));
				}
				if (depth >= 10) continue;
				foreach (var directory in new DirectoryInfo(path).EnumerateDirectories())
					if (!directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) pending.Enqueue((directory.FullName, depth + 1));
			}
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}
		return matches.OrderByDescending(item => item.Score).ThenBy(item => item.RelativePath, StringComparer.CurrentCultureIgnoreCase).Take(30).ToArray();
	}

	private static string Normalize(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
