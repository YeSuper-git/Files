// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Files.App.Data.Models.ResourceManager;
using Files.App.Services.ResourceManager;
using Microsoft.Extensions.Logging;

namespace Files.App.ViewModels.ResourceManager;

public sealed partial class ResourceManagerViewModel : ObservableObject
{
    private readonly IResourceScanner _scanner;
    private readonly IResourceOperationsService _operations;
    private readonly IResourceWorkspaceService _workspace;
    private readonly ILogger<ResourceManagerViewModel> _logger;
    private CancellationTokenSource? _operationCancellation;

    // Partial properties keep the generated bindings AOT-compatible for WinUI 3.
    [ObservableProperty] public partial string LibraryPath { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsScanning { get; set; }
    [ObservableProperty] public partial bool IsOperating { get; set; }
    [ObservableProperty] public partial string StatusMessage { get; set; } = "选择一个本地文件夹作为资源库";
    [ObservableProperty] public partial ResourceScanResult? ScanResult { get; set; }
    [ObservableProperty] public partial ResourceFolder? SelectedFolder { get; set; }
    [ObservableProperty] public partial string CurrentFilter { get; set; } = "全部";
    [ObservableProperty] public partial string SearchKeyword { get; set; } = string.Empty;
    [ObservableProperty] public partial ResourceSettings Settings { get; set; } = new();
    [ObservableProperty] public partial bool HasPendingOps { get; set; }

    public ObservableCollection<ResourceFolder> DisplayFolders { get; } = [];
    public ObservableCollection<ResourceFileOperation> PendingOperations { get; } = [];
    public ObservableCollection<ResourceOperationBatch> OperationHistory { get; } = [];

    public ResourceManagerViewModel(IResourceScanner scanner, IResourceOperationsService operations, IResourceWorkspaceService workspace, ILogger<ResourceManagerViewModel> logger)
    {
        _scanner = scanner;
        _operations = operations;
        _workspace = workspace;
        _logger = logger;
        LibraryPath = workspace.LibraryPath;
        Settings = workspace.Settings.Clone();
    }

    public void SetLibraryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        LibraryPath = path;
        _workspace.SetLibraryPath(path);
    }

    public void SaveSettings(ResourceSettings settings)
    {
        settings.Normalize();
        Settings = settings.Clone();
        _workspace.UpdateSettings(Settings);
    }

    public void CancelCurrentOperation()
    {
        _operationCancellation?.Cancel();
    }

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

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (string.IsNullOrEmpty(LibraryPath)) return;
        using var operation = BeginOperation();
        var ct = operation.Token;
        IsScanning = true; StatusMessage = "正在扫描..."; SelectedFolder = null;
        try
        {
            ScanResult = await _scanner.AnalyzeLibraryAsync(LibraryPath, Settings, ct);
            StatusMessage = $"扫描完成：共 {ScanResult.TotalFolders} 个资源，{ScanResult.ProblemCount} 个异常";
            ApplyFilter();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { StatusMessage = "已停止扫描"; }
        catch (Exception ex) { StatusMessage = $"扫描失败：{ex.Message}"; }
        finally { IsScanning = false; EndOperation(operation); }
    }

    [RelayCommand]
    private void ApplyFilter()
    {
        DisplayFolders.Clear();
        if (ScanResult is null) return;
        var folders = CurrentFilter switch
        {
            "异常" => ScanResult.Folders.Where(f => !f.IsNormal),
            "缺视频" => ScanResult.Folders.Where(f => !f.HasVideo),
            "缺海报" => ScanResult.Folders.Where(f => !f.HasPoster),
            "无中字" => ScanResult.Folders.Where(f => !f.HasChineseSubtitle),
            "重复番号" => ScanResult.Folders.Where(f => f.Problems.Contains("重复番号")),
            _ => ScanResult.Folders.AsEnumerable()
        };
        if (!string.IsNullOrWhiteSpace(SearchKeyword))
        {
            var kw = SearchKeyword.Trim();
            folders = folders.Where(f => f.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) || (f.Code?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        foreach (var f in folders) DisplayFolders.Add(f);
    }

    [RelayCommand] private void SetFilter(string filter) { CurrentFilter = filter; ApplyFilter(); }
    partial void OnSearchKeywordChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task PreviewRenameAsync()
    {
        if (string.IsNullOrEmpty(LibraryPath)) return;
        using var operation = BeginOperation();
        var ct = operation.Token;
        IsOperating = true; StatusMessage = "正在预览重命名...";
        try { var ops = await _operations.PreviewRenameVideosAsync(LibraryPath, Settings, ct); PendingOperations.Clear(); foreach (var op in ops) PendingOperations.Add(op); HasPendingOps = PendingOperations.Count > 0; StatusMessage = $"预览完成：{ops.Count(o => o.Status == "ready")} 项就绪"; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { StatusMessage = "已停止预览"; }
        catch (Exception ex) { StatusMessage = $"预览失败：{ex.Message}"; }
        finally { IsOperating = false; EndOperation(operation); }
    }

    [RelayCommand]
    private async Task PreviewClassifySubsAsync()
    {
        if (string.IsNullOrEmpty(LibraryPath)) return;
        using var operation = BeginOperation();
        var ct = operation.Token;
        IsOperating = true; StatusMessage = "正在预览字幕分类...";
        try { var ops = await _operations.PreviewClassifySubtitlesAsync(LibraryPath, Settings, ct); PendingOperations.Clear(); foreach (var op in ops) PendingOperations.Add(op); HasPendingOps = PendingOperations.Count > 0; StatusMessage = $"预览完成：{ops.Count} 项待分类"; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { StatusMessage = "已停止预览"; }
        catch (Exception ex) { StatusMessage = $"预览失败：{ex.Message}"; }
        finally { IsOperating = false; EndOperation(operation); }
    }

    [RelayCommand]
    private async Task ExecutePendingAsync()
    {
        if (!HasPendingOps || string.IsNullOrEmpty(LibraryPath)) return;
        using var operation = BeginOperation();
        var ct = operation.Token;
        IsOperating = true; StatusMessage = "正在执行...";
        try { var results = await _operations.ExecuteOperationsAsync(LibraryPath, PendingOperations.ToList(), ct); StatusMessage = $"执行完成：{results.Count(r => r.Status == "done")} 项成功"; PendingOperations.Clear(); HasPendingOps = false; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { StatusMessage = "已停止执行"; }
        catch (Exception ex) { StatusMessage = $"执行失败：{ex.Message}"; }
        finally { IsOperating = false; EndOperation(operation); }
    }

    [RelayCommand] private void CancelPending() { PendingOperations.Clear(); HasPendingOps = false; StatusMessage = "已取消"; }

    [RelayCommand]
    private void ResolveConflict(string strategy) { var resolved = _operations.ApplyConflictStrategy(PendingOperations.ToList(), strategy); PendingOperations.Clear(); foreach (var op in resolved) PendingOperations.Add(op); }

    [RelayCommand]
    private async Task UndoLastAsync()
    {
        if (string.IsNullOrEmpty(LibraryPath)) return;
        using var operation = BeginOperation();
        var ct = operation.Token;
        IsOperating = true; StatusMessage = "正在撤销...";
        try { var results = await _operations.UndoLastOperationAsync(LibraryPath, ct); StatusMessage = $"撤销完成：{results.Count(r => r.Status == "done")} 项已恢复"; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { StatusMessage = "已停止撤销"; }
        catch (Exception ex) { StatusMessage = $"撤销失败：{ex.Message}"; }
        finally { IsOperating = false; EndOperation(operation); }
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        if (string.IsNullOrEmpty(LibraryPath)) return;
        try { var history = await _operations.GetOperationHistoryAsync(LibraryPath); OperationHistory.Clear(); foreach (var b in history.AsEnumerable().Reverse()) OperationHistory.Add(b); }
        catch (Exception ex) { StatusMessage = $"加载历史失败：{ex.Message}"; }
    }
}
