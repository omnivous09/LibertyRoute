using System.Net;
using LibertyRoute.Core;

namespace LibertyRoute.Restoration;

public enum RestorationCategory
{
    Adapter,
    Address,
    Gateway,
    Dns,
    Route
}

public enum DifferenceClassification
{
    Unchanged,
    Missing,
    Added,
    Changed,
    ExpectedDynamicState,
    Unverifiable
}

public enum RestorationIntent
{
    None,
    RestoreBaseline,
    NoAutomaticDeletion,
    ManualReview
}

public sealed record RestorationDifference(
    RestorationCategory Category,
    string Identity,
    string OriginalValue,
    string CurrentValue,
    DifferenceClassification Classification,
    RestorationIntent Intent,
    string Reason);

public sealed record RestorationPlan(IReadOnlyList<RestorationDifference> Differences)
{
    public bool IsEmpty => Differences.Count == 0;
}

public static class RestorationPlanner
{
    public static RestorationPlan CreatePlan(NetworkStateSnapshot? original, NetworkStateSnapshot? current)
    {
        if (original is null || current is null)
        {
            return new RestorationPlan(new[]
            {
                new RestorationDifference(
                    RestorationCategory.Adapter,
                    "snapshot",
                    original is null ? "missing" : "present",
                    current is null ? "missing" : "present",
                    DifferenceClassification.Unverifiable,
                    RestorationIntent.ManualReview,
                    "Both baseline and current snapshots are required for a read-only comparison.")
            });
        }

        var differences = new List<RestorationDifference>();
        CompareAdapters(original, current, differences);
        CompareDnsInterfaces(original.DnsInterfaces, current.DnsInterfaces, differences);
        CompareRoutes(original.Routes, current.Routes, differences);
        return new RestorationPlan(differences
            .OrderBy(difference => difference.Category)
            .ThenBy(difference => difference.Identity, StringComparer.Ordinal)
            .ThenBy(difference => difference.Classification)
            .ThenBy(difference => difference.OriginalValue, StringComparer.Ordinal)
            .ThenBy(difference => difference.CurrentValue, StringComparer.Ordinal)
            .ToArray());
    }

    private static void CompareAdapters(
        NetworkStateSnapshot original,
        NetworkStateSnapshot current,
        List<RestorationDifference> differences)
    {
        var originalAdapters = (original.Adapters ?? Array.Empty<AdapterState>())
            .ToDictionary(adapter => adapter.Id, StringComparer.OrdinalIgnoreCase);
        var currentAdapters = (current.Adapters ?? Array.Empty<AdapterState>())
            .ToDictionary(adapter => adapter.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var id in originalAdapters.Keys.Union(currentAdapters.Keys, StringComparer.OrdinalIgnoreCase))
        {
            originalAdapters.TryGetValue(id, out var baseline);
            currentAdapters.TryGetValue(id, out var present);
            var name = baseline?.Name ?? present?.Name ?? id;

            if (baseline is null)
            {
                differences.Add(Difference(
                    RestorationCategory.Adapter, id, "<absent>", AdapterValue(present!),
                    DifferenceClassification.Added, RestorationIntent.NoAutomaticDeletion,
                    "The adapter appeared after the baseline; additions are not automatically deleted."));
                continue;
            }

            if (present is null)
            {
                differences.Add(Difference(
                    RestorationCategory.Adapter, id, AdapterValue(baseline), "<absent>",
                    DifferenceClassification.Missing, RestorationIntent.RestoreBaseline,
                    "The baseline adapter is missing and may require restoration."));
                continue;
            }

            CompareAddressSet(id, name, baseline.UnicastAddresses, present.UnicastAddresses, differences);
            CompareStringSet(RestorationCategory.Gateway, id, baseline.Gateways, present.Gateways, differences,
                "A baseline gateway is missing and may require restoration.",
                "A gateway appeared after the baseline; it is not automatically deleted.");
            CompareDns(id, baseline, present, differences);
        }
    }

    private static void CompareAddressSet(string id, string name, IReadOnlyList<string>? baseline, IReadOnlyList<string>? present, List<RestorationDifference> differences)
    {
        CompareStringSet(RestorationCategory.Address, id, baseline, present, differences,
            "A baseline address is missing and may require restoration.",
            "An address appeared after the baseline; it is not automatically deleted.");
    }

    private static void CompareDns(string id, AdapterState baseline, AdapterState present, List<RestorationDifference> differences)
    {
        CompareStringSet(RestorationCategory.Dns, id, baseline.DnsServers, present.DnsServers, differences,
            "A baseline DNS server is missing and may require restoration.",
            "A DNS server appeared after the baseline; it is not automatically deleted.");

    }

    private static void CompareStringSet(
        RestorationCategory category,
        string identity,
        IReadOnlyList<string>? baseline,
        IReadOnlyList<string>? present,
        List<RestorationDifference> differences,
        string missingReason,
        string addedReason)
    {
        var originalValues = new HashSet<string>(baseline ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var currentValues = new HashSet<string>(present ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        var missingValues = originalValues.Except(currentValues, StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        var addedValues = currentValues.Except(originalValues, StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        var changedCount = Math.Min(missingValues.Length, addedValues.Length);

        for (var index = 0; index < changedCount; index++)
        {
            differences.Add(Difference(category, identity, missingValues[index], addedValues[index], DifferenceClassification.Changed, RestorationIntent.RestoreBaseline, "A baseline value differs from the current value and may require restoration."));
        }

        foreach (var value in missingValues.Skip(changedCount))
        {
            differences.Add(Difference(category, identity, value, "<absent>", DifferenceClassification.Missing, RestorationIntent.RestoreBaseline, missingReason));
        }

        foreach (var value in addedValues.Skip(changedCount))
        {
            differences.Add(Difference(category, identity, "<absent>", value, DifferenceClassification.Added, RestorationIntent.NoAutomaticDeletion, addedReason));
        }
    }

    private static void CompareDnsInterfaces(IReadOnlyList<DnsInterfaceState>? baseline, IReadOnlyList<DnsInterfaceState>? present, List<RestorationDifference> differences)
    {
        var originalInterfaces = (baseline ?? Array.Empty<DnsInterfaceState>()).ToDictionary(state => state.InterfaceId, StringComparer.OrdinalIgnoreCase);
        var currentInterfaces = (present ?? Array.Empty<DnsInterfaceState>()).ToDictionary(state => state.InterfaceId, StringComparer.OrdinalIgnoreCase);

        foreach (var id in originalInterfaces.Keys.Intersect(currentInterfaces.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            CompareStringSet(RestorationCategory.Dns, $"dns-interface:{id}", originalInterfaces[id].DnsServers, currentInterfaces[id].DnsServers, differences,
                "A baseline DNS interface server is missing and may require restoration.",
                "A DNS interface server appeared after the baseline; it is not automatically deleted.");
        }
    }

    private static void CompareRoutes(IReadOnlyList<RouteState>? baseline, IReadOnlyList<RouteState>? present, List<RestorationDifference> differences)
    {
        var originalRoutes = (baseline ?? Array.Empty<RouteState>()).GroupBy(RouteIdentity).ToDictionary(group => group.Key, group => group.OrderBy(RouteValue, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var currentRoutes = (present ?? Array.Empty<RouteState>()).GroupBy(RouteIdentity).ToDictionary(group => group.Key, group => group.OrderBy(RouteValue, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

        foreach (var identity in originalRoutes.Keys.Union(currentRoutes.Keys, StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
        {
            originalRoutes.TryGetValue(identity, out var originalGroup);
            currentRoutes.TryGetValue(identity, out var currentGroup);
            var originalValues = originalGroup ?? Array.Empty<RouteState>();
            var currentValues = currentGroup ?? Array.Empty<RouteState>();

            var pairCount = Math.Min(originalValues.Length, currentValues.Length);
            for (var index = 0; index < pairCount; index++)
            {
                if (!StringComparer.Ordinal.Equals(RouteValue(originalValues[index]), RouteValue(currentValues[index])))
                {
                    differences.Add(Difference(RestorationCategory.Route, identity, RouteValue(originalValues[index]), RouteValue(currentValues[index]), DifferenceClassification.Changed, RestorationIntent.RestoreBaseline, RouteChangeReason(originalValues[index], currentValues[index])));
                }
            }

            foreach (var value in originalValues.Skip(pairCount))
            {
                differences.Add(Difference(RestorationCategory.Route, identity, RouteValue(value), "<absent>", DifferenceClassification.Missing, RestorationIntent.RestoreBaseline, "A baseline route is missing and may require restoration."));
            }

            foreach (var value in currentValues.Skip(pairCount))
            {
                differences.Add(Difference(RestorationCategory.Route, identity, "<absent>", RouteValue(value), DifferenceClassification.Added, RestorationIntent.NoAutomaticDeletion, "A route appeared after the baseline; it is not automatically deleted."));
            }
        }
    }

    private static string RouteIdentity(RouteState route)
    {
        var (address, prefixLength) = ParsePrefix(route.Destination);
        return $"{route.AddressFamily}|{address}|{prefixLength}";
    }

    private static string RouteValue(RouteState route)
        => $"destination={CanonicalPrefix(route.Destination)};nextHop={route.NextHop};interfaceIndex={route.InterfaceIndex};metric={route.Metric};addressFamily={route.AddressFamily}";

    private static string RouteChangeReason(RouteState original, RouteState current)
    {
        var changes = new List<string>();
        if (!StringComparer.Ordinal.Equals(CanonicalPrefix(original.Destination), CanonicalPrefix(current.Destination))) changes.Add("destination");
        if (!StringComparer.Ordinal.Equals(original.NextHop, current.NextHop)) changes.Add("next hop");
        if (original.InterfaceIndex != current.InterfaceIndex) changes.Add("interface index");
        if (original.Metric != current.Metric) changes.Add("metric");
        if (!StringComparer.Ordinal.Equals(original.AddressFamily, current.AddressFamily)) changes.Add("address family");
        return "Baseline route changed: " + string.Join(", ", changes) + ".";
    }

    private static (string Address, int PrefixLength) ParsePrefix(string value)
    {
        var separator = value.LastIndexOf('/');
        if (separator < 0)
            separator = value.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(value[(separator + 1)..], out var prefixLength) || !IPAddress.TryParse(value[..separator], out var address))
            return (value, -1);
        return (address.ToString(), prefixLength);
    }

    private static string CanonicalPrefix(string value)
    {
        var (address, prefixLength) = ParsePrefix(value);
        return prefixLength < 0 ? value : $"{address}/{prefixLength}";
    }

    private static string AdapterValue(AdapterState adapter) => $"{adapter.Name}|{adapter.Description}|{adapter.NetworkInterfaceType}|{adapter.OperationalStatus}";

    private static RestorationDifference Difference(RestorationCategory category, string identity, string original, string current, DifferenceClassification classification, RestorationIntent intent, string reason)
        => new(category, identity, original, current, classification, intent, reason);
}
