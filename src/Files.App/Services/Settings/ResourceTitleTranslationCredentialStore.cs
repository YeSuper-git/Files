// Copyright (c) Files Community
// Licensed under the MIT License.

using Windows.Security.Credentials;

namespace Files.App.Services.Settings;

public static class ResourceTitleTranslationCredentialStore
{
    private const string MachineTranslationResource = "Files.Aliyun.MachineTranslation";
    private const string BailianResource = "Files.Aliyun.Bailian.QwenMT";
    private const string LegacyBailianResource = "Files.Aliyun.QwenMT";
    private const string AccessKeyIdUser = "AccessKeyId";
    private const string AccessKeySecretUser = "AccessKeySecret";
    private const string ApiKeyUser = "APIKey";

    public static string GetMachineAccessKeyId() => Retrieve(MachineTranslationResource, AccessKeyIdUser);

    public static string GetMachineAccessKeySecret() => Retrieve(MachineTranslationResource, AccessKeySecretUser);

    public static string GetBailianApiKey()
    {
        var apiKey = Retrieve(BailianResource, ApiKeyUser);
        return string.IsNullOrWhiteSpace(apiKey) ? Retrieve(LegacyBailianResource, ApiKeyUser) : apiKey;
    }

    public static void SaveMachineTranslationCredentials(string accessKeyId, string accessKeySecret)
    {
        Save(MachineTranslationResource, AccessKeyIdUser, accessKeyId);
        Save(MachineTranslationResource, AccessKeySecretUser, accessKeySecret);
    }

    public static void SaveBailianApiKey(string apiKey)
        => Save(BailianResource, ApiKeyUser, apiKey);

    public static void RemoveMachineTranslationCredentials()
    {
        Remove(MachineTranslationResource, AccessKeyIdUser);
        Remove(MachineTranslationResource, AccessKeySecretUser);
    }

    public static void RemoveBailianApiKey()
    {
        Remove(BailianResource, ApiKeyUser);
        Remove(LegacyBailianResource, ApiKeyUser);
    }

    private static string Retrieve(string resource, string userName)
    {
        try
        {
            var credential = new PasswordVault().Retrieve(resource, userName);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void Save(string resource, string userName, string password)
    {
        var vault = new PasswordVault();
        Remove(resource, userName);
        vault.Add(new PasswordCredential(resource, userName, password.Trim()));
    }

    private static void Remove(string resource, string userName)
    {
        try
        {
            var vault = new PasswordVault();
            vault.Remove(vault.Retrieve(resource, userName));
        }
        catch
        {
            // The credential was already absent.
        }
    }
}
