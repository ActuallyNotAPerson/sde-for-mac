using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Sde.Field;

/// <summary>Which addresses the phone form listens on and accepts: private local networks and this computer only.</summary>
public static class NetworkAccess
{
	/// <summary>
	/// True for a private IPv4 address (10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16) or loopback (127.0.0.0/8, ::1).
	/// IPv4 addresses written as IPv6 (::ffff:a.b.c.d) are checked as IPv4. Everything else (public, link-local,
	/// other IPv6) is false.
	/// </summary>
	public static bool IsPrivateOrLoopback(IPAddress address)
	{
		if (address == null)
		{
			return false;
		}
		if (address.IsIPv4MappedToIPv6)
		{
			address = address.MapToIPv4();
		}
		if (address.AddressFamily == AddressFamily.InterNetworkV6)
		{
			return address.Equals(IPAddress.IPv6Loopback);
		}
		if (address.AddressFamily != AddressFamily.InterNetwork)
		{
			return false;
		}
		byte[] b = address.GetAddressBytes();
		return b[0] == 127 || IsPrivateIPv4(b);
	}

	/// <summary>True for 10.0.0.0/8, 172.16.0.0/12 and 192.168.0.0/16 (not loopback).</summary>
	public static bool IsPrivateLan(IPAddress address)
	{
		if (address == null)
		{
			return false;
		}
		if (address.IsIPv4MappedToIPv6)
		{
			address = address.MapToIPv4();
		}
		return address.AddressFamily == AddressFamily.InterNetwork && IsPrivateIPv4(address.GetAddressBytes());
	}

	private static bool IsPrivateIPv4(byte[] b) =>
		b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);

	/// <summary>
	/// The Mac's address on its active private network (Wi-Fi or wired), or null when it has none. Prefers an
	/// interface with a router (a gateway), then Wi-Fi on a Mac (en0).
	/// </summary>
	public static IPAddress FindLanAddress()
	{
		List<(IPAddress Address, int Rank)> found = new();
		foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
		{
			if (ni.OperationalStatus != OperationalStatus.Up
				|| ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
			{
				continue;
			}
			IPInterfaceProperties props;
			try
			{
				props = ni.GetIPProperties();
			}
			catch (NetworkInformationException)
			{
				continue;
			}
			bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
			foreach (UnicastIPAddressInformation u in props.UnicastAddresses)
			{
				if (IsPrivateLan(u.Address))
				{
					int rank = (hasGateway ? 2 : 0) + (ni.Name == "en0" ? 1 : 0);
					found.Add((u.Address, rank));
				}
			}
		}
		return found.OrderByDescending(f => f.Rank).Select(f => f.Address).FirstOrDefault();
	}

	/// <summary>A TCP port that is free on <paramref name="address"/> right now.</summary>
	internal static int FreePort(IPAddress address)
	{
		TcpListener listener = new TcpListener(address, 0);
		listener.Start();
		try
		{
			return ((IPEndPoint)listener.LocalEndpoint).Port;
		}
		finally
		{
			listener.Stop();
		}
	}
}
