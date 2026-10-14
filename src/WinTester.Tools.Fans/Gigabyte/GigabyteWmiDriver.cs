using System;
using System.Management;
using System.Runtime.Versioning;

namespace WinTester.Tools.Fans.Gigabyte;

// Low-level GB_WMIACPI_Set / GB_WMIACPI_Get accessor for Gigabyte laptop firmware (root\WMI).
//
// The WMI class names, method names, and the "Data" in/out parameter name are reproduced exactly as
// confirmed against hardware.
//
// Deliberately silent on failure like every other WMI consumer in this codebase: a missing class, a
// method the board doesn't implement, or a stale handle after resume all degrade to "no value" rather
// than throwing at the caller.
[SupportedOSPlatform("windows")]
internal sealed class GigabyteWmiDriver : IDisposable
{
    private ManagementObject? _setInstance;
    private ManagementObject? _getInstance;
    private ManagementScope? _scope;

    public bool EnsureConnected()
    {
        try
        {
            if (_scope is not null && _scope.IsConnected && _setInstance is not null && _getInstance is not null)
                return true;

            var options = new ConnectionOptions
            {
                // Both required: GB_WMIACPI methods are ACPI-backed and reject the call without an
                // impersonated, privilege-enabled connection.
                EnablePrivileges = true,
                Impersonation = ImpersonationLevel.Impersonate
            };

            _scope = new ManagementScope(@"root\WMI", options);
            _scope.Connect();

            _setInstance = FirstInstance(_scope, "GB_WMIACPI_Set");
            _getInstance = FirstInstance(_scope, "GB_WMIACPI_Get");

            return _setInstance is not null && _getInstance is not null;
        }
        catch
        {
            return false;
        }
    }

    // Every Set method on GB_WMIACPI_Set takes a single byte in-parameter named "Data".
    public void SetMethod(string methodName, byte value)
    {
        try
        {
            if (_setInstance is null && (!EnsureConnected() || _setInstance is null)) return;

            ManagementBaseObject inParams = _setInstance.GetMethodParameters(methodName);
            inParams["Data"] = value;
            _setInstance.InvokeMethod(methodName, inParams, null);
        }
        catch (Exception ex)
        {
            InvalidateOnFailure(ex);
        }
    }

    // Every Get method on GB_WMIACPI_Get takes no in-parameters and returns a single out-parameter
    // named "Data". 0 is returned for any failure, and is also a legitimate reading (a parked fan), so
    // callers that need to distinguish the two have to do it from context — see GigabyteFanWorker's
    // zero-masking during Turbo.
    public uint GetMethod(string methodName)
    {
        try
        {
            if (_getInstance is null && (!EnsureConnected() || _getInstance is null)) return 0;

            ManagementBaseObject outParams = _getInstance.InvokeMethod(methodName, null, null);
            if (outParams?["Data"] is { } data)
                return Convert.ToUInt32(data);
        }
        catch (Exception ex)
        {
            InvalidateOnFailure(ex);
        }

        return 0;
    }

    // Drops the cached WMI handles after a connection-shaped failure so the next call reconnects. This
    // fixes a real failure: handles cached before a sleep/resume cycle stay non-null but fail every call
    // forever afterwards, which presents as the fan tool silently freezing its readings rather than
    // erroring.
    //
    // A method that simply isn't implemented on this board is explicitly not treated as a connection
    // failure — reconnecting can't make a missing method appear, and tearing down the scope on every
    // such call would reconnect on every poll for the life of the window.
    private void InvalidateOnFailure(Exception ex)
    {
        if (ex is ManagementException mex &&
            mex.ErrorCode is ManagementStatus.NotFound
                          or ManagementStatus.MethodNotImplemented
                          or ManagementStatus.InvalidMethod
                          or ManagementStatus.InvalidMethodParameters)
        {
            return;
        }

        _setInstance = null;
        _getInstance = null;
        _scope = null;
    }

    private static ManagementObject? FirstInstance(ManagementScope scope, string className)
    {
        var wmiClass = new ManagementClass(scope, new ManagementPath(className), null);
        foreach (ManagementObject instance in wmiClass.GetInstances())
            return instance;

        return null;
    }

    public void Dispose()
    {
        _setInstance?.Dispose();
        _getInstance?.Dispose();
        _setInstance = null;
        _getInstance = null;
        _scope = null;
    }
}
