using System.Text;
using NetTopologySuite.Geometries;

namespace Sde.Gis;

/// <summary>One-time setup the GIS readers need before they open files.</summary>
public static class GisSetup
{
	private static int _done;

	/// <summary>The geometry factory for WGS 84 longitude and latitude (SRID 4326, double precision) that every reader uses.</summary>
	public static GeometryFactory Factory { get; } = new GeometryFactory(new PrecisionModel(), 4326);

	/// <summary>
	/// Registers the Windows and DOS code pages (<see cref="CodePagesEncodingProvider"/>), so .dbf files written in
	/// code page 1252, 437 or 850 (named by a .cpg file or the .dbf language byte) read correctly. Safe to call often.
	/// </summary>
	public static void EnsureInitialized()
	{
		if (Interlocked.Exchange(ref _done, 1) == 0)
		{
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		}
	}
}
