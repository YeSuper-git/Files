// Copyright (c) Files Community
// Licensed under the MIT License.
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Files.App.Services.ResourceManager;

public sealed record BangumiSubject(int Id, string Name, string ChineseName, string? Date, string? Summary, string? ImageUrl)
{
	public string DisplayName => string.IsNullOrWhiteSpace(ChineseName) ? Name : ChineseName;
	public string DisplayLabel => string.IsNullOrWhiteSpace(Date) ? DisplayName : $"{DisplayName} ({Date[..Math.Min(4, Date.Length)]})";
}

public sealed class BangumiScraperService
{
	private static readonly HttpClient Client = CreateClient();

	private static HttpClient CreateClient()
	{
		var client = new HttpClient { BaseAddress = new Uri("https://api.bgm.tv/") };
		client.DefaultRequestHeaders.UserAgent.ParseAdd("FilesMax/1.0 (+https://github.com/YeSuper-git/Files)");
		client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		return client;
	}

	public async Task<IReadOnlyList<BangumiSubject>> SearchAnimeAsync(string keyword, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(keyword))
			return [];

		var payload = $"{{\"keyword\":{JsonSerializer.Serialize(keyword.Trim())},\"sort\":\"match\",\"filter\":{{\"type\":[2]}}}}";
		using var response = await Client.PostAsync(
			"v0/search/subjects?limit=20",
			new StringContent(payload, Encoding.UTF8, "application/json"),
			cancellationToken);
		response.EnsureSuccessStatusCode();
		await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
		using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

		if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
			return [];

		var results = new List<BangumiSubject>();
		foreach (var item in data.EnumerateArray())
		{
			if (!item.TryGetProperty("id", out var idValue) || !idValue.TryGetInt32(out var id))
				continue;

			var name = GetString(item, "name") ?? string.Empty;
			var chineseName = GetString(item, "name_cn") ?? string.Empty;
			var imageUrl = item.TryGetProperty("images", out var images)
				? GetString(images, "large") ?? GetString(images, "common") ?? GetString(images, "medium")
				: null;
			results.Add(new BangumiSubject(id, name, chineseName, GetString(item, "date"), GetString(item, "summary"), imageUrl));
		}

		return results;
	}

	public async Task<byte[]> DownloadImageAsync(string imageUrl, CancellationToken cancellationToken = default)
	{
		if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
			throw new InvalidDataException("Bangumi 返回的海报地址无效。");
		return await Client.GetByteArrayAsync(uri, cancellationToken);
	}

	private static string? GetString(JsonElement parent, string propertyName)
		=> parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;
}
