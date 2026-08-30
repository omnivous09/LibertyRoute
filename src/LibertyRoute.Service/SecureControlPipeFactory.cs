using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using LibertyRoute.ControlProtocol;

namespace LibertyRoute.Service;

internal sealed class SecureControlPipeFactory
{
    private const PipeAccessRights DuplexClientRights = PipeAccessRights.ReadWrite;
    private const PipeAccessRights ServerInstanceRights =
        DuplexClientRights | PipeAccessRights.CreateNewInstance;
    internal const int MaximumActiveClients = 8;
    internal const int MaximumPipeInstances = MaximumActiveClients + 1;
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
        var interactiveSid = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);

        security.AddAccessRule(new PipeAccessRule(networkSid, DuplexClientRights, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(_serviceSid, ServerInstanceRights, AccessControlType.Allow));
        foreach (var allowedSid in new[] { localSystemSid, administratorsSid, interactiveSid }
                     .Distinct()
                     .Where(sid => !sid.Equals(_serviceSid)))
            security.AddAccessRule(new PipeAccessRule(allowedSid, DuplexClientRights, AccessControlType.Allow));

        return security;
    }

    internal NamedPipeServerStream Create(string pipeName)
        => Create(pipeName, firstInstance: true);

    internal NamedPipeServerStream Create(string pipeName, bool firstInstance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            MaximumPipeInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous |
                (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            ControlProtocolConstants.MaximumRequestSize,
            ControlProtocolConstants.MaximumResponseSize,
            CreateSecurity(),
            HandleInheritability.None,
            (PipeAccessRights)0);
    }
}
