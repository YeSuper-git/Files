// Copyright (c) Files Community
// Licensed under the MIT License.

using AlibabaCloud.OpenApiClient.Models;
using AlibabaCloud.SDK.Alimt20181012.Models;
using System.IO;

namespace Files.App.Services.ResourceManager;

public sealed class AliyunMachineTranslationTitleTranslationService : IResourceTitleTranslationService
{
    public async Task<string> TranslateJapaneseTitleAsync(
        string title,
        string accessKeyId,
        string accessKeySecret,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("标题不能为空。", nameof(title));
        if (string.IsNullOrWhiteSpace(accessKeyId))
            throw new ArgumentException("请先配置阿里云 AccessKey ID。", nameof(accessKeyId));
        if (string.IsNullOrWhiteSpace(accessKeySecret))
            throw new ArgumentException("请先配置阿里云 AccessKey Secret。", nameof(accessKeySecret));

        cancellationToken.ThrowIfCancellationRequested();
        var config = new Config
        {
            AccessKeyId = accessKeyId.Trim(),
            AccessKeySecret = accessKeySecret.Trim(),
            Endpoint = "mt.cn-hangzhou.aliyuncs.com",
            RegionId = "cn-hangzhou",
        };
        var client = new AlibabaCloud.SDK.Alimt20181012.Client(config);
        var request = new TranslateGeneralRequest
        {
            FormatType = "text",
            Scene = "general",
            SourceLanguage = "ja",
            SourceText = title.Trim(),
            TargetLanguage = "zh",
        };

        var response = await Task.Run(() => client.TranslateGeneral(request), cancellationToken).ConfigureAwait(false);
        var body = response?.Body;
        if (body?.Code != 200)
            throw new InvalidDataException(string.IsNullOrWhiteSpace(body?.Message)
                ? "阿里云机器翻译请求失败。请检查 AccessKey 权限、服务开通状态和额度。"
                : $"阿里云机器翻译请求失败：{body.Message}");

        var translatedTitle = body.Data?.Translated;
        if (string.IsNullOrWhiteSpace(translatedTitle))
            throw new InvalidDataException("阿里云机器翻译返回的结果为空。");

        return translatedTitle.Trim();
    }
}
