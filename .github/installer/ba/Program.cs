using System;
using System.IO;
using WixToolset.BootstrapperApplicationApi;

namespace FilesMax.Installer.Bootstrapper;

internal static class Program
{
    private static int Main()
    {
        try
        {
            ManagedBootstrapperApplication.Run(new FilesBootstrapperApplication());
            return 0;
        }
        catch (Exception exception)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "FilesMax.Installer.Bootstrapper.error.log"),
                    exception.ToString());
            }
            catch
            {
                // Preserve the original process failure even if the temp log cannot be written.
            }

            return exception.HResult;
        }
    }
}
