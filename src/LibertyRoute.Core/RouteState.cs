namespace LibertyRoute.Core;

public sealed class RouteState
{
    public string Destination { get; set; } = string.Empty;

    public string NextHop { get; set; } = string.Empty;

    public int InterfaceIndex { get; set; }

    public uint Metric { get; set; }

    public string AddressFamily { get; set; } = string.Empty;
}