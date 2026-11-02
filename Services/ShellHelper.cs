using System;
using System.Runtime.InteropServices;

namespace VNotch.Services;

internal static class ShellHelper
{
    public static void ShowWindowsShutdownDialog()
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType != null)
            {
                dynamic? shell = Activator.CreateInstance(shellType);
                shell?.ShutdownWindows();
                if (shell != null && Marshal.IsComObject(shell))
                {
                    Marshal.ReleaseComObject(shell);
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("SHELL", $"Unable to show Windows shutdown dialog: {ex.Message}");
        }
    }
}
