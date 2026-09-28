// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Models.ResourceManager;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Storage;

namespace Files.App.Services.ResourceManager;

internal static partial class ChangLiActorImportService
{
    private const string ActorPackageFormat = "changli-files-actors";
    private const string LibraryPackageFormat = "changli-workbench-library";
    private static readonly char[] AliasSeparators = [',', '，', ';', '；', '|', '/', '、', '\n', '\r'];

    public static async Task<ChangLiActorImportPlan> CreatePlanAsync(
        string packagePath,
        IReadOnlyList<ChangLiActorImportTarget> targets,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var format = ReadString(root, "format");
        if (!string.Equals(format, ActorPackageFormat, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(format, LibraryPackageFormat, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("文件不是受支持的 ChangLi 演员包或影视库导出文件。");

        var actorRows = ReadTableRows(root, "actors");
        if (actorRows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("ChangLi 导出文件中没有演员数据。");

        var photoRows = ReadTableRows(root, "actor_photos");
        var photosByActor = new Dictionary<string, List<ChangLiActorPhoto>>(StringComparer.OrdinalIgnoreCase);
        if (photoRows.ValueKind == JsonValueKind.Array)
        {
            var rowIndex = 0;
            foreach (var row in photoRows.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var actorId = ReadScalarText(row, "actor_id");
                if (string.IsNullOrWhiteSpace(actorId))
                {
                    rowIndex++;
                    continue;
                }

                var photo = new ChangLiActorPhoto(
                    ReadString(row, "photo_base64"),
                    ReadString(row, "photo"),
                    ReadBoolean(row, "is_primary"),
                    ReadInteger(row, "sort_order", rowIndex));
                if (!string.IsNullOrWhiteSpace(photo.ImageData) || !string.IsNullOrWhiteSpace(photo.Path))
                {
                    if (!photosByActor.TryGetValue(actorId, out var actorPhotos))
                        photosByActor[actorId] = actorPhotos = [];
                    actorPhotos.Add(photo);
                }

                rowIndex++;
            }
        }

        var actors = new List<ChangLiActor>();
        foreach (var row in actorRows.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = ReadString(row, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var actorId = ReadScalarText(row, "id") ?? string.Empty;
            List<ChangLiActorPhoto> photos = photosByActor.TryGetValue(actorId, out var actorPhotos)
                ? actorPhotos.OrderByDescending(photo => photo.IsPrimary).ThenBy(photo => photo.SortOrder).ToList()
                : [];
            var mainPhoto = new ChangLiActorPhoto(
                ReadString(row, "avatar_base64"),
                ReadString(row, "photo"),
                IsPrimary: true,
                SortOrder: -1);
            if (!string.IsNullOrWhiteSpace(mainPhoto.ImageData) || !string.IsNullOrWhiteSpace(mainPhoto.Path))
                photos.Insert(0, mainPhoto);

            actors.Add(new ChangLiActor
            {
                Id = actorId,
                Name = name,
                Alias = ReadString(row, "alias"),
                JapaneseName = ReadString(row, "japanese_name"),
                Biography = ReadString(row, "bio"),
                Birthday = ReadString(row, "birthday"),
                Height = ReadString(row, "height"),
                Weight = ReadString(row, "weight"),
                Measurements = ReadString(row, "measurements"),
                CupSize = ReadString(row, "cup_size"),
                Photos = photos,
            });
        }

        return MatchActors(actors, actorRows.GetArrayLength(), targets);
    }

    public static async Task<ChangLiActorImportResult> ApplyAsync(
        ChangLiActorImportPlan plan,
        IResourceWorkspaceService workspace,
        bool overwriteExisting,
        CancellationToken cancellationToken = default)
    {
        var importedActors = 0;
        var importedPhotos = 0;
        var skippedPhotos = 0;
        var photoRoot = Path.Combine(
            ApplicationData.Current.LocalFolder.Path,
            Constants.LocalSettings.SettingsFolderName,
            "ResourceManager",
            "ActorPosters");

        foreach (var match in plan.Matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var savedPhotos = new List<string>();
            string? primaryPhotoPath = null;
            var actorPhotoDirectory = Path.Combine(photoRoot, GetStableFolderKey(match.Target.Path));

            foreach (var photo in match.Actor.Photos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var asset = await ReadPhotoAsync(photo, cancellationToken);
                    if (asset is null)
                    {
                        skippedPhotos++;
                        continue;
                    }

                    Directory.CreateDirectory(actorPhotoDirectory);
                    var hash = Convert.ToHexString(SHA256.HashData(asset.Bytes)).ToLowerInvariant();
                    var destination = Path.Combine(actorPhotoDirectory, $"{hash[..24]}{asset.Extension}");
                    if (!File.Exists(destination))
                        await File.WriteAllBytesAsync(destination, asset.Bytes, cancellationToken);

                    if (!savedPhotos.Contains(destination, StringComparer.OrdinalIgnoreCase))
                        savedPhotos.Add(destination);
                    if (photo.IsPrimary && primaryPhotoPath is null)
                        primaryPhotoPath = destination;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    skippedPhotos++;
                }
            }

            importedPhotos += savedPhotos.Count;
            primaryPhotoPath ??= savedPhotos.FirstOrDefault();
            var existing = workspace.GetActorDetails(match.Target.Path);
            var importedMeasurements = ParseMeasurements(match.Actor.Measurements);
            var importedAliases = GetImportedAliases(match.Actor);
            var details = existing.Clone();
            var existingNameIsFolderName = string.Equals(
                NormalizeName(existing.Name),
                NormalizeName(match.Target.FolderName),
                StringComparison.OrdinalIgnoreCase);
            if (overwriteExisting || string.IsNullOrWhiteSpace(existing.Name) || existingNameIsFolderName)
                details.Name = match.Actor.Name;

            details.Aliases = MergeAliases(existing.Aliases, importedAliases, overwriteExisting);
            details.Biography = MergeText(existing.Biography, match.Actor.Biography, overwriteExisting);
            details.HeightCm = MergeText(existing.HeightCm, NormalizeNumericValue(match.Actor.Height, allowMeters: true), overwriteExisting);
            details.WeightKg = MergeText(existing.WeightKg, NormalizeNumericValue(match.Actor.Weight, allowMeters: false), overwriteExisting);
            details.Bust = MergeText(existing.Bust, importedMeasurements.Bust, overwriteExisting);
            details.Waist = MergeText(existing.Waist, importedMeasurements.Waist, overwriteExisting);
            details.Hip = MergeText(existing.Hip, importedMeasurements.Hip, overwriteExisting);
            details.CupSize = MergeText(existing.CupSize, NormalizeCupSize(match.Actor.CupSize), overwriteExisting);

            if (TryParseDate(match.Actor.Birthday, out var birthday) && (overwriteExisting || existing.BirthDate is null))
                details.BirthDate = birthday;

            details.PosterPaths = existing.PosterPaths
                .Concat(savedPhotos)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            workspace.SetActorDetails(match.Target.Path, details);

            if (primaryPhotoPath is not null &&
                (overwriteExisting || string.IsNullOrWhiteSpace(workspace.GetPosterOverride(match.Target.Path))))
                workspace.SetPosterOverride(match.Target.Path, primaryPhotoPath);

            importedActors++;
        }

        return new ChangLiActorImportResult(importedActors, importedPhotos, skippedPhotos);
    }

    private static ChangLiActorImportPlan MatchActors(
        IReadOnlyList<ChangLiActor> actors,
        int sourceActorCount,
        IReadOnlyList<ChangLiActorImportTarget> targets)
    {
        var targetLookup = new Dictionary<string, List<ChangLiActorImportTarget>>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            AddTargetName(targetLookup, target, target.FolderName);
            AddTargetName(targetLookup, target, target.CurrentName);
            foreach (var alias in SplitAliases(target.CurrentAliases))
                AddTargetName(targetLookup, target, alias);
        }

        var candidates = new List<(ChangLiActor Actor, string? TargetPath)>();
        var ambiguousCount = 0;
        foreach (var actor in actors)
        {
            var matchedTargets = new Dictionary<string, ChangLiActorImportTarget>(StringComparer.OrdinalIgnoreCase);
            var names = new[] { actor.Name }
                .Concat(SplitAliases(actor.JapaneseName))
                .Concat(SplitAliases(actor.Alias));
            foreach (var name in names)
            {
                var key = NormalizeName(name);
                if (key.Length == 0 || !targetLookup.TryGetValue(key, out var targetMatches))
                    continue;
                foreach (var target in targetMatches)
                    matchedTargets[target.Path] = target;
            }

            if (matchedTargets.Count == 1)
                candidates.Add((actor, matchedTargets.Keys.Single()));
            else
            {
                candidates.Add((actor, null));
                if (matchedTargets.Count > 1)
                    ambiguousCount++;
            }
        }

        var duplicateTargetPaths = candidates
            .Where(candidate => candidate.TargetPath is not null)
            .GroupBy(candidate => candidate.TargetPath!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ambiguousCount += candidates.Count(candidate => candidate.TargetPath is not null && duplicateTargetPaths.Contains(candidate.TargetPath));

        var matches = candidates
            .Where(candidate => candidate.TargetPath is not null && !duplicateTargetPaths.Contains(candidate.TargetPath))
            .Select(candidate => new ChangLiActorImportMatch(
                candidate.Actor,
                targets.First(target => string.Equals(target.Path, candidate.TargetPath, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        var unmatchedCount = Math.Max(0, sourceActorCount - matches.Count - ambiguousCount);
        return new ChangLiActorImportPlan(sourceActorCount, matches, unmatchedCount, ambiguousCount);
    }

    private static async Task<ChangLiActorAsset?> ReadPhotoAsync(ChangLiActorPhoto photo, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(photo.ImageData))
        {
            var data = photo.ImageData.Trim();
            var extension = string.Empty;
            var encoded = data;
            if (data.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                var commaIndex = data.IndexOf(',');
                if (commaIndex >= 0 && data[..commaIndex].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                {
                    var mediaType = data[5..data.IndexOf(';')].ToLowerInvariant();
                    extension = mediaType switch
                    {
                        "image/jpeg" or "image/jpg" => ".jpg",
                        "image/png" => ".png",
                        "image/webp" => ".webp",
                        "image/gif" => ".gif",
                        "image/bmp" => ".bmp",
                        "image/tiff" => ".tiff",
                        _ => string.Empty,
                    };
                    encoded = extension.Length == 0 ? string.Empty : data[(commaIndex + 1)..];
                }
                else
                    encoded = string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(encoded))
            {
                try
                {
                    var bytes = Convert.FromBase64String(encoded);
                    if (bytes.Length > 0)
                    {
                        extension = string.IsNullOrEmpty(extension) ? DetectImageExtension(bytes) ?? string.Empty : extension;
                        if (extension.Length > 0)
                            return new ChangLiActorAsset(bytes, extension);
                    }
                }
                catch (FormatException)
                {
                    // Older exports may have a usable local photo path beside invalid cached data.
                }
            }
        }

        if (string.IsNullOrWhiteSpace(photo.Path))
            return null;

        var changLiDataDirectory = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "changli"));
        var sourcePath = Path.IsPathRooted(photo.Path)
            ? Path.GetFullPath(photo.Path)
            : Path.GetFullPath(Path.Combine(changLiDataDirectory, photo.Path));
        if (!IsWithinDirectory(sourcePath, changLiDataDirectory) || !File.Exists(sourcePath))
            return null;

        var fileExtension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!IsSupportedImageExtension(fileExtension))
            return null;

        return new ChangLiActorAsset(await File.ReadAllBytesAsync(sourcePath, cancellationToken), fileExtension);
    }

    private static string? DetectImageExtension(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return ".jpg";
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return ".png";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            return ".webp";
        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
            return ".gif";
        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D)
            return ".bmp";
        return null;
    }

    private static bool IsSupportedImageExtension(string extension)
        => extension is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp" or ".tif" or ".tiff";

    private static bool IsWithinDirectory(string filePath, string directoryPath)
    {
        var relativePath = Path.GetRelativePath(directoryPath, filePath);
        return !Path.IsPathRooted(relativePath) &&
            !string.Equals(relativePath, "..", StringComparison.Ordinal) &&
            !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    internal static bool IsImportActorFolder(DirectoryInfo directory)
    {
        if (directory.Name.StartsWith('.') || directory.Name.Equals("@eaDir", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            return !directory.Attributes.HasFlag(System.IO.FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    private static string GetStableFolderKey(string path)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))
            .ToLowerInvariant()[..16];

    private static void AddTargetName(
        IDictionary<string, List<ChangLiActorImportTarget>> lookup,
        ChangLiActorImportTarget target,
        string? name)
    {
        var key = NormalizeName(name);
        if (key.Length == 0)
            return;
        if (!lookup.TryGetValue(key, out var targets))
            lookup[key] = targets = [];
        if (!targets.Any(existing => string.Equals(existing.Path, target.Path, StringComparison.OrdinalIgnoreCase)))
            targets.Add(target);
    }

    private static IEnumerable<string> SplitAliases(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(AliasSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> GetImportedAliases(ChangLiActor actor)
        => SplitAliases(actor.Alias)
            .Concat(SplitAliases(actor.JapaneseName))
            .DistinctBy(NormalizeName, StringComparer.OrdinalIgnoreCase);

    private static string MergeAliases(string? existingAliases, IEnumerable<string> importedAliases, bool overwriteExisting)
    {
        var existing = overwriteExisting ? Enumerable.Empty<string>() : SplitAliases(existingAliases);
        var merged = existing
            .Concat(importedAliases)
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .DistinctBy(NormalizeName, StringComparer.OrdinalIgnoreCase);
        return string.Join("、", merged);
    }

    private static string MergeText(string? existingValue, string? importedValue, bool overwriteExisting)
        => !string.IsNullOrWhiteSpace(importedValue) && (overwriteExisting || string.IsNullOrWhiteSpace(existingValue))
            ? importedValue.Trim()
            : existingValue?.Trim() ?? string.Empty;

    private static (string? Bust, string? Waist, string? Hip) ParseMeasurements(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (null, null, null);

        var numbers = MeasurementNumberRegex().Matches(value)
            .Cast<Match>()
            .Select(match => match.Value)
            .Take(3)
            .ToArray();
        return numbers.Length == 3
            ? (numbers[0], numbers[1], numbers[2])
            : (null, null, null);
    }

    private static string? NormalizeNumericValue(string? value, bool allowMeters)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var match = NumericWithUnitRegex().Match(value.Trim());
        if (!match.Success)
            return null;

        var number = match.Groups[1].Value;
        var unit = match.Groups[2].Value;
        if (allowMeters && (unit.Equals("m", StringComparison.OrdinalIgnoreCase) || unit == "米") &&
            decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var meters))
            return (meters * 100m).ToString("0.#", CultureInfo.InvariantCulture);

        return number;
    }

    private static string? NormalizeCupSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var cup = value.Trim().FirstOrDefault(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        return cup == default ? null : char.ToUpperInvariant(cup).ToString();
    }

    private static bool TryParseDate(string? value, out DateTime date)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date);

    private static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var rune in value.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
                builder.Append(rune.ToString().ToUpperInvariant());
        }
        return builder.ToString();
    }

    private static JsonElement ReadTableRows(JsonElement root, string tableName)
    {
        if (TryGetProperty(root, "tables", out var tables) && TryGetProperty(tables, tableName, out var nestedRows))
            return nestedRows;
        return TryGetProperty(root, tableName, out var rows) ? rows : default;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static string? ReadScalarText(JsonElement element, string propertyName)
        => ReadString(element, propertyName);

    private static bool ReadBoolean(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
            return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            JsonValueKind.String => string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase) || value.GetString() == "1",
            _ => false,
        };
    }

    private static int ReadInteger(JsonElement element, string propertyName, int fallback)
    {
        if (!TryGetProperty(element, propertyName, out var value))
            return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex MeasurementNumberRegex();

    [GeneratedRegex(@"^\s*(\d+(?:\.\d+)?)\s*(cm|厘米|kg|公斤|m|米)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex NumericWithUnitRegex();
}

internal sealed record ChangLiActorImportTarget(string Path, string FolderName, string CurrentName, string CurrentAliases);
internal sealed record ChangLiActorImportMatch(ChangLiActor Actor, ChangLiActorImportTarget Target);
internal sealed record ChangLiActorImportPlan(int SourceActorCount, IReadOnlyList<ChangLiActorImportMatch> Matches, int UnmatchedCount, int AmbiguousCount);
internal sealed record ChangLiActorImportResult(int ImportedActors, int ImportedPhotos, int SkippedPhotos);
internal sealed record ChangLiActorPhoto(string? ImageData, string? Path, bool IsPrimary, int SortOrder);
internal sealed record ChangLiActorAsset(byte[] Bytes, string Extension);

internal sealed class ChangLiActor
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Alias { get; init; }
    public string? JapaneseName { get; init; }
    public string? Biography { get; init; }
    public string? Birthday { get; init; }
    public string? Height { get; init; }
    public string? Weight { get; init; }
    public string? Measurements { get; init; }
    public string? CupSize { get; init; }
    public List<ChangLiActorPhoto> Photos { get; init; } = [];
}
