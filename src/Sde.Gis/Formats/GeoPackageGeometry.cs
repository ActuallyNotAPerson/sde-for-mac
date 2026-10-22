using System.Buffers.Binary;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Sde.Gis.Formats;

/// <summary>
/// GeoPackage geometry blobs (OGC GeoPackage 1.3, section 2.1.3): a "GP" header with flags, the SRS id and an optional
/// envelope, followed by standard WKB. SDE writes little-endian blobs with an XY envelope, two-dimensional WKB.
/// </summary>
public static class GeoPackageGeometry
{
	private const int HeaderLength = 8;

	private static readonly NetTopologySuite.NtsGeometryServices DefaultServices =
		new NetTopologySuite.NtsGeometryServices(GisSetup.Factory.CoordinateSequenceFactory, GisSetup.Factory.PrecisionModel, GisSetup.Factory.SRID);

	/// <summary>A blob for a geometry (in the coordinates of <paramref name="srsId"/>). Empty geometries get the empty flag and no envelope.</summary>
	public static byte[] Write(Geometry geometry, int srsId = 4326)
	{
		ArgumentNullException.ThrowIfNull(geometry);
		WKBWriter writer = new WKBWriter(ByteOrder.LittleEndian, false, false, false);
		byte[] wkb = writer.Write(geometry);
		bool empty = geometry.IsEmpty;
		int envelopeLength = empty ? 0 : 32;
		byte[] blob = new byte[HeaderLength + envelopeLength + wkb.Length];
		blob[0] = (byte)'G';
		blob[1] = (byte)'P';
		blob[2] = 0;
		// Flags: bit 0 little-endian; bits 1-3 envelope type (1 = minx, maxx, miny, maxy); bit 4 empty geometry.
		blob[3] = (byte)(0x01 | (empty ? 0x10 : 0x02));
		BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(4, 4), srsId);
		if (!empty)
		{
			Envelope e = geometry.EnvelopeInternal;
			BinaryPrimitives.WriteDoubleLittleEndian(blob.AsSpan(8, 8), e.MinX);
			BinaryPrimitives.WriteDoubleLittleEndian(blob.AsSpan(16, 8), e.MaxX);
			BinaryPrimitives.WriteDoubleLittleEndian(blob.AsSpan(24, 8), e.MinY);
			BinaryPrimitives.WriteDoubleLittleEndian(blob.AsSpan(32, 8), e.MaxY);
		}
		Buffer.BlockCopy(wkb, 0, blob, HeaderLength + envelopeLength, wkb.Length);
		return blob;
	}

	/// <summary>Reads a blob's geometry (two-dimensional; Z and M are dropped). Null for a null blob. Throws <see cref="FormatException"/> for anything that isn't a GeoPackage geometry.</summary>
	public static Geometry Read(byte[] blob, GeometryFactory factory = null) => Read(blob, out _, factory);

	/// <summary>Reads a blob's geometry and its SRS id.</summary>
	public static Geometry Read(byte[] blob, out int srsId, GeometryFactory factory = null)
	{
		srsId = 0;
		if (blob == null)
		{
			return null;
		}
		int wkbStart = ParseHeader(blob, out srsId, out bool empty, out _);
		factory ??= GisSetup.Factory;
		NetTopologySuite.NtsGeometryServices services = ReferenceEquals(factory, GisSetup.Factory)
			? DefaultServices
			: new NetTopologySuite.NtsGeometryServices(factory.CoordinateSequenceFactory, factory.PrecisionModel, factory.SRID);
		WKBReader reader = new WKBReader(services) { HandleOrdinates = Ordinates.XY };
		Geometry geometry;
		try
		{
			using MemoryStream wkb = new MemoryStream(blob, wkbStart, blob.Length - wkbStart, writable: false);
			geometry = reader.Read(wkb);
		}
		catch (Exception ex) when (ex is ParseException or ArgumentException or IOException or IndexOutOfRangeException)
		{
			throw new FormatException("The geometry's WKB can't be read: " + ex.Message, ex);
		}
		if (empty && !geometry.IsEmpty)
		{
			geometry = factory.CreateGeometryCollection();
		}
		return geometry;
	}

	/// <summary>The blob's envelope from its header, or computed from the WKB when the header has none. False for null and empty geometries.</summary>
	public static bool TryGetEnvelope(byte[] blob, out Envelope envelope)
	{
		envelope = null;
		if (blob == null)
		{
			return false;
		}
		int wkbStart = ParseHeader(blob, out _, out bool empty, out int envelopeType);
		if (empty)
		{
			return false;
		}
		if (envelopeType >= 1)
		{
			bool little = (blob[3] & 0x01) != 0;
			double D(int offset) => little ? BinaryPrimitives.ReadDoubleLittleEndian(blob.AsSpan(offset, 8)) : BinaryPrimitives.ReadDoubleBigEndian(blob.AsSpan(offset, 8));
			envelope = new Envelope(D(8), D(16), D(24), D(32));
			return true;
		}
		Geometry g = Read(blob);
		if (g == null || g.IsEmpty)
		{
			return false;
		}
		envelope = g.EnvelopeInternal;
		return true;
	}

	/// <summary>True when the blob is flagged empty (or holds an empty geometry).</summary>
	public static bool IsEmpty(byte[] blob)
	{
		if (blob == null)
		{
			return true;
		}
		ParseHeader(blob, out _, out bool empty, out int envelopeType);
		if (empty)
		{
			return true;
		}
		return envelopeType == 0 && (Read(blob)?.IsEmpty ?? true);
	}

	private static int ParseHeader(byte[] blob, out int srsId, out bool empty, out int envelopeType)
	{
		if (blob.Length < HeaderLength || blob[0] != (byte)'G' || blob[1] != (byte)'P')
		{
			throw new FormatException("This isn't a GeoPackage geometry (no GP header).");
		}
		byte flags = blob[3];
		bool little = (flags & 0x01) != 0;
		envelopeType = (flags >> 1) & 0x07;
		empty = (flags & 0x10) != 0;
		srsId = little ? BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(4, 4)) : BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(4, 4));
		int envelopeLength = envelopeType switch
		{
			0 => 0,
			1 => 32,
			2 or 3 => 48,
			4 => 64,
			_ => throw new FormatException($"Unknown GeoPackage envelope type {envelopeType}."),
		};
		if (blob.Length < HeaderLength + envelopeLength)
		{
			throw new FormatException("The GeoPackage geometry is cut short.");
		}
		return HeaderLength + envelopeLength;
	}
}
