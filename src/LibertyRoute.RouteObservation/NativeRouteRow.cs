using LibertyRoute.Core;

namespace LibertyRoute.RouteObservation;

public sealed record ExactNativeRouteRow(ExactRouteMutationIdentity Identity, uint InterfaceIndex, uint Age, uint Origin)
{
    public NativeRouteKey Key => Identity.Key;
    public NativeRouteProfile Profile => Identity.Profile;
    public byte SitePrefixLength => Profile.SitePrefixLength;
    public uint ValidLifetime => Profile.InitialValidLifetime;
    public uint PreferredLifetime => Profile.InitialPreferredLifetime;
    public uint Metric => Profile.Metric;
    public uint Protocol => Profile.Protocol;
    public bool Loopback => Profile.Loopback;
    public bool AutoconfigureAddress => Profile.AutoconfigureAddress;
    public bool Publish => Profile.Publish;
    public bool Immortal => Profile.Immortal;
}

public sealed record ExactRouteObservation(IReadOnlyList<ExactNativeRouteRow> Rows, bool Ipv4Complete,
    bool Ipv6Complete, bool Truncated, IReadOnlyList<string> IncompleteReasons)
{
    public bool Complete => Ipv4Complete && Ipv6Complete && !Truncated && IncompleteReasons.Count == 0;
}

internal static class NativeRouteEvidenceValidator
{
    public static bool IsValid(ExactNativeRouteRow row)
    {
        if (row is null || row.Identity is null || row.InterfaceIndex == 0) return false;
        try { row.Profile.ValidateFor(row.Key); return true; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { return false; }
    }
}
