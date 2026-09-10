// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.DependencyInjection;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Xaml.Shapes;
using Files.App.Data.Models.AvManager;
using Files.App.Services.AvManager;
using Files.App.ViewModels.AvManager;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Files.App.Views.AvManager;

public sealed partial class AvManagerPage : Page
{
    private readonly AvManagerViewModel _vm = Ioc.Default.GetRequiredService<AvManagerViewModel>();
    private readonly IAvOperationsService _ops = Ioc.Default.GetRequiredService<IAvOperationsService>();
    private readonly IAvScanner _scanner = Ioc.Default.GetRequiredService<IAvScanner>();

    private List<AvResourceFolder> _allFolders = [];
    private List<AvFileOperation> _pendingOps = [];
    private string _filter = "全部";
    private CancellationTokenSource? _operationCancellation;

    public AvManagerPage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
        UpdateUI();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoaded;
        UpdateUI();
        if (string.IsNullOrWhiteSpace(_vm.LibraryPath))
            return;
        if (!Directory.Exists(_vm.LibraryPath))
        {
            SetStatus("上次资源库路径不可用，请重新选择文件夹");
            return;
        }

        await RefreshAsync();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e) => _operationCancellation?.Cancel();

    private CancellationTokenSource BeginOperation()
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        return _operationCancellation;
    }

    private void EndOperation(CancellationTokenSource operation)
    {
        if (ReferenceEquals(_operationCancellation, operation))
        {
            _operationCancellation = null;
            operation.Dispose();
        }
    }

    private void UpdateUI()
    {
        var has = !string.IsNullOrEmpty(_vm.LibraryPath);
        WelcomePanel.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        MainPanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string msg) => StatusText.Text = msg;

    private void ApplyFilter(string? kw = null)
    {
        FolderList.Items.Clear();
        IEnumerable<AvResourceFolder> list = _allFolders;
        list = _filter switch { "异常" => list.Where(f => !f.IsNormal), "缺视频" => list.Where(f => !f.HasVideo), "缺海报" => list.Where(f => !f.HasPoster), "无中字" => list.Where(f => !f.HasChineseSubtitle), "重复番号" => list.Where(f => f.Problems.Contains("重复番号")), _ => list };
        if (!string.IsNullOrWhiteSpace(kw)) { var k = kw.Trim().ToLower(); list = list.Where(f => f.Name.Contains(k, StringComparison.OrdinalIgnoreCase) || (f.Code?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false)); }
        foreach (var f in list)
        {
            var item = new ListViewItem { Tag = f };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Padding = new Thickness(8) };
            var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
            dot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(f.IsNormal ? Microsoft.UI.Colors.Green : Microsoft.UI.Colors.Red);
            panel.Children.Add(dot);
            var ns = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            ns.Children.Add(new TextBlock { Text = f.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            ns.Children.Add(new TextBlock { Text = f.Path, FontSize = 11, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            panel.Children.Add(ns);
            if (f.Code is not null)
            {
                var cb = new Border { Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"], CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
                cb.Child = new TextBlock { Text = f.Code, FontSize = 11, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemAccentColor"] };
                panel.Children.Add(cb);
            }
            var vs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            vs.Children.Add(new FontIcon { Glyph = "&#xE7F4;", FontSize = 12 });
            vs.Children.Add(new TextBlock { Text = f.VideoCount.ToString(), FontSize = 11 });
            panel.Children.Add(vs);
            item.Content = panel;
            FolderList.Items.Add(item);
        }
        SetStatus($"显示 {FolderList.Items.Count} / {_allFolders.Count} 个资源");
    }

    private async void OnChooseLibrary(object s, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        var hwnd = MainWindow.Instance.WindowHandle;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        _vm.SetLibraryPath(folder.Path);
        UpdateUI();
        await RefreshAsync();
    }

    private async void OnRefresh(object s, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (string.IsNullOrEmpty(_vm.LibraryPath)) return;
        using var operation = BeginOperation();
        var ct = operation.Token;
        ScanProgress.Visibility = Visibility.Visible; SetStatus("正在扫描...");
        try
        {
            var result = await _scanner.AnalyzeLibraryAsync(_vm.LibraryPath, _vm.Settings, ct);
            _allFolders = result.Folders; _vm.ScanResult = result;
            SummaryCards.Children.Clear();
            AddCard("资源总数", result.TotalFolders); AddCard("正常", result.NormalCount, Microsoft.UI.Colors.Green);
            AddCard("缺视频", result.MissingVideoCount, Microsoft.UI.Colors.Red); AddCard("缺海报", result.MissingPosterCount, Microsoft.UI.Colors.Orange);
            AddCard("低质海报", result.LowQualityPosterCount, Microsoft.UI.Colors.Orange);
            AddCard("无中字", result.NoChineseSubCount); AddCard("重复番号", result.DuplicateCodeCount, Microsoft.UI.Colors.OrangeRed);
            AddCard("散落视频", result.LooseVideoCount);
            ApplyFilter(); SetStatus($"扫描完成：共 {result.TotalFolders} 个资源，{result.ProblemCount} 个异常");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { SetStatus("已停止扫描"); }
        catch (Exception ex) { SetStatus($"扫描失败：{ex.Message}"); }
        finally { ScanProgress.Visibility = Visibility.Collapsed; EndOperation(operation); }
    }

    private void AddCard(string label, int value, Windows.UI.Color? color = null)
    {
        var b = new Border { Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"], CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 16, 12) };
        var s = new StackPanel();
        s.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        var v = new TextBlock { Text = value.ToString(), FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        if (color.HasValue) v.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(color.Value);
        s.Children.Add(v); b.Child = s; SummaryCards.Children.Add(b);
    }

    private void OnFilterChecked(object s, RoutedEventArgs e) { if (s is RadioButton rb && rb.Tag is string f) { _filter = f; ApplyFilter(SearchBox.Text); } }
    private void OnSearchChanged(AutoSuggestBox s, AutoSuggestBoxTextChangedEventArgs e) => ApplyFilter(s.Text);

    private void OnFolderSelected(object s, SelectionChangedEventArgs e)
    {
        if (FolderList.SelectedItem is ListViewItem item && item.Tag is AvResourceFolder folder)
        {
            DetailPanel.Visibility = Visibility.Visible; DetailContent.Children.Clear();
            AddDetail("名称", folder.Name); AddDetail("路径", folder.Path, true);
            var cp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            cp.Children.Add(new TextBlock { Text = folder.Code ?? "未识别", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemAccentColor"] });
            if (folder.Code is not null) { var btn = new Button { Content = "复制", Padding = new Thickness(4, 2, 4, 2), MinHeight = 0, FontSize = 11 }; btn.Click += (_, _) => { var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage(); pkg.SetText(folder.Code); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg); SetStatus($"已复制：{folder.Code}"); }; cp.Children.Add(btn); }
            DetailContent.Children.Add(new TextBlock { Text = "番号", FontSize = 12, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] }); DetailContent.Children.Add(cp);
            var sg = new Grid { ColumnSpacing = 16, RowSpacing = 8 }; sg.ColumnDefinitions.Add(new ColumnDefinition()); sg.ColumnDefinitions.Add(new ColumnDefinition()); sg.RowDefinitions.Add(new RowDefinition()); sg.RowDefinitions.Add(new RowDefinition());
            AddStatusCell(sg, 0, 0, "视频", $"{folder.VideoCount} 个"); AddStatusCell(sg, 1, 0, "海报", $"{folder.PosterCount} 张"); AddStatusCell(sg, 0, 1, "中文字幕", folder.HasChineseSubtitle ? "有" : "无"); AddStatusCell(sg, 1, 1, "低质海报", $"{folder.LowQualityPosterCount} 张");
            DetailContent.Children.Add(sg);
            if (folder.Problems.Count > 0) { DetailContent.Children.Add(new TextBlock { Text = "问题", FontSize = 12, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] }); foreach (var p in folder.Problems) { var pb = new Border { Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"], CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 2, 0, 2) }; pb.Child = new TextBlock { Text = p, FontSize = 11, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red) }; DetailContent.Children.Add(pb); } }
            var ob = new Button { Content = "在资源管理器中打开", HorizontalAlignment = HorizontalAlignment.Stretch }; ob.Click += (_, _) => { try { System.Diagnostics.Process.Start("explorer.exe", folder.Path); } catch (Exception ex) { SetStatus($"打开失败：{ex.Message}"); } }; DetailContent.Children.Add(ob);
        }
        else DetailPanel.Visibility = Visibility.Collapsed;
    }

    private void AddDetail(string label, string value, bool small = false)
    {
        DetailContent.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        var t = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap }; if (small) { t.FontSize = 11; t.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]; } else t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        DetailContent.Children.Add(t);
    }

    private void AddStatusCell(Grid g, int c, int r, string l, string v) { var s = new StackPanel(); s.Children.Add(new TextBlock { Text = l, FontSize = 12, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] }); s.Children.Add(new TextBlock { Text = v, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }); Grid.SetColumn(s, c); Grid.SetRow(s, r); g.Children.Add(s); }

    private async void OnPreviewRename(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_vm.LibraryPath)) return;
        using var operation = BeginOperation();
        var ct = operation.Token;
        SetStatus("正在预览重命名...");
        try
        {
            _pendingOps = await _ops.PreviewRenameVideosAsync(_vm.LibraryPath, _vm.Settings, ct);
            ShowPending();
            SetStatus($"预览完成：{_pendingOps.Count(o => o.Status == "ready")} 项就绪");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { SetStatus("已停止预览"); }
        catch (Exception ex) { SetStatus($"预览失败：{ex.Message}"); }
        finally { EndOperation(operation); }
    }

    private async void OnPreviewClassifySubs(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_vm.LibraryPath)) return;
        using var operation = BeginOperation();
        var ct = operation.Token;
        SetStatus("正在预览字幕分类...");
        try
        {
            _pendingOps = await _ops.PreviewClassifySubtitlesAsync(_vm.LibraryPath, _vm.Settings, ct);
            ShowPending();
            SetStatus($"预览完成：{_pendingOps.Count} 项待分类");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { SetStatus("已停止预览"); }
        catch (Exception ex) { SetStatus($"预览失败：{ex.Message}"); }
        finally { EndOperation(operation); }
    }

    private void OnStop(object s, RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
        _vm.CancelCurrentOperation();
        SetStatus("正在停止当前任务...");
    }
    private void ShowPending() { PendingBar.Visibility = _pendingOps.Count > 0 ? Visibility.Visible : Visibility.Collapsed; PendingCountText.Text = $"待执行：{_pendingOps.Count} 项操作"; }
    private async void OnExecutePending(object s, RoutedEventArgs e)
    {
        if (_pendingOps.Count == 0) return;
        using var operation = BeginOperation();
        var ct = operation.Token;
        SetStatus("正在执行...");
        try
        {
            var results = await _ops.ExecuteOperationsAsync(_vm.LibraryPath, _pendingOps.ToList(), ct);
            var failed = results.Where(x => x.Status is "failed" or "conflict").ToList();
            var succeeded = results.Count(x => x.Status == "done");
            _pendingOps = failed;
            ShowPending();
            SetStatus(failed.Count == 0
                ? $"执行完成：{succeeded} 项成功"
                : $"执行完成：{succeeded} 项成功，{failed.Count} 项失败或冲突，请检查后重试");
            await RefreshAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { SetStatus("已停止执行，未完成的项目仍保留在待处理列表"); }
        catch (Exception ex) { SetStatus($"执行失败：{ex.Message}"); }
        finally { EndOperation(operation); }
    }
    private void OnCancelPending(object s, RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
        _pendingOps.Clear();
        PendingBar.Visibility = Visibility.Collapsed;
        SetStatus("已取消");
    }
    private void OnResolveConflict(object s, RoutedEventArgs e) { if (s is Button b && b.Tag is string st) { _pendingOps = _ops.ApplyConflictStrategy(_pendingOps, st); ShowPending(); } }
    private async void OnUndo(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_vm.LibraryPath)) return;
        using var operation = BeginOperation();
        var ct = operation.Token;
        SetStatus("正在撤销...");
        try
        {
            var r = await _ops.UndoLastOperationAsync(_vm.LibraryPath, ct);
            SetStatus($"撤销完成：{r.Count(x => x.Status == "done")} 项已恢复");
            await RefreshAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { SetStatus("已停止撤销"); }
        catch (Exception ex) { SetStatus($"撤销失败：{ex.Message}"); }
        finally { EndOperation(operation); }
    }

    private async void OnSettings(object s, RoutedEventArgs e)
    {
        if (XamlRoot is null)
            return;

        var videoExtensions = new TextBox
        {
            Header = "视频扩展名（逗号分隔）",
            Text = string.Join(", ", _vm.Settings.VideoExtensions),
            PlaceholderText = "例如：mp4, mkv, avi"
        };
        var imageExtensions = new TextBox
        {
            Header = "海报扩展名（逗号分隔）",
            Text = string.Join(", ", _vm.Settings.ImageExtensions),
            PlaceholderText = "例如：jpg, png, webp"
        };
        var subtitleKeywords = new TextBox
        {
            Header = "中文字幕关键词（逗号分隔）",
            Text = string.Join(", ", _vm.Settings.SubtitleKeywords),
            PlaceholderText = "例如：中文字幕, chs, cht"
        };
        var posterQuality = new NumberBox
        {
            Header = "海报最低质量（KB）",
            Value = _vm.Settings.PosterQualityKb,
            Minimum = 1,
            Maximum = 1024 * 1024,
            SpinButtonPlacementMode = SpinButtonPlacementMode.Inline
        };
        var content = new StackPanel { Spacing = 12, Width = 440 };
        content.Children.Add(videoExtensions);
        content.Children.Add(imageExtensions);
        content.Children.Add(subtitleKeywords);
        content.Children.Add(posterQuality);

        var dialog = new ContentDialog
        {
            Title = "AV 资源规则",
            Content = new ScrollViewer { MaxHeight = 520, Content = content },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var settings = new AvSettings
        {
            VideoExtensions = ParseList(videoExtensions.Text),
            ImageExtensions = ParseList(imageExtensions.Text),
            SubtitleKeywords = ParseList(subtitleKeywords.Text),
            PosterQualityKb = double.IsNaN(posterQuality.Value) ? _vm.Settings.PosterQualityKb : (int)Math.Round(posterQuality.Value)
        };
        _vm.SaveSettings(settings);
        SetStatus("AV 资源规则已保存");
        if (!string.IsNullOrEmpty(_vm.LibraryPath))
            await RefreshAsync();
    }

    private static List<string> ParseList(string text) => text
        .Split([',', ';', '，', '；', '\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Trim())
        .Where(x => x.Length > 0)
        .ToList();
    private async void OnHistory(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_vm.LibraryPath)) return;
        try
        {
            await _vm.LoadHistoryCommand.ExecuteAsync(null);
            if (_vm.OperationHistory.Count == 0)
            {
                SetStatus("没有操作历史记录");
                return;
            }

            var historyText = string.Join(
                Environment.NewLine + Environment.NewLine,
                _vm.OperationHistory.Select(batch =>
                    $"{batch.CreatedDateTime:yyyy-MM-dd HH:mm:ss}  {batch.Summary}{Environment.NewLine}" +
                    string.Join(Environment.NewLine, batch.Operations.Select(op => $"  {op.Source} -> {op.Target}"))));

            if (XamlRoot is null)
            {
                SetStatus($"已加载 {_vm.OperationHistory.Count} 批操作记录");
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "操作历史",
                Content = new ScrollViewer
                {
                    MaxHeight = 480,
                    Content = new TextBlock { Text = historyText, TextWrapping = TextWrapping.Wrap }
                },
                CloseButtonText = "关闭",
                XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex) { SetStatus($"加载历史失败：{ex.Message}"); }
    }
}
