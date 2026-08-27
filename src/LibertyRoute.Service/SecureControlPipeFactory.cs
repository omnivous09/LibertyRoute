using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using LibertyRoute.ControlProtocol;

namespace LibertyRoute.Service;

internal sealed class SecureControlPipeFactory
{
    private const PipeAccessRights DuplexClientRights = PipeAccessRights.ReadWrite;
    private readonly SecurityIdentifier _serviceSid;

    internal SecureControlPipeFactory(SecurityIdentifier serviceSid)
    {
        _serviceSid = serviceSid ?? throw new ArgumentNullException(nameof(serviceSid));
    }

    internal static SecureControlPipeFactory ForCurrentProcess()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return new SecureControlPipeFactory(
            identity.User ?? throw new InvalidOperationException("The service process token has no user SID."));
    }

    internal PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(_serviceSid);

        var networkSid = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        var localSystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        security.AddAccessRule(new PipeAccessRule(networkSid, DuplexClientRights, AccessControlType.Deny));
        foreach (var allowedSid in new[] { _serviceSid, localSystemSid, administratorsSid }.Distinct())
            security.AddAccessRule(new PipeAccessRule(allowedSid, DuplexClientRights, AccessControlType.Allow));

        return security;
    }

    internal NamedPipeServerStream Create(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            ControlProtocolConstants.MaximumRequestSize,
            ControlProtocolConstants.MaximumResponseSize,
            CreateSecurity(),
            HandleInheritability.None,
            (PipeAccessRights)0);
    }
}
