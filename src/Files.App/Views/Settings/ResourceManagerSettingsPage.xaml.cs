// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.DependencyInjection;
using Files.App.Data.Contracts;
using Files.App.Data.Models.ResourceManager;
using Files.App.Services.ResourceManager;
using Files.App.Services.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.IO;
using WinRT;

namespace Files.App.Views.Settings;

public sealed partial class ResourceManagerSettingsPage : Page
{
    private readonly IAppSettingsService _appSettings = Ioc.Default.GetRequiredService<IAppSettingsService>();
    private readonly IResourceWorkspaceService _workspace = Ioc.Default.GetRequiredService<IResourceWorkspaceService>();
    private readonly IResourceOperationsService _operations = Ioc.Default.GetRequiredService<IResourceOperationsService>();
    private readonly ObservableCollection<ResourceToolSnapshot> _snapshots = [];
    private bool _isInitializing;
    private bool _isEditingTranslationProvider;

    public ResourceManagerSettingsPage()
    {
        InitializeComponent();
        _isInitializing = true;
        var selectedProvider = _appSettings.ResourceManagerTranslationProvider;
        TranslationProviderComboBox.SelectedItem = TranslationProviderComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), selectedProvider, StringComparison.Ordinal))
            ?? TranslationProviderComboBox.Items.OfType<ComboBoxItem>().First();
        UpdateTranslationConfigVisibility();
        MachineAccessKeyIdTextBox.PlaceholderText = GetCredentialPlaceholder(ResourceTitleTranslationCredentialStore.GetMachineAccessKeyId());
        MachineAccessKeySecretPasswordBox.PlaceholderText = GetCredentialPlaceholder(ResourceTitleTranslationCredentialStore.GetMachineAccessKeySecret());
        BailianApiKeyPasswordBox.PlaceholderText = GetCredentialPlaceholder(ResourceTitleTranslationCredentialStore.GetBailianApiKey());
        SnapshotsListView.ItemsSource = _snapshots;
        RefreshSnapshots();
        _isInitializing = false;
    }

    private void TranslationProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTranslationConfigVisibility();
        if (!_isInitializing && TranslationProviderComboBox.SelectedItem is ComboBoxItem { Tag: string provider })
            _appSettings.ResourceManagerTranslationProvider = provider;
    }

    private void EditTranslationProviderButton_Click(object sender, RoutedEventArgs e)
    {
        _isEditingTranslationProvider = !_isEditingTranslationProvider;
        TranslationProviderEditPanel.Visibility = _isEditingTranslationProvider ? Visibility.Visible : Visibility.Collapsed;
        EditTranslationProviderButton.Content = _isEditingTranslationProvider
            ? Strings.ResourceManagerFinishEditing.GetLocalizedResource()
            : Strings.Edit.GetLocalizedResource();
        UpdateTranslationConfigVisibility();
    }

    private void UpdateTranslationConfigVisibility()
    {
        var provider = (TranslationProviderComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        MachineTranslationConfigPanel.Visibility = string.Equals(provider, "AliyunMachineTranslation", StringComparison.Ordinal)
            ? (_isEditingTranslationProvider ? Visibility.Visible : Visibility.Collapsed)
            : Visibility.Collapsed;
        BailianTranslationConfigPanel.Visibility = string.Equals(provider, "BailianQwenMt", StringComparison.Ordinal)
            ? (_isEditingTranslationProvider ? Visibility.Visible : Visibility.Collapsed)
            : Visibility.Collapsed;
    }

    private void SaveMachineCredentialsButton_Click(object sender, RoutedEventArgs e)
    {
        var accessKeyId = string.IsNullOrWhiteSpace(MachineAccessKeyIdTextBox.Text)
            ? ResourceTitleTranslationCredentialStore.GetMachineAccessKeyId()
            : MachineAccessKeyIdTextBox.Text.Trim();
        var accessKeySecret = string.IsNullOrWhiteSpace(MachineAccessKeySecretPasswordBox.Password)
            ? ResourceTitleTranslationCredentialStore.GetMachineAccessKeySecret()
            : MachineAccessKeySecretPasswordBox.Password.Trim();

        if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(accessKeySecret))
        {
            StatusTextBlock.Text = Strings.ResourceManagerCredentialsMissing.GetLocalizedResource();
            return;
        }

        try
        {
            ResourceTitleTranslationCredentialStore.SaveMachineTranslationCredentials(accessKeyId, accessKeySecret);
            MachineAccessKeyIdTextBox.Text = string.Empty;
            MachineAccessKeySecretPasswordBox.Password = string.Empty;
            MachineAccessKeyIdTextBox.PlaceholderText = Strings.ResourceManagerCredentialSaved.GetLocalizedResource();
            MachineAccessKeySecretPasswordBox.PlaceholderText = Strings.ResourceManagerCredentialSaved.GetLocalizedResource();
            StatusTextBlock.Text = Strings.ResourceManagerCredentialsSaved.GetLocalizedResource();
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"{Strings.ResourceManagerCredentialsSaveFailed.GetLocalizedResource()} {ex.Message}";
        }
    }

    private void RemoveMachineCredentialsButton_Click(object sender, RoutedEventArgs e)
    {
        ResourceTitleTranslationCredentialStore.RemoveMachineTranslationCredentials();
        MachineAccessKeyIdTextBox.PlaceholderText = Strings.ResourceManagerCredentialEmpty.GetLocalizedResource();
        MachineAccessKeySecretPasswordBox.PlaceholderText = Strings.ResourceManagerCredentialEmpty.GetLocalizedResource();
        StatusTextBlock.Text = Strings.ResourceManagerCredentialsRemoved.GetLocalizedResource();
    }

    private void SaveBailianCredentialsButton_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = string.IsNullOrWhiteSpace(BailianApiKeyPasswordBox.Password)
            ? ResourceTitleTranslationCredentialStore.GetBailianApiKey()
            : BailianApiKeyPasswordBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            StatusTextBlock.Text = Strings.ResourceManagerCredentialsMissing.GetLocalizedResource();
            return;
        }

        try
        {
            ResourceTitleTranslationCredentialStore.SaveBailianApiKey(apiKey);
            BailianApiKeyPasswordBox.Password = string.Empty;
            BailianApiKeyPasswordBox.PlaceholderText = Strings.ResourceManagerCredentialSaved.GetLocalizedResource();
            StatusTextBlock.Text = Strings.ResourceManagerCredentialsSaved.GetLocalizedResource();
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"{Strings.ResourceManagerCredentialsSaveFailed.GetLocalizedResource()} {ex.Message}";
        }
    }

    private void RemoveBailianCredentialsButton_Click(object sender, RoutedEventArgs e)
    {
        ResourceTitleTranslationCredentialStore.RemoveBailianApiKey();
        BailianApiKeyPasswordBox.PlaceholderText = Strings.ResourceManagerCredentialEmpty.GetLocalizedResource();
        StatusTextBlock.Text = Strings.ResourceManagerCredentialsRemoved.GetLocalizedResource();
    }

    private void RefreshSnapshotsButton_Click(object sender, RoutedEventArgs e)
        => RefreshSnapshots();

    private void RefreshSnapshots()
    {
        _snapshots.Clear();
        foreach (var snapshot in _workspace.ResourceToolSnapshots)
            _snapshots.Add(snapshot);
        SnapshotsEmptyTextBlock.Visibility = _snapshots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SnapshotsListView.SelectedItem = null;
        UpdateSnapshotButtons();
    }

    private void SnapshotsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateSnapshotButtons();

    private void UpdateSnapshotButtons()
    {
        var snapshot = SnapshotsListView.SelectedItem as ResourceToolSnapshot;
        RollbackSnapshotButton.IsEnabled = snapshot is not null && !snapshot.IsRestored;
        DeleteSnapshotButton.IsEnabled = snapshot is not null;
    }

    private async void RollbackSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotsListView.SelectedItem is not ResourceToolSnapshot snapshot || snapshot.IsRestored)
            return;

        if (!Directory.Exists(snapshot.LibraryPath))
        {
            SnapshotStatusTextBlock.Text = $"找不到快照对应的资源库：{snapshot.LibraryPath}";
            return;
        }

        if (XamlRoot is null)
            return;

        var confirmation = new ContentDialog
        {
            Title = "回退资源修改",
            Content = new TextBlock
            {
                Text = $"将按快照恢复文件名、文件夹位置和资源标签。若相关路径已被后续操作占用，这些项目会保留并报告冲突。快照回退后仍会保留。\n\n{snapshot.Action} · {snapshot.CreatedAt.LocalDateTime:g}\n{snapshot.Summary}",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
            },
            PrimaryButtonText = "确认回退",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            return;

        RollbackSnapshotButton.IsEnabled = false;
        DeleteSnapshotButton.IsEnabled = false;
        SnapshotStatusTextBlock.Text = "正在回退快照…";
        try
        {
            var results = await _operations.RestoreOperationsAsync(snapshot.LibraryPath, snapshot.Operations.ToList());
            foreach (var result in results)
            {
                var stored = snapshot.Operations.FirstOrDefault(operation =>
                    string.Equals(operation.Operation, result.Operation, StringComparison.OrdinalIgnoreCase)
                    && PathsEqual(operation.Source, result.Source)
                    && PathsEqual(operation.Target, result.Target));
                if (stored is null)
                    continue;
                stored.Status = result.Status;
                stored.Reason = result.Reason;
            }

            var failures = results.Where(result => result.Status is "failed" or "conflict").ToList();
            if (failures.Count == 0)
            {
                foreach (var assignment in snapshot.TagAssignments)
                    _workspace.SetResourceTagIds(assignment.ItemPath, assignment.TagIds);

                if (!string.IsNullOrWhiteSpace(snapshot.CreatedTagUid)
                    && !_workspace.IsResourceTagAssigned(snapshot.CreatedTagUid))
                    _workspace.DeleteResourceTag(snapshot.CreatedTagUid);

                snapshot.IsRestored = true;
                snapshot.RestoredAt = DateTimeOffset.Now;
                snapshot.RestoreSummary = $"已回退 {results.Count(result => result.Status is "restored" or "already_restored")} 项文件操作并恢复标签。";
                SnapshotStatusTextBlock.Text = snapshot.RestoreSummary;
            }
            else
            {
                snapshot.RestoreSummary = $"已回退部分修改，{failures.Count} 项因路径冲突或占用未完成；解决冲突后可重试。";
                SnapshotStatusTextBlock.Text = snapshot.RestoreSummary;
            }

            if (!_workspace.SaveResourceToolSnapshot(snapshot))
                SnapshotStatusTextBlock.Text += " 快照状态保存失败，请重试刷新后确认。";
            RefreshSnapshots();
            var refreshed = _snapshots.FirstOrDefault(item => string.Equals(item.Id, snapshot.Id, StringComparison.OrdinalIgnoreCase));
            if (refreshed is not null)
                SnapshotsListView.SelectedItem = refreshed;
        }
        catch (Exception ex)
        {
            SnapshotStatusTextBlock.Text = $"回退失败：{ex.Message}";
            UpdateSnapshotButtons();
        }
    }

    private async void DeleteSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotsListView.SelectedItem is not ResourceToolSnapshot snapshot || XamlRoot is null)
            return;

        var confirmation = new ContentDialog
        {
            Title = "删除回退快照",
            Content = "删除快照不会更改当前文件，但之后将无法再用它回退。",
            PrimaryButtonText = "删除快照",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (_workspace.DeleteResourceToolSnapshot(snapshot.Id))
            SnapshotStatusTextBlock.Text = "快照已删除。";
        else
            SnapshotStatusTextBlock.Text = "无法删除快照，请重试。";
        RefreshSnapshots();
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string GetCredentialPlaceholder(string credential)
        => string.IsNullOrWhiteSpace(credential)
            ? Strings.ResourceManagerCredentialEmpty.GetLocalizedResource()
            : Strings.ResourceManagerCredentialSaved.GetLocalizedResource();
}
