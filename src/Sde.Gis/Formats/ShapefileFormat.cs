using System.Globalization;
using System.Text;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Esri;
using NetTopologySuite.IO.Esri.Dbf.Fields;
using NetTopologySuite.IO.Esri.Shapefiles.Readers;
using NetTopologySuite.IO.Esri.Shapefiles.Writers;
using Sde.Gis.Features;
using Sde.Gis.Projections;

namespace Sde.Gis.Formats;

/// <summary>
/// Reads and writes Esri Shapefiles (.shp, .shx, .dbf, .prj and .cpg) with NetTopologySuite.IO.Esri.Shapefile (BSD-3).
/// Reading converts the coordinates to WGS 84 through the .prj file; the .dbf text encoding comes from the .cpg file,
/// or the .dbf's own language byte when there is none.
/// </summary>
public static class ShapefileFormat
{
	/// <summary>
	/// Reads a Shapefile into a layer in WGS 84. Throws <see cref="GisException"/> when a part is missing or the file
	/// can't be read, and <see cref="UnsupportedCoordinateSystemException"/> when the .prj names a system SDE can't convert.
	/// </summary>
	public static FeatureLayer Read(string shpPath)
	{
		GisSetup.EnsureInitialized();
		if (!File.Exists(shpPath))
		{
			throw new GisException("The Shapefile couldn't be found.", shpPath);
		}
		string dbfPath = Sibling(shpPath, ".dbf");
		if (dbfPath == null)
		{
			throw new GisException("The .dbf file that goes with this Shapefile is missing. Copy the .shp, .shx, .dbf and .prj files together.", shpPath);
		}
		string prjPath = Sibling(shpPath, ".prj");
		CoordinateSystem cs = prjPath == null ? null : CoordinateSystem.FromWkt(File.ReadAllText(prjPath));
		Encoding encoding = EncodingFromCpg(Sibling(shpPath, ".cpg"));

		FeatureLayer layer = new FeatureLayer(Path.GetFileNameWithoutExtension(shpPath));
		try
		{
			using FileStream shp = File.OpenRead(shpPath);
			using FileStream dbf = File.OpenRead(dbfPath);
			ShapefileReaderOptions options = new ShapefileReaderOptions
			{
				Factory = new GeometryFactory(new PrecisionModel(), 0),
				Encoding = encoding,
				GeometryBuilderMode = GeometryBuilderMode.FixInvalidShapes,
			};
			using ShapefileReader reader = Shapefile.OpenRead(shp, dbf, options);
			List<(DbfField Dbf, int Index)> columns = new List<(DbfField, int)>();
			foreach (DbfField field in reader.Fields)
			{
				if (layer.Fields.Contains(field.Name))
				{
					continue;
				}
				layer.AddField(field.Name, TypeOf(field));
				columns.Add((field, layer.Fields.Count - 1));
			}
			if (cs == null)
			{
				Envelope box = reader.BoundingBox;
				bool looksGeographic = box == null || box.IsNull || (box.MinX >= -180.5 && box.MaxX <= 180.5 && box.MinY >= -90.5 && box.MaxY <= 90.5);
				if (!looksGeographic)
				{
					throw new GisException("This Shapefile has no .prj file, so SDE can't tell where it is. Copy its .prj file next to it, or set its coordinate system in QGIS first.", shpPath);
				}
				layer.Warnings.Add("This Shapefile has no .prj file. Its coordinates look like longitude and latitude, so SDE has read them as WGS 84.");
				cs = CoordinateSystem.Wgs84;
			}
			layer.SourceCoordinateSystem = cs.IsGeographic ? null : cs;
			long record = 0;
			int unreadable = 0;
			while (reader.Read(out bool deleted))
			{
				record++;
				if (deleted)
				{
					continue;
				}
				Geometry g = reader.Geometry;
				if (g != null && g.IsEmpty)
				{
					g = null;
				}
				g = g == null ? null : GisSetup.Factory.CreateGeometry(Reproject.ToWgs84(g, cs));
				Feature feature = layer.NewFeature(g, record);
				foreach ((DbfField dbfField, int index) in columns)
				{
					object raw = dbfField.Value;
					if (raw is string text)
					{
						raw = text.Trim();
					}
					if (FieldValues.TryConvert(layer.Fields[index].Type, raw, out object value))
					{
						feature.SetRaw(index, value);
					}
					else
					{
						unreadable++;
					}
				}
				layer.Features.Add(feature);
			}
			if (unreadable > 0)
			{
				layer.Warnings.Add($"{unreadable} values in the .dbf couldn't be read and are shown empty.");
			}
		}
		catch (Exception ex) when (ex is ShapefileException or IOException or FormatException or InvalidOperationException or ArgumentException or NotSupportedException)
		{
			throw new GisException("This Shapefile couldn't be read. It may be damaged, or its parts may not belong together.", $"{shpPath}: {ex.Message}", ex);
		}
		layer.DetectGeometryKind();
		return layer;
	}

	/// <summary>
	/// Writes a layer as a Shapefile (.shp, .shx, .dbf, .prj and a UTF-8 .cpg), in <paramref name="target"/>'s
	/// coordinates (WGS 84 when null). Field names are cut to the 10 characters a .dbf allows (kept unique), and
	/// features without a geometry are left out. Throws <see cref="GisException"/> for a layer of mixed kinds.
	/// </summary>
	public static void Write(string shpPath, FeatureLayer layer, CoordinateSystem target = null)
	{
		GisSetup.EnsureInitialized();
		ArgumentNullException.ThrowIfNull(layer);
		target ??= CoordinateSystem.Wgs84;
		GeometryKind kind = layer.GeometryKind != GeometryKind.Unknown ? layer.GeometryKind : layer.DetectGeometryKind();
		ShapeType shapeType = kind switch
		{
			GeometryKind.Point => layer.Features.Any(f => f.Geometry is MultiPoint) ? ShapeType.MultiPoint : ShapeType.Point,
			GeometryKind.Line => ShapeType.PolyLine,
			GeometryKind.Polygon => ShapeType.Polygon,
			_ => throw new GisException("A Shapefile holds one kind of feature (points, lines or polygons), and this layer has a mix or none.", layer.Name),
		};

		List<DbfField> dbfFields = new List<DbfField>();
		HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (LayerField field in layer.Fields)
		{
			string name = DbfName(field.Name, used);
			dbfFields.Add(field.Type switch
			{
				FieldType.Integer => new DbfNumericInt64Field(name),
				FieldType.Real => new DbfNumericDoubleField(name),
				FieldType.Boolean => new DbfLogicalField(name),
				FieldType.Date or FieldType.DateTime => new DbfDateField(name),
				_ => new DbfCharacterField(name, TextLength(layer, field)),
			});
		}

		string dir = Path.GetDirectoryName(Path.GetFullPath(shpPath));
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}
		ShapefileWriterOptions options = new ShapefileWriterOptions(shapeType, dbfFields.ToArray())
		{
			Encoding = Encoding.UTF8,
			Projection = target.ToEsriWkt(),
		};
		using (ShapefileWriter writer = Shapefile.OpenWrite(shpPath, options))
		{
			foreach (Feature feature in layer.Features)
			{
				if (feature.Geometry == null || feature.Geometry.IsEmpty)
				{
					continue;
				}
				Geometry g = Reproject.FromWgs84(feature.Geometry, target);
				if (shapeType == ShapeType.MultiPoint && g is Point p)
				{
					g = p.Factory.CreateMultiPoint(new[] { p });
				}
				writer.Geometry = g;
				for (int i = 0; i < layer.Fields.Count; i++)
				{
					object value = feature[i];
					writer.Fields[i].Value = layer.Fields[i].Type switch
					{
						FieldType.DateTime => value is DateTime d ? d.Date : null,
						FieldType.Text => value as string,
						_ => value,
					};
				}
				writer.Write();
			}
		}
		File.WriteAllText(Path.ChangeExtension(shpPath, ".cpg"), "UTF-8");
	}

	/// <summary>The encoding a .cpg file names ("UTF-8", "1252", "ANSI 1252", "ISO 88591" and so on), or null when there is no usable .cpg.</summary>
	public static Encoding EncodingFromCpg(string cpgPath)
	{
		GisSetup.EnsureInitialized();
		if (cpgPath == null || !File.Exists(cpgPath))
		{
			return null;
		}
		string text = File.ReadAllText(cpgPath).Trim().Trim('\0').Trim();
		string key = text.ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
		if (key is "UTF8" or "65001")
		{
			return new UTF8Encoding(false);
		}
		if (key.StartsWith("ANSI", StringComparison.Ordinal))
		{
			key = key.Substring(4);
		}
		if (key.StartsWith("CP", StringComparison.Ordinal))
		{
			key = key.Substring(2);
		}
		if (key.StartsWith("ISO8859", StringComparison.Ordinal) && int.TryParse(key.AsSpan(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out int part))
		{
			return Encoding.GetEncoding(28590 + part);
		}
		if (key.StartsWith("8859", StringComparison.Ordinal) && int.TryParse(key.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out int part2))
		{
			return Encoding.GetEncoding(28590 + part2);
		}
		if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int codePage))
		{
			try
			{
				return Encoding.GetEncoding(codePage);
			}
			catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
			{
				return null;
			}
		}
		try
		{
			return Encoding.GetEncoding(text);
		}
		catch (ArgumentException)
		{
			return null;
		}
	}

	/// <summary>The file beside <paramref name="shpPath"/> with the same name and the given extension in any case, or null.</summary>
	public static string Sibling(string shpPath, string extension)
	{
		string exact = Path.ChangeExtension(shpPath, extension);
		if (File.Exists(exact))
		{
			return exact;
		}
		string dir = Path.GetDirectoryName(Path.GetFullPath(shpPath));
		string stem = Path.GetFileNameWithoutExtension(shpPath);
		if (dir == null || !Directory.Exists(dir))
		{
			return null;
		}
		return Directory.EnumerateFiles(dir, stem + ".*")
			.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(Path.GetExtension(f), extension, StringComparison.OrdinalIgnoreCase));
	}

	private static FieldType TypeOf(DbfField field) => field switch
	{
		DbfCharacterField => FieldType.Text,
		DbfDateField => FieldType.Date,
		DbfLogicalField => FieldType.Boolean,
		DbfNumericInt32Field or DbfNumericInt64Field => FieldType.Integer,
		DbfNumericDoubleField or DbfFloatField or DbfNumericDecimalField => FieldType.Real,
		_ => FieldType.Text,
	};

	private static string DbfName(string name, HashSet<string> used)
	{
		StringBuilder b = new StringBuilder();
		foreach (char c in name)
		{
			b.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
		}
		string baseName = b.Length == 0 ? "FIELD" : b.ToString();
		string candidate = baseName.Length > 10 ? baseName.Substring(0, 10) : baseName;
		for (int n = 1; used.Contains(candidate); n++)
		{
			string suffix = "_" + n.ToString(CultureInfo.InvariantCulture);
			candidate = baseName.Substring(0, Math.Min(baseName.Length, 10 - suffix.Length)) + suffix;
		}
		used.Add(candidate);
		return candidate;
	}

	private static int TextLength(FeatureLayer layer, LayerField field)
	{
		int longest = 1;
		foreach (Feature f in layer.Features)
		{
			if (f[field.Name] is string s)
			{
				longest = Math.Max(longest, Encoding.UTF8.GetByteCount(s));
			}
		}
		return Math.Min(254, longest);
	}
}
