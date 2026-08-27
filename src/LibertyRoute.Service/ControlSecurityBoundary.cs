using System.Collections.ObjectModel;
using System.IO.Pipes;
using System.Security.Principal;
using LibertyRoute.ControlProtocol;

namespace LibertyRoute.Service;

internal sealed record ControlCallerIdentity
{
    internal ControlCallerIdentity(
        string userSid,
        IEnumerable<string> groupSids,
        bool isAuthenticated,
        bool isBuiltinAdministrator,
        bool hasNetworkLogonSid,
        bool isLocalSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        UserSid = userSid;
        GroupSids = new ReadOnlyCollection<string>(groupSids.Distinct(StringComparer.Ordinal).ToArray());
        IsAuthenticated = isAuthenticated;
        IsBuiltinAdministrator = isBuiltinAdministrator;
        HasNetworkLogonSid = hasNetworkLogonSid;
        IsLocalSystem = isLocalSystem;
    }

    public string UserSid { get; }
    public IReadOnlyList<string> GroupSids { get; }
    public bool IsAuthenticated { get; }
    public bool IsBuiltinAdministrator { get; }
    public bool HasNetworkLogonSid { get; }
    public bool IsLocalSystem { get; }
}

internal static class WindowsControlCallerIdentityCapture
{
    internal static string CanonicalizeUserSid(string? userSid)
    {
        if (string.IsNullOrWhiteSpace(userSid))
            throw new ArgumentException("A nonempty owner SID is required.", nameof(userSid));

        SecurityIdentifier sid;
        try
        {
            sid = new SecurityIdentifier(userSid);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("The owner SID is malformed.", nameof(userSid), exception);
        }

        if (!StringComparer.Ordinal.Equals(userSid, sid.Value))
            throw new ArgumentException("The owner SID must use its canonical representation.", nameof(userSid));

        return sid.Value;
    }

    internal static ControlCallerIdentity Capture(NamedPipeServerStream server)
    {
        ArgumentNullException.ThrowIfNull(server);

        ControlCallerIdentity? captured = null;
        server.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            var user = identity.User ?? throw new UnauthorizedAccessException("The caller token has no user SID.");
            var administratorSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var networkSid = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
            var localSystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var principal = new WindowsPrincipal(identity);

            captured = new ControlCallerIdentity(
                CanonicalizeUserSid(user.Value),
                identity.Groups?.Select(group => group.Value) ?? Array.Empty<string>(),
                identity.IsAuthenticated,
                principal.IsInRole(administratorSid),
                principal.IsInRole(networkSid),
                user.Equals(localSystemSid));
        });

        return captured ?? throw new UnauthorizedAccessException("The caller identity could not be captured.");
    }
}

internal enum ControlAuthorizationDecision
{
    Authorized,
    Unauthenticated,
    NetworkLogonDenied,
    Forbidden
}

internal sealed class ControlCommandAuthorization
{
    private readonly IReadOnlySet<ControlCommand> _forbiddenCommands;

    internal ControlCommandAuthorization(IEnumerable<ControlCommand>? forbiddenCommands = null)
    {
        _forbiddenCommands = new HashSet<ControlCommand>(
            forbiddenCommands ?? Array.Empty<ControlCommand>());
    }

    internal ControlAuthorizationDecision AuthorizePrincipal(ControlCallerIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!caller.IsAuthenticated)
            return ControlAuthorizationDecision.Unauthenticated;
        if (caller.HasNetworkLogonSid)
            return ControlAuthorizationDecision.NetworkLogonDenied;
        return caller.IsLocalSystem || caller.IsBuiltinAdministrator
            ? ControlAuthorizationDecision.Authorized
            : ControlAuthorizationDecision.Forbidden;
    }

    internal ControlAuthorizationDecision AuthorizeCommand(
        ControlCallerIdentity caller,
        ControlCommand command)
    {
        var principalDecision = AuthorizePrincipal(caller);
        if (principalDecision != ControlAuthorizationDecision.Authorized)
            return principalDecision;
        return Enum.IsDefined(command) && !_forbiddenCommands.Contains(command)
            ? ControlAuthorizationDecision.Authorized
            : ControlAuthorizationDecision.Forbidden;
    }
}

internal sealed class ControlServiceInstance
{
    internal ControlServiceInstance(Guid id)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("The service instance ID must be nonempty.", nameof(id));
        Id = id;
    }

    internal Guid Id { get; }

    internal static ControlServiceInstance CreateTransient()
        => new(Guid.NewGuid());
}
