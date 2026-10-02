// Copyright (c) Files Community. Licensed under the MIT License.
using Files.App.Services.ResourceManager;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.IO;
using Microsoft.Extensions.Logging;
namespace Files.App.Helpers;
public static class ResourceActorImportWorkflow
{
    public static async Task ImportAsync(string libraryPath, IResourceWorkspaceService workspace, XamlRoot xamlRoot,
        Action<string> setStatus, Func<Task>? refresh = null, Action<bool>? setBusy = null)
    {
        if (!Directory.Exists(libraryPath)) { setStatus(Strings.ResourceSettingsChooseLibraryFirst.GetLocalizedResource()); return; }

        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
        };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        setBusy?.Invoke(true);
        setStatus("正在读取演员资料包并匹配文件夹……");
        try
        {
            var targets = Directory.EnumerateDirectories(libraryPath, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new DirectoryInfo(path))
                .Where(ChangLiActorImportService.IsImportActorFolder)
                .Select(directory =>
                {
                    var details = workspace.GetActorDetails(directory.FullName);
                    return new ChangLiActorImportTarget(
                        directory.FullName,
                        directory.Name,
                        details.Name,
                        details.Aliases);
                })
                .ToArray();
            var plan = await ChangLiActorImportService.CreatePlanAsync(file.Path, targets);
            setBusy?.Invoke(false);

            if (plan.Matches.Count == 0)
            {
                setStatus($"没有找到可导入的匹配演员。未匹配 {plan.UnmatchedCount} 位，重名冲突 {plan.AmbiguousCount} 位。");
                return;
            }

            var overwriteCheckBox = new CheckBox
            {
                Content = "覆盖 Files 中已有资料和主海报；不勾选时只补空字段",
                IsChecked = false,
            };
            var summary = new StackPanel { Spacing = 10 };
            summary.Children.Add(new TextBlock
            {
                Text = $"导出文件包含 {plan.SourceActorCount} 位演员；按姓名、别名或日文名精确匹配到 {plan.Matches.Count} 个文件夹。未匹配 {plan.UnmatchedCount} 位，重名冲突 {plan.AmbiguousCount} 位。",
                TextWrapping = TextWrapping.Wrap,
            });
            summary.Children.Add(new TextBlock
            {
                Text = "导入会合并演员海报，并写入简介、生日、身高、体重、数值和罩杯。",
                TextWrapping = TextWrapping.Wrap,
            });
            summary.Children.Add(overwriteCheckBox);

            var dialog = new ContentDialog
            {
                Title = "导入演员资料",
                Content = summary,
                PrimaryButtonText = $"导入 {plan.Matches.Count} 位演员",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                setStatus("已取消导入。");
                return;
            }

            setBusy?.Invoke(true);
            setStatus("正在导入演员资料和海报……");
            var result = await ChangLiActorImportService.ApplyAsync(
                plan,
                workspace,
                overwriteCheckBox.IsChecked == true);
            if (refresh is not null) await refresh();
            setStatus($"已导入 {result.ImportedActors} 位演员，登记 {result.ImportedPhotos} 张海报；无法读取 {result.SkippedPhotos} 张。未匹配 {plan.UnmatchedCount} 位，重名冲突 {plan.AmbiguousCount} 位。");
        }
        catch (Exception ex)
        {
            App.Logger.LogError(ex, "Unable to import ChangLi actor data from {PackagePath}", file.Path);
            setStatus($"导入演员失败：{ex.Message}");
        }
        finally
        {
            setBusy?.Invoke(false);
        }
        }
}
