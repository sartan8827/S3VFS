using Microsoft.Win32;
using System.Runtime.Versioning;

namespace S3VFS.ProjFs;

/// <summary>
/// Detects whether the Windows ProjFS optional feature ("Client-ProjFS") is
/// installed and not disabled, so mount startup can fail with an actionable
/// message instead of a raw <see cref="DllNotFoundException"/> or HRESULT. We
/// avoid the DISM API and just look at the kernel-mode driver's service
/// registration plus the user-mode library — both are present iff the feature
/// has been enabled and not since uninstalled.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProjFsFeature
{
    /// <summary>
    /// Service registry hive of the ProjFS minifilter driver (PrjFlt). Present
    /// when the optional feature has been turned on at least once.
    /// </summary>
    private const string PrjFltServiceKey = @"SYSTEM\CurrentControlSet\Services\PrjFlt";

    /// <summary>
    /// Disabled-service marker value of the <c>Start</c> column. A driver with
    /// <c>Start = 4</c> will not load on demand even if the feature is "installed".
    /// </summary>
    private const int ServiceStartDisabled = 4;

    /// <summary>
    /// Returns a remediation message describing why ProjFS is unavailable, or
    /// null when the driver is registered and the user-mode library is present.
    /// The most actionable failure is reported first ("feature missing"
    /// outranks "feature installed but service disabled").
    /// </summary>
    public static string? GetProblem()
    {
        using var serviceKey = Registry.LocalMachine.OpenSubKey(PrjFltServiceKey);
        if (serviceKey is null)
        {
            return "Windows ProjFS is not enabled (the PrjFlt driver is not registered). " +
                "Enable it from an elevated PowerShell with: " +
                "Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -NoRestart";
        }

        if (serviceKey.GetValue("Start") is int start && start == ServiceStartDisabled)
        {
            return "Windows ProjFS is disabled (PrjFlt service Start=4). Re-enable the optional " +
                "feature or set HKLM\\SYSTEM\\CurrentControlSet\\Services\\PrjFlt\\Start to 3.";
        }

        var userModeLib = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "ProjectedFSLib.dll");
        if (!File.Exists(userModeLib))
        {
            return $"Windows ProjFS is only partially installed ({userModeLib} is missing). " +
                "Re-run: Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS";
        }

        return null;
    }
}
