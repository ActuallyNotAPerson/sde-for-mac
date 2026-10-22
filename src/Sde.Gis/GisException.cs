namespace Sde.Gis;

/// <summary>
/// A GIS file, coordinate system or online layer couldn't be used. <see cref="Exception.Message"/> is written for the
/// person using the app (plain English, says what to do); the technical detail is in <see cref="Detail"/> and
/// <see cref="Exception.InnerException"/>.
/// </summary>
public class GisException : Exception
{
	public GisException(string message, string detail = null, Exception inner = null) : base(message, inner)
	{
		Detail = detail;
	}

	/// <summary>Technical detail for logs and support: the file, URL, tag or text that caused the problem.</summary>
	public string Detail { get; }
}

/// <summary>
/// A coordinate system SDE can't convert: NAD27, an unknown datum, an unsupported projection or a WKT it can't read.
/// The message names the system and says to reproject the data to NAD83 or WGS 84 in QGIS first.
/// </summary>
public sealed class UnsupportedCoordinateSystemException : GisException
{
	public UnsupportedCoordinateSystemException(string systemName, string detail = null)
		: base($"This file's coordinate system ({systemName}) isn't supported. Reproject it to NAD83 or WGS 84 in QGIS first.", detail)
	{
		SystemName = systemName;
	}

	/// <summary>The short name of the coordinate system or datum that was refused, such as "NAD27".</summary>
	public string SystemName { get; }
}
