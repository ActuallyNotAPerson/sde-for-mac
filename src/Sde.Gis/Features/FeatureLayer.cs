using NetTopologySuite.Geometries;
using Sde.Gis.Projections;

namespace Sde.Gis.Features;

/// <summary>What a layer's features are: points, lines or polygons (each including the multi- forms).</summary>
public enum GeometryKind
{
	/// <summary>Not known yet, empty, or a mix of kinds.</summary>
	Unknown,
	Point,
	Line,
	Polygon,
}

/// <summary>
/// One feature: a geometry in WGS 84 longitude and latitude plus a value for each of its layer's fields. Values are
/// converted to the field's type when set (see <see cref="FieldValues"/>).
/// </summary>
public sealed class Feature
{
	private object[] _values;

	/// <summary>A feature with the given fields and no values yet.</summary>
	public Feature(FieldList fields, Geometry geometry = null, long id = 0)
	{
		Fields = fields ?? throw new ArgumentNullException(nameof(fields));
		Geometry = geometry;
		Id = id;
		_values = new object[fields.Count];
	}

	/// <summary>The feature's id in its source: the GeoPackage fid, the ArcGIS object id, the record number. 0 when it hasn't been saved.</summary>
	public long Id { get; set; }

	/// <summary>The geometry in WGS 84 longitude (x) and latitude (y); null when the feature has none.</summary>
	public Geometry Geometry { get; set; }

	/// <summary>The layer's fields (shared by every feature of the layer).</summary>
	public FieldList Fields { get; }

	/// <summary>The value at a field position (null when empty). Setting converts to the field's type and may throw <see cref="FormatException"/>.</summary>
	public object this[int index]
	{
		get => index < _values.Length ? _values[index] : null;
		set
		{
			object converted = Fields[index].Convert(value);
			if (index >= _values.Length)
			{
				Array.Resize(ref _values, Fields.Count);
			}
			_values[index] = converted;
		}
	}

	/// <summary>The value of the named field (null when empty or when there is no such field). Setting an unknown field throws.</summary>
	public object this[string field]
	{
		get
		{
			int i = Fields.IndexOf(field);
			return i < 0 ? null : this[i];
		}
		set
		{
			int i = Fields.IndexOf(field);
			if (i < 0)
			{
				throw new ArgumentException($"There's no field called {field}.", nameof(field));
			}
			this[i] = value;
		}
	}

	/// <summary>Sets a value that is already of the field's type, without converting it (for readers).</summary>
	internal void SetRaw(int index, object value)
	{
		if (index >= _values.Length)
		{
			Array.Resize(ref _values, Fields.Count);
		}
		_values[index] = value;
	}

	/// <summary>The values in field order (a copy).</summary>
	public object[] GetValues()
	{
		object[] copy = new object[Fields.Count];
		Array.Copy(_values, copy, Math.Min(_values.Length, copy.Length));
		return copy;
	}

	/// <summary>A copy of this feature (the geometry is copied too).</summary>
	public Feature Clone()
	{
		Feature f = new Feature(Fields, Geometry?.Copy(), Id);
		f._values = GetValues();
		return f;
	}

	public override string ToString() => $"Feature {Id}";
}

/// <summary>
/// A layer read from a file, a GeoPackage table or an online service: its fields, its features (geometry always in
/// WGS 84 longitude and latitude) and anything worth telling the person about how it was read.
/// </summary>
public sealed class FeatureLayer
{
	public FeatureLayer(string name, GeometryKind kind = GeometryKind.Unknown)
	{
		Name = name;
		GeometryKind = kind;
	}

	/// <summary>The layer's name (file name, table name or service layer name).</summary>
	public string Name { get; set; }

	public GeometryKind GeometryKind { get; set; }

	public FieldList Fields { get; } = new FieldList();

	public List<Feature> Features { get; } = new List<Feature>();

	/// <summary>The coordinate system the source used before it was converted to WGS 84 (null when it was already WGS 84 or NAD83 longitude and latitude).</summary>
	public CoordinateSystem SourceCoordinateSystem { get; set; }

	/// <summary>Plain-English notes about the read, such as rows skipped because they had no location.</summary>
	public List<string> Warnings { get; } = new List<string>();

	/// <summary>Adds a field. Existing features get an empty value for it.</summary>
	public LayerField AddField(string name, FieldType type, string alias = null) => Fields.Add(new LayerField(name, type, alias));

	/// <summary>A new feature with this layer's fields, not yet added to <see cref="Features"/>.</summary>
	public Feature NewFeature(Geometry geometry = null, long id = 0) => new Feature(Fields, geometry, id);

	/// <summary>Creates a feature, adds it to <see cref="Features"/> and returns it.</summary>
	public Feature AddFeature(Geometry geometry, long id = 0)
	{
		Feature f = NewFeature(geometry, id);
		Features.Add(f);
		return f;
	}

	/// <summary>The bounding box of every geometry (an empty envelope when there are none).</summary>
	public Envelope GetExtent()
	{
		Envelope e = new Envelope();
		foreach (Feature f in Features)
		{
			if (f.Geometry != null && !f.Geometry.IsEmpty)
			{
				e.ExpandToInclude(f.Geometry.EnvelopeInternal);
			}
		}
		return e;
	}

	/// <summary>Sets <see cref="GeometryKind"/> from the features: the one kind they share, or Unknown for none or a mix.</summary>
	public GeometryKind DetectGeometryKind()
	{
		GeometryKind kind = GeometryKind.Unknown;
		foreach (Feature f in Features)
		{
			GeometryKind k = KindOf(f.Geometry);
			if (k == GeometryKind.Unknown)
			{
				continue;
			}
			if (kind == GeometryKind.Unknown)
			{
				kind = k;
			}
			else if (kind != k)
			{
				kind = GeometryKind.Unknown;
				break;
			}
		}
		GeometryKind = kind;
		return kind;
	}

	/// <summary>The kind of a geometry: Point and MultiPoint are points, and so on. Null, empty and collections are Unknown.</summary>
	public static GeometryKind KindOf(Geometry geometry) => geometry switch
	{
		null => GeometryKind.Unknown,
		Point or MultiPoint => GeometryKind.Point,
		LineString or MultiLineString => GeometryKind.Line,
		Polygon or MultiPolygon => GeometryKind.Polygon,
		_ => GeometryKind.Unknown,
	};

	public override string ToString() => $"{Name} ({Features.Count} features)";
}
