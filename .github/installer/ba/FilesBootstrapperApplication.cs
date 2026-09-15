using System;
using System.Diagnostics;
using System.IO;
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

    private InstallerWindow? window;
    private Dispatcher? dispatcher;
    private IBootstrapperCommand? command;
    private LaunchAction plannedAction;
    private bool mainPackageInstalled;
    private bool applying;
    private bool cancelRequested;
    private int result;
    private string? lastError;

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
    }

    protected override void Run()
    {
        dispatcher = Dispatcher.CurrentDispatcher;
        result = 0;

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

        engine.Detect();
        Dispatcher.Run();
        engine.Quit(result);
    }

    private void OnDetectPackageComplete(object? sender, DetectPackageCompleteEventArgs args)
    {
        if (string.Equals(args.PackageId, MainPackageId, StringComparison.OrdinalIgnoreCase))
            mainPackageInstalled = args.State == PackageState.Present;
    }

    private void OnDetectComplete(object? sender, DetectCompleteEventArgs args)
    {
        RunOnUi(() =>
        {
            if (args.Status != 0)
            {
                ShowFailure("无法完成安装环境检测。" + FormatLastError());
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
        RunOnUi(() =>
        {
            if (args.Status != 0)
            {
                ShowFailure("无法准备安装操作。" + FormatLastError());
                return;
            }

            applying = true;
            window?.ShowProgress();
            var parentHandle = window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
            engine.Apply(parentHandle);
        });
    }

    private void OnApplyBegin(object? sender, ApplyBeginEventArgs args)
    {
        RunOnUi(() => window?.ShowProgress());
    }

    private void OnProgress(object? sender, ProgressEventArgs args)
    {
        RunOnUi(() => window?.SetProgress(args.OverallPercentage, string.Empty));
        args.Cancel = cancelRequested;
    }

    private void OnExecutePackageBegin(object? sender, ExecutePackageBeginEventArgs args)
    {
        RunOnUi(() =>
        {
            if (window is not null)
                window.SetProgress(0, GetPackageMessage(args.PackageId));
        });
        args.Cancel = cancelRequested;
    }

    private void OnError(object? sender, ErrorEventArgs args)
    {
        lastError = args.ErrorMessage;
        args.Result = args.Recommendation;
    }

    private void OnApplyComplete(object? sender, ApplyCompleteEventArgs args)
    {
        applying = false;
        result = args.Status;

        RunOnUi(() =>
        {
            if (args.Status != 0)
            {
                ShowFailure("安装操作失败。" + FormatLastError());
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
                window?.ShowComplete("卸载成功完成", canLaunch: false);
            }
            else if (plannedAction == LaunchAction.Repair)
            {
                window?.ShowComplete("修复成功完成", canLaunch: true);
            }
            else
            {
                window?.ShowComplete("安装成功完成", canLaunch: true);
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
        window?.ShowProgress();
        engine.Plan(plannedAction, BundleScope.Default);
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
        try
        {
            return engine.GetVariableString(InstallFolderVariable);
        }
        catch
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Files max");
        }
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
        return string.Equals(packageId, MainPackageId, StringComparison.OrdinalIgnoreCase)
            ? "正在安装 Files max……"
            : "正在安装运行库……";
    }

    private string FormatLastError() => string.IsNullOrWhiteSpace(lastError) ? string.Empty : $"\n{lastError}";

    private void ShowFailure(string message)
    {
        if (window is not null)
        {
            window.ShowFailure(message);
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
        window?.AllowCloseAndClose();
        dispatcher?.BeginInvokeShutdown(DispatcherPriority.Background);
    }

    private void RunOnUi(Action action)
    {
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }
}
