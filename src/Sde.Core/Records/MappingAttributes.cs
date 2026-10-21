namespace Sde.Core.Records;

/// <summary>The database table a record maps to (same names as FEMA's Access database).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class TableAttribute(string name) : Attribute
{
	public string Name { get; } = name;
}

/// <summary>Primary-key column(s).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class KeyAttribute : Attribute;

/// <summary>Jet AutoNumber: the database assigns the value on insert.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AutoIncrementAttribute : Attribute;

/// <summary>Not a database column (computed convenience property).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NotMappedAttribute : Attribute;

/// <summary>
/// The column was Jet Currency (scale 4) or Numeric(p, s). Jet rounded values to this many decimals on
/// write and OleDb handed them back with exactly this scale (e.g. Currency 1234.5 → 1234.5000), which is
/// how they appeared in SDE's Export.json. The data layer reproduces both behaviours.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class JetScaleAttribute(int scale) : Attribute
{
	public int Scale { get; } = scale;

	public const int Currency = 4;
}
