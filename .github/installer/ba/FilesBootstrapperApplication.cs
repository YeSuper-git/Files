using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
    private string currentStage = "准备安装";
    private DateTime operationStartedAt;

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetDesktopWindow();

    public FilesBootstrapperApplication()
    {
        DetectPackageComplete += OnDetectPackageComplete;
        DetectComplete += OnDetectComplete;
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

            if (command is null || command.Display != Display.Full)
            {
                StartPlan(GetCommandAction());
                return;
            }

            if (command.Action is LaunchAction.Uninstall or LaunchAction.UnsafeUninstall or LaunchAction.Repair or LaunchAction.Layout or LaunchAction.Cache)
            {
                StartPlan(command.Action);
                return;
            }

            if (mainPackageInstalled)
                ShowModify();
            else
                ShowWelcome();
        });
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
                window?.ShowComplete("卸载成功完成", "Files max 已经卸载完成。", canLaunch: false);
            }
            else if (plannedAction == LaunchAction.Repair)
            {
                window?.ShowComplete("修复成功完成", "Files max 已经修复完成。", canLaunch: true);
            }
            else
            {
                window?.ShowComplete("安装成功完成", "Files max 已经安装完成。", canLaunch: true);
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
            window.ShowProgress("卸载进度", "正在卸载 Files max……");
        }
        else if (plannedAction == LaunchAction.Repair)
        {
            window.ShowProgress("修复进度", "正在修复 Files max……");
        }
        else
        {
            window.ShowProgress("安装进度", "正在准备安装……");
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
            $"{GetActionFailureHeader()}：操作未完成。",
            string.Empty,
            $"失败阶段：{stage ?? currentStage}",
            $"错误代码：0x{unchecked((uint)status):X8}（{status}）",
        };

        var statusHint = GetStatusHint(status);
        if (!string.IsNullOrWhiteSpace(statusHint))
            details.Add($"系统提示：{statusHint}");

        if (!string.IsNullOrWhiteSpace(currentPackageId))
            details.Add($"失败组件：{GetPackageDisplayName(currentPackageId)}");

        if (!string.IsNullOrWhiteSpace(lastError))
            details.Add($"Burn 错误：{lastError}");

        var scriptError = ReadRecentInstallerLog(InstallerErrorLogFileName);
        if (!string.IsNullOrWhiteSpace(scriptError))
        {
            details.Add(string.Empty);
            details.Add("安装脚本详细错误：");
            details.Add(scriptError);
        }

        var scriptLogPath = Path.Combine(Path.GetTempPath(), InstallerScriptLogFileName);
        var errorLogPath = Path.Combine(Path.GetTempPath(), InstallerErrorLogFileName);
        details.Add(string.Empty);
        details.Add($"详细日志：{scriptLogPath}");
        details.Add($"错误日志：{errorLogPath}");
        details.Add("如果日志不在当前用户临时目录，请同时查看 C:\\Windows\\Temp 中的同名文件。");
        return string.Join(Environment.NewLine, details);
    }

    private static string? GetStatusHint(int status) => unchecked((uint)status) switch
    {
        0x80070005 => "权限不足。请关闭正在运行的 Files max，并以管理员身份重试。",
        0x80073D02 => "应用文件或相关资源仍被占用。请关闭 Files max 及其后台进程后重试。",
        0x80073CF3 => "应用包校验、版本或依赖关系不满足要求。请确认安装包完整，并卸载旧版本后重试。",
        0x80073CF6 => "应用身份注册失败。请查看上方的安装脚本详细错误和日志路径。",
        0x800B0109 => "签名证书不受信任。请确认安装包来自同一版本，并重新运行安装程序。",
        0x80070643 => "MSI 自定义安装步骤失败。上方的安装脚本详细错误会给出具体失败步骤。",
        _ => null,
    };

    private string GetPackageDisplayName(string? packageId) =>
        string.Equals(packageId, MainPackageId, StringComparison.OrdinalIgnoreCase)
            ? "Files max 主程序"
            : "Microsoft Visual C++ 运行库";

    private string? ReadRecentInstallerLog(string fileName)
    {
        var tempRoots = new List<string> { Path.GetTempPath() };
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (!string.IsNullOrWhiteSpace(systemRoot))
            tempRoots.Add(Path.Combine(systemRoot, "Temp"));

        foreach (var root in tempRoots)
        {
            var path = Path.Combine(root, fileName);
            try
            {
                if (!File.Exists(path))
                    continue;

                var lastWrite = File.GetLastWriteTime(path);
                if (operationStartedAt != default && lastWrite < operationStartedAt.AddMinutes(-2))
                    continue;

                var contents = File.ReadAllText(path).Trim();
                if (contents.Length <= 3200)
                    return contents;

                return "……" + contents[^3200..];
            }
            catch (Exception exception)
            {
                LogDiagnostic($"Could not read installer log '{path}': {exception.Message}");
            }
        }

        return null;
    }

    private void ShowFailure(string message, string? header = null)
    {
        if (window is not null)
        {
            window.ShowFailure(message, header);
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
            engine.Log(LogLevel.Verbose, $"[Files max BA] {message}");
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
