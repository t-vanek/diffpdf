using System.Net;
using System.Net.Sockets;

namespace DiffPdf.Core.Discovery;

/// <summary>A CIDR subnet (e.g. <c>10.0.0.0/8</c>) with a membership test.</summary>
public sealed class IpSubnet
{
    private readonly byte[] _network;
    private readonly int _prefixLength;
    private readonly AddressFamily _family;

    private IpSubnet(byte[] network, int prefixLength, AddressFamily family)
    {
        _network = network;
        _prefixLength = prefixLength;
        _family = family;
    }

    /// <summary>Parses <c>address/prefix</c>; returns false for malformed input.</summary>
    public static bool TryParse(string cidr, out IpSubnet? subnet)
    {
        subnet = null;
        if (string.IsNullOrWhiteSpace(cidr))
            return false;

        string[] parts = cidr.Split('/', 2);
        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out var address)
            || !int.TryParse(parts[1], out int prefix))
            return false;

        byte[] bytes = address.GetAddressBytes();
        if (prefix < 0 || prefix > bytes.Length * 8)
            return false;

        subnet = new IpSubnet(bytes, prefix, address.AddressFamily);
        return true;
    }

    /// <summary>True when <paramref name="address"/> falls within this subnet.</summary>
    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != _family)
        {
            // Compare an IPv4-mapped IPv6 client against an IPv4 subnet.
            if (_family == AddressFamily.InterNetwork && address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            else
                return false;
        }

        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != _network.Length)
            return false;

        int fullBytes = _prefixLength / 8;
        for (int i = 0; i < fullBytes; i++)
        {
            if (bytes[i] != _network[i])
                return false;
        }

        int remainingBits = _prefixLength % 8;
        if (remainingBits == 0)
            return true;

        int mask = (byte)(0xFF << (8 - remainingBits));
        return (bytes[fullBytes] & mask) == (_network[fullBytes] & mask);
    }
}
