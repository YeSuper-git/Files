using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WixToolset.BootstrapperApplicationApi;

namespace FilesMax.Installer.Bootstrapper;

public sealed class FilesBootstrapperApplication : BootstrapperApplication
{
    private const string MainPackageId = "FilesInstallerMsi";
    private const string InstallFolderVariable = "InstallFolder";
    private const string EulaVariable = "EulaAcceptCheckbox";
    private const string InstallerScriptLogFileName = "Files max Installer-script.log";
    private const string InstallerErrorLogFileName = "Files max Installer-error.log";
    private const int MaxFailureClipboardCharacters = 24000;

    private InstallerWindow? window;
    private Dispatcher? dispatcher;
    private IBootstrapperCommand? command;
    private LaunchAction plannedAction;
    private bool mainPackageInstalled;
    private bool applying;
    private bool cancelRequested;
    private int result;
    private string? lastError;
    private string? currentPackageId;
    private string? currentAttemptId;
    private string currentStage = "准备安装";
    private DateTime operationStartedAt;

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetDesktopWindow();

    public FilesBootstrapperApplication()
    {
        DetectPackageComplete += OnDetectPackageComplete;
        DetectComplete += OnDetectComplete;
        PlanRelatedBundleType += OnPlanRelatedBundleType;
        PlanRelatedBundle += OnPlanRelatedBundle;
        PlanComplete += OnPlanComplete;
        ApplyBegin += OnApplyBegin;
        Progress += OnProgress;
        ExecutePackageBegin += OnExecutePackageBegin;
        Error += OnError;
        ApplyComplete += OnApplyComplete;
    }

    protected override void OnCreate(CreateEventArgs args)
    {
        base.OnCreate(args);
        command = args.Command;

        // A custom BA must explicitly apply overridable command-line
        // variables. WixStdBA does this internally, but Burn does not
        // automatically copy values such as InstallFolder into the engine
        // variable store for a custom application.
        try
        {
            var parsedCommand = command.ParseCommandLine();
            foreach (var variable in parsedCommand.Variables)
            {
                if (!string.Equals(variable.Key, InstallFolderVariable, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(variable.Value))
                {
                    continue;
                }

                engine.SetVariableString(InstallFolderVariable, variable.Value, formatted: false);
                LogDiagnostic($"Applied command-line InstallFolder={variable.Value}");
                break;
            }
        }
        catch (Exception exception)
        {
            LogDiagnostic($"Could not apply command-line variables: {exception.Message}");
        }

        LogDiagnostic($"OnCreate action={command.Action}, display={command.Display}");
    }

    protected override void Run()
    {
        dispatcher = Dispatcher.CurrentDispatcher;
        result = 0;
        operationStartedAt = DateTime.Now;
        LogDiagnostic("Run entered");

        if (command is not null && command.Display is Display.Full or Display.Passive)
        {
            window = new InstallerWindow();
            window.NextRequested += (_, _) => ShowOptions();
            window.BackRequested += (_, _) => ShowWelcome();
            window.InstallRequested += (_, _) => Install();
            window.RepairRequested += (_, _) => StartPlan(LaunchAction.Repair);
            window.UninstallRequested += (_, _) => StartPlan(LaunchAction.Uninstall);
            window.LaunchRequested += (_, _) => LaunchAndClose();
            window.CancelRequested += (_, _) => Cancel();
            window.LicenseChanged += (_, _) => SetEulaAccepted(window.LicenseAccepted);
            window.OpenErrorLogRequested += (_, _) => OpenInstallerErrorLog();
            window.FinalCloseRequested += (_, _) => CloseAndQuit(result);
            window.SetVersion(GetBundleVersion());
            window.ShowWelcome();
            window.Show();
        }

        LogDiagnostic("Starting detect");
        engine.Detect();
        Dispatcher.Run();
        LogDiagnostic($"Dispatcher stopped with result={result}");
        engine.Quit(result);
    }

    private void OnDetectPackageComplete(object? sender, DetectPackageCompleteEventArgs args)
    {
        if (string.Equals(args.PackageId, MainPackageId, StringComparison.OrdinalIgnoreCase))
        {
            mainPackageInstalled = args.State == PackageState.Present;
            LogDiagnostic($"Main package detect state={args.State}");
        }
    }

    private void OnDetectComplete(object? sender, DetectCompleteEventArgs args)
    {
        LogDiagnostic($"Detect complete status=0x{args.Status:X8}, display={command?.Display}, action={command?.Action}");
        RunOnUi(() =>
        {
            if (args.Status != 0)
            {
                currentStage = "检测安装环境";
                ShowFailure(FormatFailureDetails(args.Status, "检测安装环境"), "安装环境检测失败");
                return;
            }

            if (window is not null)
                window.SetInstallFolder(ReadInstallFolder());

            var requestedAction = GetCommandAction();
            if (requestedAction is LaunchAction.Uninstall or LaunchAction.UnsafeUninstall or LaunchAction.Layout or LaunchAction.Cache)
            {
                StartPlan(requestedAction);
                return;
            }

            Version? installedVersion = null;
            if (mainPackageInstalled && !TryGetInstalledInstallerVersion(out installedVersion, out var versionError))
            {
                currentStage = "检查已安装版本";
                ShowFailure(versionError ?? "无法确认当前安装版本，因此为避免覆盖或破坏现有安装，已停止本次操作。", "版本检查失败");
                return;
            }

            if (mainPackageInstalled && installedVersion is not null)
            {
                if (!Version.TryParse(GetBundleVersion(), out var incomingVersion))
                {
                    currentStage = "验证安装包版本";
                    ShowFailure($"当前安装包版本无效（{GetBundleVersion()}），无法与已安装版本 {installedVersion} 安全比较，因此已停止本次操作。", "版本检查失败");
                    return;
                }

                if (installedVersion.CompareTo(incomingVersion) > 0)
                {
                    currentStage = "阻止旧版本覆盖新版";
                    var details = $"检测到已安装 Files max {installedVersion}，当前安装包为 {incomingVersion}。为避免降级覆盖，安装已停止。请使用不低于已安装版本的安装包。";
                    LogDiagnostic(details);
                    ShowFailure(details, "不能安装较旧版本");
                    return;
                }
            }

            if (requestedAction == LaunchAction.Repair || command is null || command.Display != Display.Full)
            {
                StartPlan(requestedAction);
                return;
            }

            // PackageState.Present only tells us that an MSI with this package
            // identity is already registered. It does not mean that the
            // incoming bundle is the same version, and it must not force a
            // direct launch of a newer installer into maintenance mode.
            // Burn's Install plan performs the normal upgrade/repair decision
            // after the user confirms the install page. Repair and uninstall
            // launched from Apps & features still arrive above with an
            // explicit action and keep their dedicated flows.
            if (mainPackageInstalled)
                LogDiagnostic($"Existing MSI detected for direct {requestedAction} launch; showing the install/upgrade page.");

            ShowWelcome();
        });
    }

    private void OnPlanRelatedBundleType(object? sender, PlanRelatedBundleTypeEventArgs args)
    {
        if (plannedAction != LaunchAction.Install)
            return;

        // The one-time move from 4.2.32.x to the canonical 1.0.x line is a
        // numeric downgrade to Burn. Treat the detected previous Files max
        // bundle as an upgrade so Burn removes its registration after the new
        // MSI has migrated the installation. A registry version guard above
        // prevents this path from replacing a newer canonical 1.0.x install.
        args.Type = RelatedBundlePlanType.Upgrade;
        LogDiagnostic($"Treating related bundle {args.BundleCode} as the previous Files max release during canonical-version migration.");
    }

    private void OnPlanRelatedBundle(object? sender, PlanRelatedBundleEventArgs args)
    {
        if (plannedAction != LaunchAction.Install)
            return;

        args.State = RequestState.Absent;
        LogDiagnostic($"Scheduling previous related bundle {args.BundleCode} for removal after the canonical-version upgrade.");
    }

    private static bool TryGetInstalledInstallerVersion(out Version? version, out string? error)
    {
        version = null;
        error = null;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var productKey = baseKey.OpenSubKey("Software\\YeSuper\\Files", writable: false);
            var versionText = productKey?.GetValue("InstallerVersion") as string;
            if (string.IsNullOrWhiteSpace(versionText))
            {
                // Installations made before the canonical 1.0.x marker existed
                // are eligible for the one-time migration.
                return true;
            }

            if (!Version.TryParse(versionText, out version))
            {
                error = $"Windows 注册表中的 Files max 版本标记无效（{versionText}）。为保护现有安装，本次未继续。";
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            error = $"读取 Files max 安装版本时失败：{exception.Message}";
            return false;
        }
    }

    private void OnPlanComplete(object? sender, PlanCompleteEventArgs args)
    {
        LogDiagnostic($"Plan complete status=0x{args.Status:X8}, action={plannedAction}");
        RunOnUi(() =>
        {
            if (args.Status != 0)
            {
                currentStage = "生成安装计划";
                ShowFailure(FormatFailureDetails(args.Status, "生成安装计划"), GetActionFailureHeader());
                return;
            }

            applying = true;
            ShowProgress();
            var parentHandle = window is null
                ? GetDesktopWindow()
                : new WindowInteropHelper(window).Handle;
            engine.Apply(parentHandle);
        });
    }

    private void OnApplyBegin(object? sender, ApplyBeginEventArgs args)
    {
        currentStage = GetActionStage();
        currentPackageId = null;
        LogDiagnostic("Apply begin");
        RunOnUi(ShowProgress);
    }

    private void OnProgress(object? sender, ProgressEventArgs args)
    {
        RunOnUi(() => window?.SetProgress(args.OverallPercentage, string.Empty));
        args.Cancel = cancelRequested;
    }

    private void OnExecutePackageBegin(object? sender, ExecutePackageBeginEventArgs args)
    {
        currentPackageId = args.PackageId;
        currentStage = GetPackageStage(args.PackageId);
        LogDiagnostic($"Execute package begin package={args.PackageId}, stage={currentStage}");
        RunOnUi(() =>
        {
            if (window is not null)
                window.SetProgressMessage(GetPackageMessage(args.PackageId));
        });
        args.Cancel = cancelRequested;
    }

    private void OnError(object? sender, WixToolset.BootstrapperApplicationApi.ErrorEventArgs args)
    {
        var errorMessage = args.ErrorMessage?.Trim();
        if (!string.IsNullOrWhiteSpace(errorMessage))
            lastError = errorMessage;

        LogDiagnostic($"Burn error stage={currentStage}, package={currentPackageId ?? "none"}, message={errorMessage ?? "(empty)"}");
        args.Result = args.Recommendation;
    }

    private void OnApplyComplete(object? sender, ApplyCompleteEventArgs args)
    {
        applying = false;
        result = args.Status;
        LogDiagnostic($"Apply complete status=0x{args.Status:X8}");

        RunOnUi(() =>
        {
            if (args.Status != 0)
            {
                ShowFailure(FormatFailureDetails(args.Status, currentStage), GetActionFailureHeader());
                return;
            }

            if (command?.Display != Display.Full)
            {
                CloseAndQuit(0);
                return;
            }

            window?.SetBusy(false);
            if (plannedAction is LaunchAction.Uninstall or LaunchAction.UnsafeUninstall)
            {
                window?.ShowComplete("卸载完成", "Files max 已从这台电脑移除。", canLaunch: false);
            }
            else if (plannedAction == LaunchAction.Repair)
            {
                window?.ShowComplete("修复完成", "Files max 已修复完成，可以重新启动应用。", canLaunch: true);
            }
            else
            {
                window?.ShowComplete("安装完成", "Files max 已安装完成，可以开始使用了。", canLaunch: true);
            }
        });
    }

    private void ShowWelcome()
    {
        if (window is null)
            return;

        window.SetVersion(GetBundleVersion());
        window.ShowWelcome();
        window.SetBusy(false);
        SetEulaAccepted(false);
    }

    private void ShowOptions()
    {
        if (window is null)
            return;

        window.ShowOptions();
        window.SetBusy(false);
    }

    private void ShowModify()
    {
        if (window is null)
            return;

        window.ShowModify();
        window.SetBusy(false);
    }

    private void Install()
    {
        if (window is null || !window.LicenseAccepted || string.IsNullOrWhiteSpace(window.InstallFolder))
            return;

        engine.SetVariableNumeric(EulaVariable, 1);
        engine.SetVariableString(InstallFolderVariable, window.InstallFolder, formatted: false);
        StartPlan(LaunchAction.Install);
    }

    private void StartPlan(LaunchAction action)
    {
        if (applying)
            return;

        plannedAction = action == LaunchAction.Unknown ? LaunchAction.Install : action;
        cancelRequested = false;
        lastError = null;
        currentPackageId = null;
        currentStage = GetActionStage();
        operationStartedAt = DateTime.Now;
        var attemptSuffix = Guid.NewGuid().ToString("N")[..8];
        var attemptId = $"{operationStartedAt:yyyyMMdd-HHmmss}-{attemptSuffix}";
        currentAttemptId = attemptId;
        try
        {
            engine.SetVariableString("InstallerAttemptId", attemptId, formatted: false);
        }
        catch (Exception exception)
        {
            LogDiagnostic($"Could not pass attempt id to MSI: {exception.Message}");
        }

        LogDiagnostic($"Starting plan action={plannedAction}");
        ShowProgress();
        engine.Plan(plannedAction, BundleScope.Default);
    }

    private void ShowProgress()
    {
        if (window is null)
            return;

        if (plannedAction is LaunchAction.Uninstall or LaunchAction.UnsafeUninstall)
        {
            window.ShowProgress("正在卸载", "正在卸载 Files max……");
        }
        else if (plannedAction == LaunchAction.Repair)
        {
            window.ShowProgress("正在修复", "正在修复 Files max……");
        }
        else
        {
            window.ShowProgress("正在安装", "正在准备安装……");
        }
    }

    private LaunchAction GetCommandAction()
    {
        if (command is null || command.Action == LaunchAction.Unknown)
            return LaunchAction.Install;

        return command.Action;
    }

    private void Cancel()
    {
        if (applying)
        {
            cancelRequested = true;
            window?.SetBusy(true);
            return;
        }

        CloseAndQuit(result);
    }

    private void LaunchAndClose()
    {
        try
        {
            var installFolder = ReadInstallFolder();
            var executable = Path.Combine(installFolder, "Files.exe");
            if (File.Exists(executable))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = installFolder,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception exception)
        {
            engine.Log(LogLevel.Error, $"Could not launch Files.exe: {exception}");
        }

        CloseAndQuit(0);
    }

    private void SetEulaAccepted(bool accepted)
    {
        engine.SetVariableNumeric(EulaVariable, accepted ? 1 : 0);
    }

    private string ReadInstallFolder()
    {
        string value;
        try
        {
            value = engine.GetVariableString(InstallFolderVariable);
        }
        catch
        {
            value = string.Empty;
        }

        // The Bundle variable is declared as formatted for Burn/MSI, but a
        // custom BA can still receive the unexpanded token before the user
        // starts the plan. Never expose that token in the editable path box
        // or pass it to FolderBrowserDialog.
        if (string.IsNullOrWhiteSpace(value) ||
            (value.StartsWith("[", StringComparison.Ordinal) && value.Contains(']')))
        {
            return GetDefaultInstallFolder();
        }

        return value;
    }

    private static string GetDefaultInstallFolder()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return Path.Combine(
            string.IsNullOrWhiteSpace(programFiles) ? @"C:\Program Files" : programFiles,
            "Files max");
    }

    private string GetBundleVersion()
    {
        try
        {
            return engine.GetVariableString("WixBundleVersion");
        }
        catch
        {
            return "未知";
        }
    }

    private string GetPackageMessage(string packageId)
    {
        var packageName = string.Equals(packageId, MainPackageId, StringComparison.OrdinalIgnoreCase)
            ? "Files max"
            : "运行库";

        return plannedAction switch
        {
            LaunchAction.Uninstall or LaunchAction.UnsafeUninstall => $"正在卸载 {packageName}……",
            LaunchAction.Repair => $"正在修复 {packageName}……",
            _ => $"正在安装 {packageName}……",
        };
    }

    private string GetActionStage() => plannedAction switch
    {
        LaunchAction.Uninstall or LaunchAction.UnsafeUninstall => "卸载 Files max",
        LaunchAction.Repair => "修复 Files max",
        _ => "安装 Files max",
    };

    private string GetPackageStage(string packageId)
    {
        var packageName = string.Equals(packageId, MainPackageId, StringComparison.OrdinalIgnoreCase)
            ? "Files max 主程序"
            : "Microsoft Visual C++ 运行库";

        return plannedAction switch
        {
            LaunchAction.Uninstall or LaunchAction.UnsafeUninstall => $"卸载 {packageName}",
            LaunchAction.Repair => $"修复 {packageName}",
            _ => $"安装 {packageName}",
        };
    }

    private string GetActionFailureHeader() => plannedAction switch
    {
        LaunchAction.Uninstall or LaunchAction.UnsafeUninstall => "卸载失败",
        LaunchAction.Repair => "修复失败",
        _ => "安装失败",
    };

    private string FormatFailureDetails(int status, string? stage = null)
    {
        var details = new List<string>
        {
            $"失败阶段：{stage ?? currentStage}",
            $"错误代码：0x{unchecked((uint)status):X8}（{status}）",
        };

        if (!string.IsNullOrWhiteSpace(currentAttemptId))
            details.Add($"本次操作编号：{currentAttemptId}");

        var statusHint = GetStatusHint(status);
        if (!string.IsNullOrWhiteSpace(statusHint))
            details.Add($"系统提示：{statusHint}");

        if (!string.IsNullOrWhiteSpace(currentPackageId))
            details.Add($"失败组件：{GetPackageDisplayName(currentPackageId)}");

        if (!string.IsNullOrWhiteSpace(lastError))
            details.Add($"Burn 错误：{lastError}");

        var scriptErrorPath = FindRecentNonEmptyInstallerLog(InstallerErrorLogFileName);
        var scriptError = scriptErrorPath is null ? null : ReadInstallerErrorDetails(scriptErrorPath, 4500);
        if (!string.IsNullOrWhiteSpace(scriptError))
        {
            details.Add(string.Empty);
            details.Add("安装脚本详细错误：");
            details.Add(scriptError);
        }

        var scriptLogPath = FindRecentNonEmptyInstallerLog(InstallerScriptLogFileName);
        var scriptLog = scriptLogPath is null ? null : ReadInstallerLog(scriptLogPath);
        if (string.IsNullOrWhiteSpace(scriptError) && !string.IsNullOrWhiteSpace(scriptLog))
        {
            details.Add(string.Empty);
            details.Add("安装脚本过程日志（末尾）：");
            details.Add(scriptLog);
        }

        var bundleLogPath = FindRecentBundleLog();
        details.Add(string.Empty);
        details.Add($"脚本过程日志：{scriptLogPath ?? "未找到（脚本未能写出过程日志；可能在日志初始化前退出）"}");
        details.Add($"脚本错误日志：{scriptErrorPath ?? "未找到（脚本未能写出错误日志；请结合 MSI 首个失败动作判断）"}");
        if (!string.IsNullOrWhiteSpace(bundleLogPath))
            details.Add($"MSI/启动器日志：{bundleLogPath}");
        else
            details.Add("MSI/启动器日志：未在临时目录中找到本次日志");
        details.Add($"搜索位置：{string.Join("；", GetInstallerLogRoots())}");
        return string.Join(Environment.NewLine, details);
    }

    private static string? GetStatusHint(int status) => unchecked((uint)status) switch
    {
        0x80070005 => "权限不足。请关闭正在运行的 Files max，并以管理员身份重试。",
        0x80073D02 => "应用文件或相关资源仍被占用。请关闭 Files max 及其后台进程后重试。",
        0x80073CF3 => "应用包校验、版本或依赖关系不满足要求。请确认安装包完整，并卸载旧版本后重试。",
        0x80073CF6 => "应用身份注册失败。请查看上方的安装脚本详细错误和日志路径。",
        0x800B0109 => "签名证书不受信任。请确认安装包来自同一版本，并重新运行安装程序。",
        0x80070643 => "MSI 主程序包失败。该代码本身不是根因，请查看 MSI 首个失败动作、启动器错误和脚本异常上下文。",
        _ => null,
    };

    private string GetPackageDisplayName(string? packageId) =>
        string.Equals(packageId, MainPackageId, StringComparison.OrdinalIgnoreCase)
            ? "Files max 主程序"
            : "Microsoft Visual C++ 运行库";

    private List<string> GetInstallerLogRoots()
    {
        var tempRoots = new List<string> { Path.GetTempPath() };
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (!string.IsNullOrWhiteSpace(systemRoot))
            tempRoots.Add(Path.Combine(systemRoot, "Temp"));

        return tempRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<FileInfo> FindRecentInstallerLogFiles(string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var exactAttemptName = string.IsNullOrWhiteSpace(currentAttemptId)
            ? null
            : $"{baseName}-{currentAttemptId}{extension}";
        var minimumWriteTime = operationStartedAt == default
            ? DateTime.Now.AddMinutes(-1)
            : operationStartedAt;
        var matches = new List<FileInfo>();

        foreach (var root in GetInstallerLogRoots())
        {
            try
            {
                if (!Directory.Exists(root))
                    continue;

                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (exactAttemptName is not null)
                {
                    var exactPath = Path.Combine(root, exactAttemptName);
                    if (File.Exists(exactPath))
                        paths.Add(exactPath);
                }

                foreach (var path in Directory.EnumerateFiles(root, $"{baseName}-*{extension}"))
                    paths.Add(path);
                var legacyPath = Path.Combine(root, fileName);
                if (File.Exists(legacyPath))
                    paths.Add(legacyPath);

                foreach (var path in paths)
                {
                    var file = new FileInfo(path);
                    if (file.Length == 0)
                        continue;

                    var isCurrentAttempt = exactAttemptName is not null &&
                        string.Equals(file.Name, exactAttemptName, StringComparison.OrdinalIgnoreCase);
                    if (!isCurrentAttempt && file.LastWriteTime < minimumWriteTime)
                        continue;

                    matches.Add(file);
                }
            }
            catch (Exception exception)
            {
                LogDiagnostic($"Could not search installer logs in '{root}': {exception.Message}");
            }
        }

        return matches
            .OrderByDescending(file => exactAttemptName is not null &&
                string.Equals(file.Name, exactAttemptName, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(file => file.LastWriteTime)
            .ToList();
    }

    private string? FindRecentNonEmptyInstallerLog(string fileName) =>
        FindRecentInstallerLogFiles(fileName).FirstOrDefault()?.FullName;

    private string? ReadInstallerLog(string path)
    {
        try
        {
            var contents = File.ReadAllText(path).Trim();
            if (contents.Length <= 3200)
                return contents;

            return "……" + contents[^3200..];
        }
        catch (Exception exception)
        {
            LogDiagnostic($"Could not read installer log '{path}': {exception.Message}");
            return null;
        }
    }

    private string? FindRecentBundleLog()
    {
        return FindRecentBundleLogs().FirstOrDefault()?.FullName;
    }

    private List<FileInfo> FindRecentBundleLogs()
    {
        var minimumWriteTime = operationStartedAt == default
            ? DateTime.Now.AddMinutes(-1)
            : operationStartedAt;
        var bundleLogs = new List<FileInfo>();

        foreach (var root in GetInstallerLogRoots())
        {
            try
            {
                if (!Directory.Exists(root))
                    continue;

                bundleLogs.AddRange(Directory.EnumerateFiles(root, "Files_max_*.log")
                    .Select(path => new FileInfo(path))
                    .Where(file => file.Length > 0 && file.LastWriteTime >= minimumWriteTime));
            }
            catch (Exception exception)
            {
                LogDiagnostic($"Could not search bundle logs in '{root}': {exception.Message}");
            }
        }

        return bundleLogs
            .OrderByDescending(file => file.Name.EndsWith("_FilesInstallerMsi.log", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(file => file.LastWriteTime)
            .ToList();
    }

    private void OpenInstallerErrorLog()
    {
        var logPath = FindRecentNonEmptyInstallerLog(InstallerErrorLogFileName)
            ?? FindRecentNonEmptyInstallerLog(InstallerScriptLogFileName)
            ?? FindRecentBundleLog();

        try
        {
            if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
            {
                var fileExplorer = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                fileExplorer.ArgumentList.Add($"/select,{logPath}");
                Process.Start(fileExplorer);
                return;
            }

            var tempRoots = GetInstallerLogRoots();
            var logDirectory = tempRoots[0];
            var explorer = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            explorer.ArgumentList.Add(logDirectory);
            Process.Start(explorer);
            MessageBox.Show(window!,
                $"暂未找到可打开的日志，已打开：{logDirectory}\n本次操作编号：{currentAttemptId ?? "尚未开始"}\n也请检查：{string.Join("；", tempRoots)}",
                "未找到错误日志",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(window!,
                $"无法打开错误日志。\n{exception.Message}",
                "打开日志失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private string BuildFailureClipboardDetails(string message, string? header)
    {
        var details = new List<string>
        {
            "Files max 安装器诊断包",
            $"版本：{GetBundleVersion()}",
            $"操作：{(string.IsNullOrWhiteSpace(header) ? GetActionFailureHeader() : header)}",
            $"失败阶段：{currentStage}",
            $"本次操作编号：{currentAttemptId ?? "未生成（失败发生在安装计划前）"}",
            $"安装位置：{ReadInstallFolder()}",
        };

        if (!string.IsNullOrWhiteSpace(currentPackageId))
            details.Add($"失败组件：{GetPackageDisplayName(currentPackageId)} ({currentPackageId})");
        if (!string.IsNullOrWhiteSpace(lastError))
            details.Add($"Burn 错误：{lastError}");

        var bundleLogs = FindRecentBundleLogs();
        var msiLogPath = bundleLogs.FirstOrDefault(file =>
            file.Name.EndsWith("_FilesInstallerMsi.log", StringComparison.OrdinalIgnoreCase))?.FullName;
        var burnLogPath = bundleLogs.FirstOrDefault(file =>
            !file.Name.EndsWith("_FilesInstallerMsi.log", StringComparison.OrdinalIgnoreCase))?.FullName;
        AddFailureLogExcerpt(details, "MSI 首个失败动作及上下文", msiLogPath, FindMsiFailureContext);
        AddFailureLogExcerpt(details, "启动器/Burn 错误上下文", burnLogPath, path => ReadDiagnosticLogTail(path, 5000));

        var scriptErrorPath = FindRecentNonEmptyInstallerLog(InstallerErrorLogFileName);
        AddFailureLogExcerpt(details, "安装脚本异常（含异常类型、HRESULT、位置和堆栈）", scriptErrorPath, path => ReadInstallerErrorDetails(path, 4500));

        var scriptLogPath = FindRecentNonEmptyInstallerLog(InstallerScriptLogFileName);
        AddFailureLogExcerpt(details, "安装脚本过程日志（末尾）", scriptLogPath, path => ReadDiagnosticLogTail(path, 3500));

        details.Add("失败页面详情：");
        details.Add(message);
        details.Add(string.Empty);
        details.Add($"临时目录：{string.Join("；", GetInstallerLogRoots())}");

        var report = string.Join(Environment.NewLine, details);
        if (report.Length > MaxFailureClipboardCharacters)
        {
            report = report[..MaxFailureClipboardCharacters] + Environment.NewLine + "……（诊断内容已截断）";
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
            report = report.Replace(
                userProfile + Path.DirectorySeparatorChar,
                "%USERPROFILE%" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);

        return report;
    }

    private static void AddFailureLogExcerpt(
        List<string> details,
        string title,
        string? path,
        Func<string, string?> readExcerpt)
    {
        details.Add(string.Empty);
        details.Add($"{title}：");
        if (string.IsNullOrWhiteSpace(path))
        {
            details.Add("未找到本次操作对应的日志文件。");
            return;
        }

        details.Add($"日志文件：{path}");
        var excerpt = readExcerpt(path);
        details.Add(string.IsNullOrWhiteSpace(excerpt) ? "日志中没有可提取的文本内容。" : excerpt);
    }

    private static string? FindMsiFailureContext(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = CreateLogTailReader(stream, 0, out _);
            var previousLines = new Queue<string>();
            var tailLines = new Queue<string>();
            var contexts = new List<(int LineNumber, int Priority, string Marker, List<string> Lines)>();
            var seenPriorities = new HashSet<int>();
            List<string>? activeContext = null;
            var remainingContextLines = 0;
            var lineNumber = 0;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                lineNumber++;
                tailLines.Enqueue(line);
                if (tailLines.Count > 32)
                    tailLines.Dequeue();

                if (activeContext is not null)
                {
                    activeContext.Add(line);
                    remainingContextLines--;
                    if (remainingContextLines == 0)
                        activeContext = null;
                }
                else if (TryGetMsiFailureMarker(line, out var marker, out var priority) && seenPriorities.Add(priority))
                {
                    activeContext = previousLines.ToList();
                    activeContext.Add(line);
                    contexts.Add((lineNumber, priority, marker, activeContext));
                    remainingContextLines = 8;
                }

                previousLines.Enqueue(line);
                if (previousLines.Count > 18)
                    previousLines.Dequeue();
            }

            if (contexts.Count > 0)
            {
                var excerpts = contexts.OrderBy(context => context.Priority).ThenBy(context => context.LineNumber).Take(3)
                    .Select((context, index) =>
                    $"MSI 失败上下文 {index + 1}（第 {context.LineNumber} 行，匹配：{context.Marker}）{Environment.NewLine}" +
                    string.Join(Environment.NewLine, context.Lines.Select(TrimDiagnosticLine)));
                var excerpt = string.Join(Environment.NewLine + "--------------------" + Environment.NewLine, excerpts);
                return excerpt.Length <= 8500 ? excerpt : excerpt[..8500] + Environment.NewLine + "……（片段已截断）";
            }

            return "未在完整 MSI 日志中匹配到具体失败标记；以下为日志末尾：" + Environment.NewLine +
                string.Join(Environment.NewLine, tailLines.Select(TrimDiagnosticLine));
        }
        catch (Exception exception)
        {
            return $"读取日志片段失败：{exception.GetType().Name}: {exception.Message}";
        }
    }

    private static bool TryGetMsiFailureMarker(string line, out string marker, out int priority)
    {
        if (line.Contains("Return value 3", StringComparison.OrdinalIgnoreCase))
        {
            marker = "Return value 3";
            priority = 0;
        }
        else if (line.Contains("returned actual error code", StringComparison.OrdinalIgnoreCase))
        {
            marker = "returned actual error code";
            priority = 1;
        }
        else if (line.Contains("Error 1721", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("Error 1722", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("Error 1723", StringComparison.OrdinalIgnoreCase))
        {
            marker = "MSI custom action error";
            priority = 2;
        }
        else if (line.Contains("CustomAction", StringComparison.OrdinalIgnoreCase) &&
                 (line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                  line.Contains("returned", StringComparison.OrdinalIgnoreCase) ||
                  line.Contains("error", StringComparison.OrdinalIgnoreCase)))
        {
            marker = "CustomAction failure";
            priority = 3;
        }
        else if (line.Contains("access is denied", StringComparison.OrdinalIgnoreCase))
        {
            marker = "access denied";
            priority = 4;
        }
        else if (line.Contains("exception", StringComparison.OrdinalIgnoreCase))
        {
            marker = "exception";
            priority = 5;
        }
        else if (line.Contains("RegisterFilesIdentity", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("UnregisterFilesIdentity", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("WixQuietExec", StringComparison.OrdinalIgnoreCase))
        {
            marker = "MSI 自定义脚本动作";
            priority = 6;
        }
        else if (line.Contains("Error 1603", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("1603", StringComparison.OrdinalIgnoreCase) && line.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            marker = "MSI 返回 1603（通用失败码）";
            priority = 7;
        }
        else
        {
            marker = string.Empty;
            priority = int.MaxValue;
            return false;
        }

        return true;
    }

    private static string TrimDiagnosticLine(string line) =>
        line.Length <= 1200 ? line : line[..1200] + "……（本行已截断）";

    private static string? ReadDiagnosticLogTail(string path, int maxCharacters)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var startOffset = Math.Max(0, stream.Length - (maxCharacters * 4L));
            using var reader = CreateLogTailReader(stream, startOffset, out var skipPartialLine);
            if (skipPartialLine)
                reader.ReadLine();

            var contents = reader.ReadToEnd().Trim();
            return contents.Length <= maxCharacters ? contents : contents[^maxCharacters..];
        }
        catch (Exception exception)
        {
            return $"读取日志片段失败：{exception.GetType().Name}: {exception.Message}";
        }
    }

    private static string? ReadInstallerErrorDetails(string path, int maxCharacters)
    {
        try
        {
            var contents = File.ReadAllText(path).Trim();
            if (contents.Length <= maxCharacters)
                return contents;

            var headLength = maxCharacters * 2 / 3;
            var tailLength = maxCharacters - headLength;
            return contents[..headLength] + Environment.NewLine + "……（中间错误记录已省略）……" + Environment.NewLine + contents[^tailLength..];
        }
        catch (Exception exception)
        {
            return $"读取日志片段失败：{exception.GetType().Name}: {exception.Message}";
        }
    }

    private static StreamReader CreateLogTailReader(FileStream stream, long requestedOffset, out bool skipPartialLine)
    {
        var prefix = new byte[256];
        stream.Seek(0, SeekOrigin.Begin);
        var prefixLength = stream.Read(prefix, 0, prefix.Length);
        var encoding = Encoding.UTF8;
        var preambleLength = 0;
        var isUtf16 = false;

        if (prefixLength >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE)
        {
            encoding = Encoding.Unicode;
            preambleLength = 2;
            isUtf16 = true;
        }
        else if (prefixLength >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF)
        {
            encoding = Encoding.BigEndianUnicode;
            preambleLength = 2;
            isUtf16 = true;
        }
        else if (prefixLength >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
        {
            preambleLength = 3;
        }
        else
        {
            var evenNulls = 0;
            var oddNulls = 0;
            for (var index = 0; index < prefixLength; index++)
            {
                if (prefix[index] != 0)
                    continue;

                if (index % 2 == 0)
                    evenNulls++;
                else
                    oddNulls++;
            }

            if (oddNulls > 8 && oddNulls > evenNulls * 4)
            {
                encoding = Encoding.Unicode;
                isUtf16 = true;
            }
            else if (evenNulls > 8 && evenNulls > oddNulls * 4)
            {
                encoding = Encoding.BigEndianUnicode;
                isUtf16 = true;
            }
        }

        var startOffset = requestedOffset <= preambleLength ? 0 : requestedOffset;
        if (isUtf16 && startOffset > preambleLength && (startOffset - preambleLength) % 2 != 0)
            startOffset++;

        skipPartialLine = startOffset > preambleLength;
        stream.Seek(startOffset, SeekOrigin.Begin);
        return new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: startOffset == 0, bufferSize: 1024, leaveOpen: true);
    }

    private void ShowFailure(string message, string? header = null)
    {
        if (window is not null)
        {
            string clipboardDetails;
            try
            {
                clipboardDetails = BuildFailureClipboardDetails(message, header);
            }
            catch (Exception exception)
            {
                LogDiagnostic($"Could not assemble failure diagnostics: {exception.Message}");
                clipboardDetails = string.Join(Environment.NewLine, header ?? GetActionFailureHeader(), message);
            }

            window.ShowFailure(message, header, clipboardDetails);
            window.SetBusy(false);
        }
        else
        {
            CloseAndQuit(result == 0 ? 1 : result);
        }
    }

    private void CloseAndQuit(int exitCode)
    {
        result = exitCode;
        LogDiagnostic($"CloseAndQuit exitCode={exitCode}");
        window?.AllowCloseAndClose();
        dispatcher?.BeginInvokeShutdown(DispatcherPriority.Background);
    }

    private void LogDiagnostic(string message)
    {
        try
        {
            var attempt = string.IsNullOrWhiteSpace(currentAttemptId) ? "no-attempt" : currentAttemptId;
            engine.Log(LogLevel.Verbose, $"[Files max BA][{attempt}] {message}");
        }
        catch
        {
            // Diagnostic logging must never interfere with installation.
        }
    }

    private void RunOnUi(Action action)
    {
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }
}
