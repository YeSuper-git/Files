// Copyright (c) Files Community
// Licensed under the MIT License.
using Files.App.Data.Models.ResourceManager;
using Microsoft.Extensions.Logging;
using System.IO;

namespace Files.App.Services.ResourceManager;

public sealed class AnimeLibraryService
{
	public IResourceWorkspaceService Workspace { get; }
	public IResourceBrowserService Browser { get; }
	public AnimeLibraryService(ILogger<ResourceWorkspaceService> workspaceLogger, ILogger<ResourceBrowserService> browserLogger)
	{
		Workspace = new ResourceWorkspaceService(workspaceLogger, true);
		Browser = new ResourceBrowserService(Workspace, browserLogger, true);
	}

    public static IReadOnlyList<string> GetEpisodeTargets(string folder, ResourceSettings settings)
    {
        var files = Directory.EnumerateFiles(folder).ToArray();
        var videos = files.Where(file => settings.VideoExtensions.Contains(Path.GetExtension(file).TrimStart('.'), StringComparer.OrdinalIgnoreCase)).ToArray();
        var posters = files.Where(file => settings.ImageExtensions.Contains(Path.GetExtension(file).TrimStart('.'), StringComparer.OrdinalIgnoreCase)
            && GetIllustrationGroup(file) is null
            && !Path.GetFileNameWithoutExtension(file).Contains("\u63D2\u56FE", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileNameWithoutExtension(file).StartsWith("illustration-", StringComparison.OrdinalIgnoreCase)
            && (videos.Length == 0 || (GetEpisodeNumber(file) is not null && !videos.Any(video => PosterMatchScore(Path.GetFileNameWithoutExtension(file), Path.GetFileNameWithoutExtension(video)) < 10))))
            .GroupBy(file => Path.GetFileNameWithoutExtension(file), StringComparer.OrdinalIgnoreCase).Select(group => group.First());
        return videos.Concat(posters).OrderBy(file => Path.GetFileNameWithoutExtension(file), new EpisodeNameComparer()).ToArray();
    }

	public static string? ResolveEpisodePoster(string episode, string season, string series, ResourceSettings settings, string? fallback)
	{
		var target = Path.GetFileNameWithoutExtension(episode);
		var candidates = new List<string>();
		foreach (var folder in new[] { season, series }.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			try { candidates.AddRange(Directory.EnumerateFiles(folder).Where(file => settings.ImageExtensions.Contains(Path.GetExtension(file).TrimStart('.'), StringComparer.OrdinalIgnoreCase))); }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}
		if (candidates.Count == 1) return candidates[0];
		var matched = candidates.Select(path => (Path: path, Score: PosterMatchScore(Path.GetFileNameWithoutExtension(path), target)))
			.Where(item => item.Score < 10).OrderBy(item => item.Score).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
		if (matched.Path is not null) return matched.Path;
		var common = candidates.FirstOrDefault(path => new[] { "poster", "cover", "folder", "海报", "封面" }.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase));
		return common ?? fallback;
	}

    public static string GetIllustrationNumber(string video, IReadOnlyList<string> orderedVideos)
    {
        var numbers = orderedVideos.Select(GetEpisodeNumber).ToArray();
        if (numbers.All(number => number is not null) && numbers.Distinct().Count() == numbers.Length)
            return GetEpisodeNumber(video)!;
        var index = Enumerable.Range(0, orderedVideos.Count).FirstOrDefault(position => string.Equals(orderedVideos[position], video, StringComparison.OrdinalIgnoreCase), -1);
        return (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string GetVideoSeriesName(string video)
    {
        var name = Path.GetFileNameWithoutExtension(video);
        var title = System.Text.RegularExpressions.Regex.Replace(name, @"(?:[ _-]+(?:E(?:P(?:ISODE)?)?\s*)?\d{1,3}|第\s*\d+\s*[集话]|E(?:P(?:ISODE)?)?\s*\d{1,3})(?:\s*\[[^\]]*\])?$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim(' ', '-', '_');
        return string.IsNullOrWhiteSpace(title) ? name : title;
    }

	public static bool IsSeasonFolder(string name)
        => System.Text.RegularExpressions.Regex.IsMatch(name, @"第\s*[一二三四五六七八九十百零〇0-9]+\s*[季期]|(?<![A-Za-z])S(?:eason)?\s*\d{1,2}(?=$|[- _\[])|(?:^|\s)Season\s+\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static bool IsSeriesCover(string image, string folder)
    {
        var name = Path.GetFileNameWithoutExtension(image);
        var title = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(name, title, StringComparison.OrdinalIgnoreCase)) return true;
        static string WithoutRange(string value) => System.Text.RegularExpressions.Regex.Replace(value, @"\s*1\s*-\s*\d+$", "").TrimEnd();
        return string.Equals(WithoutRange(name), WithoutRange(title), StringComparison.OrdinalIgnoreCase);
    }

	public static string GetSeasonTitle(string path)
	{
		var name = Path.GetFileName(path);
		var season = System.Text.RegularExpressions.Regex.Match(name, @"(?:S(?:eason)?\s*|第)(\d{1,2})(?:季|\b|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		if (!season.Success) return name;
		var title = name[..season.Index].Trim(' ', '-', '_');
		if (title.Length == 0) title = Path.GetFileName(Path.GetDirectoryName(path)) ?? name;
		title = System.Text.RegularExpressions.Regex.Replace(title, @"\s*\d+\s*[-~至]\s*\d+\s*季.*$", "").Trim();
		var number = int.Parse(season.Groups[1].Value);
		string Chinese(int value) => value < 10 ? "零一二三四五六七八九"[value].ToString() : (value / 10 == 1 ? "十" : "零一二三四五六七八九"[value / 10] + "十") + (value % 10 == 0 ? "" : "零一二三四五六七八九"[value % 10].ToString());
		return title + "第" + Chinese(number) + "季";
	}

	public static bool IsEpisodeIllustration(string image, string episode, string? assignedNumber = null)
	{
		if (GetIllustrationGroup(image) is not null) return false;
		var name = Path.GetFileNameWithoutExtension(image);
		var imageNumber = GetIllustrationEpisodeNumber(image);
		var episodeNumber = assignedNumber ?? GetEpisodeNumber(episode);
		if (imageNumber is not null && episodeNumber is not null) return imageNumber == episodeNumber;
		return PosterMatchScore(name, Path.GetFileNameWithoutExtension(episode)) < 10;
	}

	public static string? GetIllustrationGroup(string image)
	{
		var name = Path.GetFileNameWithoutExtension(image);
		var match = System.Text.RegularExpressions.Regex.Match(name, @"^GLOD[_ -]?(\d+)(?=[_ -]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		return match.Success ? "GLOD" + match.Groups[1].Value : null;
	}

	public static string? GetIllustrationEpisodeNumber(string image)
	{
		if (GetIllustrationGroup(image) is not null) return null;
		var name = Path.GetFileNameWithoutExtension(image);
		var leading = System.Text.RegularExpressions.Regex.Match(name, @"^0*(\d{1,3})(?:\s*插图(?:_[0-9]{5})?(?:\s*[（(]\d+[）)])?$|\s*[（(]\d+[）)]$|$)");
		return leading.Success ? int.Parse(leading.Groups[1].Value).ToString() : GetEpisodeNumber(image);
	}

	public static string? GetEpisodeNumber(string name)
	{
		var stem = Path.GetFileNameWithoutExtension(name);
		var match = System.Text.RegularExpressions.Regex.Match(stem, @"(?:第|\bEP(?:ISODE)?\s*|S\d+[- _]*E?)0*(\d{1,3})(?:集|话|\b)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		if (!match.Success) match = System.Text.RegularExpressions.Regex.Match(stem, @"(?:^|[- _\[])0*(\d{1,3})(?=$|[\] _-])");
		return match.Success && int.TryParse(match.Groups[1].Value, out var number) ? number.ToString() : null;
	}

	public static int PosterMatchScore(string image, string episode)
	{
		if (GetIllustrationGroup(image) is not null) return 10;
		static string Normalize(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
		var a = Normalize(image); var b = Normalize(episode);
		if (a.Length == 0 || b.Length == 0) return 10;
		if (a == b) return 0;
		static string Number(string name)
		{
			var explicitEpisode = System.Text.RegularExpressions.Regex.Match(name, @"(?:\bEP?|第)\s*0*(\d+)(?:集|话|\b)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			if (explicitEpisode.Success) return explicitEpisode.Groups[1].Value.TrimStart('0');
			var numbers = System.Text.RegularExpressions.Regex.Matches(name, @"\d+");
			return numbers.Count == 0 ? string.Empty : numbers[^1].Value.TrimStart('0');
		}
		var an = Number(image); var bn = Number(episode);
		if (an.Length > 0 && bn.Length > 0 && an != bn) return 10;
		if (Math.Min(a.Length, b.Length) >= 4 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))) return 1;
		if (an.Length > 0 && an == bn) return 2;
		return 10;
	}

	public static async Task<IReadOnlyList<string>> GetSeasonsAsync(string path, ResourceSettings settings, CancellationToken token)
	{
		return await Task.Run(() =>
		{
			var seasons = new List<string>();
			void Visit(DirectoryInfo folder, int depth)
			{
				token.ThrowIfCancellationRequested();
				if (depth > 8 || folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
				try
				{
					var children = folder.EnumerateDirectories().Where(child => !child.Name.StartsWith('.') && child.Name != "@eaDir" && !child.Attributes.HasFlag(FileAttributes.ReparsePoint)).ToArray();
					if (folder.EnumerateFiles().Any(file => settings.VideoExtensions.Contains(file.Extension.TrimStart('.'), StringComparer.OrdinalIgnoreCase)) || children.Length == 0)
						seasons.Add(folder.FullName);
					foreach (var child in children.OrderBy(child => child.Name, new EpisodeNameComparer())) Visit(child, depth + 1);
				}
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}
			Visit(new DirectoryInfo(path), 0);
			return (IReadOnlyList<string>)seasons;
		}, token);
	}
}

public sealed class EpisodeNameComparer : IComparer<string>
{
	public int Compare(string? left, string? right)
	{
		left ??= string.Empty; right ??= string.Empty;
		int a = 0, b = 0;
		while (a < left.Length && b < right.Length)
		{
			if (char.IsAsciiDigit(left[a]) && char.IsAsciiDigit(right[b]))
			{
				int aStart = a, bStart = b;
				while (a < left.Length && char.IsAsciiDigit(left[a])) a++;
				while (b < right.Length && char.IsAsciiDigit(right[b])) b++;
				var an = left[aStart..a].TrimStart('0'); var bn = right[bStart..b].TrimStart('0');
				int result = an.Length.CompareTo(bn.Length);
				if (result == 0) result = string.Compare(an, bn, StringComparison.Ordinal);
				if (result != 0) return result;
			}
			else
			{
				int result = char.ToUpperInvariant(left[a++]).CompareTo(char.ToUpperInvariant(right[b++]));
				if (result != 0) return result;
			}
		}
		int remaining = (left.Length - a).CompareTo(right.Length - b);
		return remaining != 0 ? remaining : StringComparer.OrdinalIgnoreCase.Compare(left, right);
	}
}
