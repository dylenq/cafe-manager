using System.Globalization;
using System.Net;

namespace Cafe.Server.Devices;

public static class WakeOnLan
{
    public const int PacketLength = 102;

    /// <summary>Accepts aa:bb:cc:dd:ee:ff or dashes in any case. 6 x 0xFF followed by the MAC 16 times.</summary>
    public static bool TryBuildMagicPacket(string? mac, out byte[] packet)
    {
        packet = [];
        if (string.IsNullOrWhiteSpace(mac))
        {
            return false;
        }

        var parts = mac.Trim().Split(':', '-');
        if (parts.Length != 6)
        {
            return false;
        }

        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (parts[i].Length != 2 || !Uri.IsHexDigit(parts[i][0]) || !Uri.IsHexDigit(parts[i][1])
                || !byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return false;
            }
        }

        var result = new byte[PacketLength];
        Array.Fill(result, (byte)0xFF, 0, 6);
        for (var i = 0; i < 16; i++)
        {
            Array.Copy(bytes, 0, result, 6 + (i * 6), 6);
        }

        packet = result;
        return true;
    }

    /// <summary>The up IPv4 interfaces of this machine: address and subnet mask.</summary>
    public readonly record struct LocalInterface(IPAddress Address, IPAddress Mask);

    /// <summary>
    /// Where to send the magic packet: the directed broadcast address of every local IPv4 interface whose subnet
    /// contains the TV. When none matches (or the interfaces cannot be listed) it falls back to the /24 guess plus
    /// 255.255.255.255, which leaves through the OS default interface only.
    /// </summary>
    public static IReadOnlyList<IPAddress> BroadcastTargets(IPAddress tv, IEnumerable<LocalInterface> interfaces)
    {
        var tvBytes = tv.GetAddressBytes();
        var found = new List<IPAddress>();
        foreach (var nic in interfaces)
        {
            var address = nic.Address.GetAddressBytes();
            var mask = nic.Mask.GetAddressBytes();
            if (address.Length != 4 || mask.Length != 4 || tvBytes.Length != 4)
            {
                continue;
            }

            var inSubnet = true;
            var broadcast = new byte[4];
            for (var i = 0; i < 4; i++)
            {
                inSubnet &= (address[i] & mask[i]) == (tvBytes[i] & mask[i]);
                broadcast[i] = (byte)(address[i] | ~mask[i]);
            }

            var target = new IPAddress(broadcast);
            if (inSubnet && !found.Contains(target))
            {
                found.Add(target);
            }
        }

        return found.Count > 0 ? found : [SubnetBroadcast(tv), IPAddress.Broadcast];
    }

    /// <summary>The /24 broadcast address (a.b.c.255): the fallback guess when no local interface matches the TV.</summary>
    public static IPAddress SubnetBroadcast(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return new IPAddress([b[0], b[1], b[2], 255]);
    }
}
