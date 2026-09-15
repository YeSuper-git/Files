using System;
using WixToolset.BootstrapperApplicationApi;

namespace FilesMax.Installer.Bootstrapper;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        ManagedBootstrapperApplication.Run(new FilesBootstrapperApplication());
        return 0;
    }
}
