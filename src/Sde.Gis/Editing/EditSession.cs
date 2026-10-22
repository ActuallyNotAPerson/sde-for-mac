using NetTopologySuite.Geometries;
using Sde.Gis.Features;
using Sde.Gis.Formats;

namespace Sde.Gis.Editing;

/// <summary>
/// One editing session on a layer of features: shapes added, changed and deleted, and values typed, with undo and redo,
/// until the edits are saved (<see cref="ToEdits"/>, then <see cref="Saved"/>) or thrown away. The layer it started
/// from is never changed. <see cref="Working"/> holds the features as they are now; a feature is copied the first
/// time it changes, so starting a session on a large layer costs nothing. New features have negative ids until they
/// are saved.
/// </summary>
public sealed class EditSession
{
	/// <summary>Undo keeps this many steps.</summary>
	public const int MaxSteps = 200;

	private readonly List<Step> _undo = new List<Step>();
	private readonly List<Step> _redo = new List<Step>();
	private readonly HashSet<long> _touched = new HashSet<long>();
	private Dictionary<long, Feature> _originals;
	private long _nextNewId = -1;

	/// <param name="layer">The layer as it was read (or last saved). It is not changed.</param>
	public EditSession(FeatureLayer layer)
	{
		ArgumentNullException.ThrowIfNull(layer);
		Start(layer);
	}

	/// <summary>The features as they are now, in drawing order (shares the starting layer's fields).</summary>
	public FeatureLayer Working { get; private set; }

	/// <summary>The fields every feature has.</summary>
	public FieldList Fields => Working.Fields;

	/// <summary>Goes up by one with every change, undo and redo (the map redraws the edits when it changes).</summary>
	public int Revision { get; private set; }

	/// <summary>Raised after every change, undo, redo and save.</summary>
	public event Action Changed;

	public bool CanUndo => _undo.Count > 0;

	public bool CanRedo => _redo.Count > 0;

	/// <summary>What Undo would undo ("Move corner"), or null.</summary>
	public string UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;

	/// <summary>What Redo would redo, or null.</summary>
	public string RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

	/// <summary>The feature with an id as it is now, or null (deleted, or never there).</summary>
	public Feature Find(long id) => Working.Features.Find(f => f.Id == id);

	/// <summary>The feature as it was when the session started (or was last saved), or null for a new one.</summary>
	public Feature Original(long id) => _originals.TryGetValue(id, out Feature f) ? f : null;

	// ───────────── Changes ─────────────

	/// <summary>Adds a feature with a shape and no values; it goes on top of the others.</summary>
	public Feature Add(Geometry geometry, string label = null)
	{
		ArgumentNullException.ThrowIfNull(geometry);
		Feature feature = new Feature(Fields, geometry, _nextNewId--);
		Do(label ?? "Add " + Noun(geometry), new Change(null, feature, Working.Features.Count));
		return feature;
	}

	/// <summary>Deletes a feature. False when there is no such feature.</summary>
	public bool Delete(long id, string label = null)
	{
		int at = IndexOf(id);
		if (at < 0)
		{
			return false;
		}
		Feature before = Working.Features[at];
		Do(label ?? "Delete " + Noun(before.Geometry), new Change(before, null, at));
		return true;
	}

	/// <summary>Gives a feature a new shape (a corner moved, added or taken away, or the shape moved).</summary>
	public bool SetGeometry(long id, Geometry geometry, string label)
	{
		ArgumentNullException.ThrowIfNull(geometry);
		int at = IndexOf(id);
		if (at < 0)
		{
			return false;
		}
		Feature before = Working.Features[at];
		if (before.Geometry != null && before.Geometry.EqualsExact(geometry))
		{
			return false;
		}
		Feature after = before.Clone();
		after.Geometry = geometry.Copy();
		Do(label ?? "Change shape", new Change(before, after, at));
		return true;
	}

	/// <summary>
	/// Sets one value, converted to the field's type. Throws <see cref="FormatException"/> with a plain-English message
	/// when the value doesn't suit the field ("land_value needs a number, not "abc"."); nothing changes then. False when
	/// the value is the same as before or there is no such feature.
	/// </summary>
	public bool SetValue(long id, int field, object value)
	{
		if (field < 0 || field >= Fields.Count)
		{
			throw new ArgumentOutOfRangeException(nameof(field));
		}
		object converted = Fields[field].Convert(value);
		int at = IndexOf(id);
		if (at < 0)
		{
			return false;
		}
		Feature before = Working.Features[at];
		if (Equals(before[field], converted))
		{
			return false;
		}
		Feature after = before.Clone();
		after[field] = converted;
		Do("Change " + Fields[field].DisplayName, new Change(before, after, at));
		return true;
	}

	/// <summary>The same by field name.</summary>
	public bool SetValue(long id, string field, object value)
	{
		int i = Fields.IndexOf(field);
		if (i < 0)
		{
			throw new ArgumentException($"There's no field called {field}.", nameof(field));
		}
		return SetValue(id, i, value);
	}

	// ───────────── Undo and redo ─────────────

	public bool Undo()
	{
		if (_undo.Count == 0)
		{
			return false;
		}
		Step step = _undo[^1];
		_undo.RemoveAt(_undo.Count - 1);
		for (int i = step.Changes.Count - 1; i >= 0; i--)
		{
			Change c = step.Changes[i];
			if (c.Before == null)
			{
				Working.Features.RemoveAt(c.Index);
			}
			else if (c.After == null)
			{
				Working.Features.Insert(c.Index, c.Before);
			}
			else
			{
				Working.Features[c.Index] = c.Before;
			}
		}
		_redo.Add(step);
		Raise();
		return true;
	}

	public bool Redo()
	{
		if (_redo.Count == 0)
		{
			return false;
		}
		Step step = _redo[^1];
		_redo.RemoveAt(_redo.Count - 1);
		Apply(step);
		_undo.Add(step);
		Raise();
		return true;
	}

	// ───────────── What changed ─────────────

	/// <summary>The ids of features that differ from the start: added, changed or deleted (an undone change doesn't count).</summary>
	public IReadOnlyCollection<long> ChangedIds
	{
		get
		{
			HashSet<long> changed = new HashSet<long>();
			foreach (long id in _touched)
			{
				Feature now = Find(id);
				Feature then = Original(id);
				if (now == null && then == null)
				{
					continue;
				}
				if (now == null || then == null || !Same(now, then))
				{
					changed.Add(id);
				}
			}
			return changed;
		}
	}

	/// <summary>How many features differ from the start.</summary>
	public int ChangeCount => ChangedIds.Count;

	public bool HasChanges => ChangeCount > 0;

	/// <summary>
	/// The features that differ from the start as they are now (added and changed ones, not deleted ones), in drawing
	/// order. The map draws these over the layer, whose own copies of them are hidden.
	/// </summary>
	public IReadOnlyList<Feature> ChangedFeatures()
	{
		IReadOnlyCollection<long> changed = ChangedIds;
		return Working.Features.Where(f => changed.Contains(f.Id)).ToList();
	}

	/// <summary>The ids of features from the start that have changed or gone (the map hides its own copies of them).</summary>
	public IReadOnlyCollection<long> ChangedOriginalIds() => ChangedIds.Where(_originals.ContainsKey).ToHashSet();

	/// <summary>The changes to write to the layer's table (<see cref="LayersStore.ApplyEdits"/>), then call <see cref="Saved"/>.</summary>
	public FeatureEdits ToEdits()
	{
		FeatureEdits edits = new FeatureEdits();
		IReadOnlyCollection<long> changed = ChangedIds;
		foreach (Feature f in Working.Features)
		{
			if (!changed.Contains(f.Id))
			{
				continue;
			}
			if (_originals.ContainsKey(f.Id))
			{
				edits.Updates.Add(f);
			}
			else
			{
				edits.Inserts.Add(f);
			}
		}
		foreach (long id in changed.Where(id => _originals.ContainsKey(id) && Find(id) == null).OrderBy(id => id))
		{
			edits.Deletes.Add(id);
		}
		return edits;
	}

	/// <summary>
	/// The edits were written (new features now have the ids the table gave them): the features as they are become the
	/// starting point, and undo starts again. <see cref="Working"/> is a new layer from here on, so the saved one can be
	/// shown as the layer's data.
	/// </summary>
	public FeatureLayer Saved()
	{
		FeatureLayer saved = Working;
		Start(saved);
		Raise();
		return saved;
	}

	// ───────────── Inside ─────────────

	private void Start(FeatureLayer layer)
	{
		Working = new FeatureLayer(layer.Name, layer.GeometryKind, layer.Fields) { SourceCoordinateSystem = layer.SourceCoordinateSystem };
		Working.Features.AddRange(layer.Features);
		_originals = new Dictionary<long, Feature>();
		foreach (Feature f in layer.Features)
		{
			_originals.TryAdd(f.Id, f);
		}
		_undo.Clear();
		_redo.Clear();
		_touched.Clear();
	}

	private int IndexOf(long id) => Working.Features.FindIndex(f => f.Id == id);

	private void Do(string label, params Change[] changes)
	{
		Step step = new Step(label, changes);
		Apply(step);
		_undo.Add(step);
		if (_undo.Count > MaxSteps)
		{
			_undo.RemoveAt(0);
		}
		_redo.Clear();
		Raise();
	}

	private void Apply(Step step)
	{
		foreach (Change c in step.Changes)
		{
			if (c.Before == null)
			{
				Working.Features.Insert(c.Index, c.After);
				_touched.Add(c.After.Id);
			}
			else if (c.After == null)
			{
				Working.Features.RemoveAt(c.Index);
				_touched.Add(c.Before.Id);
			}
			else
			{
				Working.Features[c.Index] = c.After;
				_touched.Add(c.After.Id);
			}
		}
	}

	private void Raise()
	{
		Revision++;
		Changed?.Invoke();
	}

	/// <summary>True when two versions of a feature have the same shape and values.</summary>
	private static bool Same(Feature a, Feature b)
	{
		if (ReferenceEquals(a, b))
		{
			return true;
		}
		bool sameShape = a.Geometry == null ? b.Geometry == null : b.Geometry != null && a.Geometry.EqualsExact(b.Geometry);
		if (!sameShape)
		{
			return false;
		}
		object[] va = a.GetValues();
		object[] vb = b.GetValues();
		return va.Length == vb.Length && va.Zip(vb).All(p => Equals(p.First, p.Second));
	}

	/// <summary>"point", "line" or "area", for the step's label.</summary>
	private static string Noun(Geometry g) => FeatureLayer.KindOf(g) switch
	{
		GeometryKind.Point => "point",
		GeometryKind.Line => "line",
		GeometryKind.Polygon => "area",
		_ => "shape",
	};

	/// <summary>One feature before and after a change (Before null: added; After null: deleted) and its place in the list.</summary>
	private sealed record Change(Feature Before, Feature After, int Index);

	/// <summary>What one undo takes back.</summary>
	private sealed record Step(string Label, IReadOnlyList<Change> Changes);
}
