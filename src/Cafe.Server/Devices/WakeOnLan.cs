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

    /// <summary>
    /// The /24 broadcast address (a.b.c.255). An assumption: the subnet mask is not stored. 255.255.255.255 is
    /// always sent as well.
    /// </summary>
    public static IPAddress SubnetBroadcast(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return new IPAddress([b[0], b[1], b[2], 255]);
    }
}
