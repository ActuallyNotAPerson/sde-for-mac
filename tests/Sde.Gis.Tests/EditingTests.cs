using NetTopologySuite.Geometries;
using Sde.Gis.Editing;
using Sde.Gis.Features;
using Sde.Gis.Formats;

namespace Sde.Gis.Tests;

/// <summary>The map editor's logic: corners moved, added and taken away, snapping, and an editing session with undo, redo and save.</summary>
public class EditingTests
{
	private static readonly GeometryFactory F = GisSetup.Factory;

	private static Polygon Square(double x, double y, double size) => F.CreatePolygon(new[]
	{
		new Coordinate(x, y), new Coordinate(x + size, y), new Coordinate(x + size, y + size), new Coordinate(x, y + size), new Coordinate(x, y),
	});

	private static string Wkt(Geometry g) => new NetTopologySuite.IO.WKTWriter().Write(g);

	// ───────────── Corners ─────────────

	[Fact]
	public void Vertices_LeaveOutTheClosingCorner_AndCountHolesAndParts()
	{
		Polygon withHole = F.CreatePolygon(F.CreateLinearRing(Square(0, 0, 10).ExteriorRing.Coordinates),
			new[] { F.CreateLinearRing(Square(2, 2, 2).ExteriorRing.Coordinates) });
		IReadOnlyList<(VertexRef Ref, Coordinate At)> corners = GeometryEdit.Vertices(withHole);
		Assert.Equal(8, corners.Count);
		Assert.Equal(new VertexRef(0, 1, 0), corners[4].Ref);
		Assert.Equal(new Coordinate(2, 2), corners[4].At);
		// Four edges per ring, including the one back to the first corner.
		Assert.Equal(8, GeometryEdit.Edges(withHole).Count);
		Assert.Equal((new Coordinate(0, 10), new Coordinate(0, 0)), (GeometryEdit.Edges(withHole)[3].A, GeometryEdit.Edges(withHole)[3].B));

		MultiLineString lines = F.CreateMultiLineString(new[]
		{
			F.CreateLineString(new[] { new Coordinate(0, 0), new Coordinate(1, 0) }),
			F.CreateLineString(new[] { new Coordinate(5, 5), new Coordinate(6, 5), new Coordinate(7, 6) }),
		});
		Assert.Equal(5, GeometryEdit.Vertices(lines).Count);
		Assert.Equal(3, GeometryEdit.Edges(lines).Count);
		Assert.Empty(GeometryEdit.Edges(F.CreatePoint(new Coordinate(1, 1))));
		Assert.Single(GeometryEdit.Vertices(F.CreatePoint(new Coordinate(1, 1))));
	}

	[Fact]
	public void MovingTheFirstCorner_MovesTheClosingCornerToo_AndLeavesTheOriginalAlone()
	{
		Polygon square = Square(0, 0, 10);
		Geometry moved = GeometryEdit.MoveVertex(square, new VertexRef(0, 0, 0), new Coordinate(-2, -3));
		Assert.Equal("POLYGON ((-2 -3, 10 0, 10 10, 0 10, -2 -3))", Wkt(moved));
		Assert.True(moved.IsValid);
		Assert.Equal("POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0))", Wkt(square));
		Assert.Equal(4326, moved.SRID);
	}

	[Fact]
	public void InsertingACorner_PutsItOnTheEdgeAfterTheGivenCorner()
	{
		Geometry polygon = GeometryEdit.InsertVertex(Square(0, 0, 10), new VertexRef(0, 0, 3), new Coordinate(-1, 5));
		Assert.Equal("POLYGON ((0 0, 10 0, 10 10, 0 10, -1 5, 0 0))", Wkt(polygon));
		Geometry line = GeometryEdit.InsertVertex(F.CreateLineString(new[] { new Coordinate(0, 0), new Coordinate(10, 0) }), new VertexRef(0, 0, 0), new Coordinate(5, 1));
		Assert.Equal("LINESTRING (0 0, 5 1, 10 0)", Wkt(line));
		Assert.Throws<ArgumentOutOfRangeException>(() => GeometryEdit.InsertVertex(F.CreatePoint(new Coordinate(0, 0)), new VertexRef(0, 0, 0), new Coordinate(1, 1)));

		// Where the person pressed on an edge.
		(VertexRef from, Coordinate at, double d) = GeometryEdit.NearestEdge(Square(0, 0, 10), new Coordinate(4, -1)).Value;
		Assert.Equal(new VertexRef(0, 0, 0), from);
		Assert.Equal(new Coordinate(4, 0), at);
		Assert.Equal(1, d, 9);
	}

	[Fact]
	public void DeletingACorner_KeepsAtLeastATriangleOrATwoPointLine()
	{
		Geometry triangle = GeometryEdit.DeleteVertex(Square(0, 0, 10), new VertexRef(0, 0, 2));
		Assert.Equal("POLYGON ((0 0, 10 0, 0 10, 0 0))", Wkt(triangle));
		Assert.False(GeometryEdit.CanDeleteVertex(triangle, new VertexRef(0, 0, 1)));
		Assert.Null(GeometryEdit.DeleteVertex(triangle, new VertexRef(0, 0, 1)));

		// Deleting the first corner of a ring closes it on the next one.
		Assert.Equal("POLYGON ((10 0, 10 10, 0 10, 10 0))", Wkt(GeometryEdit.DeleteVertex(Square(0, 0, 10), new VertexRef(0, 0, 0))));

		LineString line = F.CreateLineString(new[] { new Coordinate(0, 0), new Coordinate(5, 5), new Coordinate(10, 0) });
		Geometry shorter = GeometryEdit.DeleteVertex(line, new VertexRef(0, 0, 1));
		Assert.Equal("LINESTRING (0 0, 10 0)", Wkt(shorter));
		Assert.Null(GeometryEdit.DeleteVertex(shorter, new VertexRef(0, 0, 0)));
		Assert.False(GeometryEdit.CanDeleteVertex(F.CreatePoint(new Coordinate(0, 0)), new VertexRef(0, 0, 0)));
		Assert.False(GeometryEdit.CanDeleteVertex(line, new VertexRef(0, 0, 9)));
	}

	[Fact]
	public void MultiPartShapes_StayMultiPart()
	{
		MultiPolygon two = F.CreateMultiPolygon(new[] { Square(0, 0, 1), Square(5, 5, 1) });
		Geometry moved = GeometryEdit.MoveVertex(two, new VertexRef(1, 0, 2), new Coordinate(7, 7));
		MultiPolygon result = Assert.IsType<MultiPolygon>(moved);
		Assert.Equal("POLYGON ((5 5, 6 5, 7 7, 5 6, 5 5))", Wkt(result.GetGeometryN(1)));
		Assert.Equal(Wkt(two.GetGeometryN(0)), Wkt(result.GetGeometryN(0)));
		MultiPoint points = F.CreateMultiPointFromCoords(new[] { new Coordinate(0, 0), new Coordinate(1, 1) });
		Assert.Equal("MULTIPOINT ((0 0), (3 4))", Wkt(GeometryEdit.MoveVertex(points, new VertexRef(1, 0, 0), new Coordinate(3, 4))));
	}

	[Fact]
	public void Translate_MovesEveryCorner()
	{
		Assert.Equal("POLYGON ((2 -1, 12 -1, 12 9, 2 9, 2 -1))", Wkt(GeometryEdit.Translate(Square(0, 0, 10), 2, -1)));
		Assert.Equal("POINT (3 4)", Wkt(GeometryEdit.Translate(F.CreatePoint(new Coordinate(1, 1)), 2, 3)));
	}

	// ───────────── Snapping ─────────────

	[Fact]
	public void Snap_PrefersACornerOverANearerEdge_AndStaysPutWhenNothingIsNear()
	{
		Polygon parcel = Square(0, 0, 100);
		// 3 from the corner (100, 100) and 1 from the top edge: the corner wins.
		SnapResult toCorner = Snapping.Snap(new Coordinate(97, 101), new[] { parcel }, 5);
		Assert.Equal(SnapKind.Vertex, toCorner.Kind);
		Assert.Equal(new Coordinate(100, 100), toCorner.At);
		Assert.Equal(0, toCorner.Candidate);
		Assert.Equal(new VertexRef(0, 0, 2), toCorner.Vertex);

		// Only the edge is within reach: onto the edge, straight across.
		SnapResult toEdge = Snapping.Snap(new Coordinate(50, 103), new[] { parcel }, 5);
		Assert.Equal(SnapKind.Edge, toEdge.Kind);
		Assert.Equal(new Coordinate(50, 100), toEdge.At);

		SnapResult none = Snapping.Snap(new Coordinate(50, 120), new[] { parcel }, 5);
		Assert.False(none.Snapped);
		Assert.Equal(new Coordinate(50, 120), none.At);

		// The nearest corner of several shapes, and points count as corners.
		Point well = F.CreatePoint(new Coordinate(103, 98));
		SnapResult nearest = Snapping.Snap(new Coordinate(102, 98.5), new Geometry[] { parcel, well }, 5);
		Assert.Equal(new Coordinate(103, 98), nearest.At);
		Assert.Equal(1, nearest.Candidate);
		Assert.False(Snapping.Snap(new Coordinate(1, 1), new[] { parcel }, 0).Snapped);
	}

	// ───────────── An editing session ─────────────

	private static FeatureLayer Parcels()
	{
		FeatureLayer layer = new FeatureLayer("parcels", GeometryKind.Polygon);
		layer.AddField("parcel_id", FieldType.Text);
		layer.AddField("land_value", FieldType.Real);
		for (int i = 0; i < 3; i++)
		{
			Feature f = layer.AddFeature(Square(-84.05 + i * 0.001, 39.62, 0.0008), i + 1);
			f["parcel_id"] = "P-" + (i + 1);
			f["land_value"] = 1000.0 * (i + 1);
		}
		return layer;
	}

	[Fact]
	public void Session_AddsChangesAndDeletes_WithoutTouchingTheLayerItStartedFrom()
	{
		FeatureLayer parcels = Parcels();
		string before = Wkt(parcels.Features[0].Geometry);
		EditSession session = new EditSession(parcels);
		Assert.False(session.HasChanges);
		Assert.False(session.CanUndo);

		Assert.True(session.SetGeometry(1, GeometryEdit.MoveVertex(session.Find(1).Geometry, new VertexRef(0, 0, 0), new Coordinate(-84.051, 39.619)), "Move corner"));
		Assert.True(session.SetValue(2, "land_value", "2,500"));
		Assert.True(session.Delete(3));
		Feature added = session.Add(Square(-84.04, 39.62, 0.0005));
		Assert.True(added.Id < 0);

		Assert.Equal(4, session.ChangeCount);
		Assert.Equal(new long[] { 1, 2, added.Id }, session.ChangedFeatures().Select(f => f.Id));
		Assert.Equal(new long[] { 1, 2, 3 }, session.ChangedOriginalIds().OrderBy(i => i));
		Assert.Equal(2500.0, session.Find(2)["land_value"]);
		Assert.Null(session.Find(3));
		Assert.Equal(3, session.Working.Features.Count);
		// The layer the session started from is as it was.
		Assert.Equal(before, Wkt(parcels.Features[0].Geometry));
		Assert.Equal(2000.0, parcels.Features[1]["land_value"]);
		Assert.Equal(3, parcels.Features.Count);
		Assert.Same(parcels.Fields, session.Fields);

		FeatureEdits edits = session.ToEdits();
		Assert.Equal(new long[] { 1, 2 }, edits.Updates.Select(f => f.Id));
		Assert.Equal(new[] { added }, edits.Inserts);
		Assert.Equal(new long[] { 3 }, edits.Deletes);
	}

	[Fact]
	public void UndoAndRedo_StepBackAndForward_AndANewChangeClearsRedo()
	{
		EditSession session = new EditSession(Parcels());
		int changed = 0;
		session.Changed += () => changed++;
		Feature added = session.Add(F.CreatePolygon(Square(0, 0, 1).Coordinates));
		session.SetValue(added.Id, "parcel_id", "NEW");
		session.Delete(2);
		Assert.Equal(3, changed);
		Assert.Equal("Delete area", session.UndoLabel);

		Assert.True(session.Undo());
		Assert.NotNull(session.Find(2));
		Assert.Equal(1, session.Working.Features.IndexOf(session.Find(2)));
		Assert.Equal("Delete area", session.RedoLabel);
		Assert.Equal("Change parcel_id", session.UndoLabel);
		Assert.True(session.Undo());
		Assert.Null(session.Find(added.Id)["parcel_id"]);
		Assert.True(session.Undo());
		Assert.Null(session.Find(added.Id));
		Assert.False(session.Undo());
		Assert.False(session.HasChanges);
		Assert.Empty(session.ToEdits().Inserts);

		Assert.True(session.Redo());
		Assert.True(session.Redo());
		Assert.Equal("NEW", session.Find(added.Id)["parcel_id"]);
		Assert.True(session.CanRedo);
		session.SetValue(1, "parcel_id", "P-1A");
		Assert.False(session.CanRedo);
		Assert.Equal(2, session.ChangeCount);
		Assert.Equal(9, changed);
	}

	[Fact]
	public void Values_AreCheckedAgainstTheFieldsType()
	{
		EditSession session = new EditSession(Parcels());
		FormatException wrong = Assert.Throws<FormatException>(() => session.SetValue(1, "land_value", "lots"));
		Assert.Equal("land_value needs a number, not \"lots\".", wrong.Message);
		Assert.False(session.HasChanges);
		// The same value again changes nothing; blank clears it.
		Assert.False(session.SetValue(1, "land_value", "1000"));
		Assert.True(session.SetValue(1, "land_value", ""));
		Assert.Null(session.Find(1)["land_value"]);
		Assert.False(session.SetGeometry(1, session.Find(1).Geometry.Copy(), "Move shape"));
		Assert.Equal(1, session.ChangeCount);
	}

	[Fact]
	public void Saving_WritesTheEditsToTheLayersFile_AndStartsAgainFromThere()
	{
		using TempFolder temp = new TempFolder();
		LayersStore store = LayersStore.Open(temp.Path("x.layers.gpkg"), Guid.NewGuid());
		store.WriteTable("parcels", Parcels());
		FeatureLayer read = store.ReadTable("parcels");
		EditSession session = new EditSession(read);
		long first = read.Features[0].Id;
		session.SetGeometry(first, GeometryEdit.Translate(session.Find(first).Geometry, 0.0001, 0), "Move shape");
		session.SetValue(read.Features[1].Id, "parcel_id", "P-2B");
		session.Delete(read.Features[2].Id);
		Feature added = session.Add(Square(-84.04, 39.62, 0.0005));
		session.SetValue(added.Id, "parcel_id", "P-NEW");

		FeatureEditResult result = store.ApplyEdits("parcels", session.ToEdits());
		Assert.Equal(2, result.Updated);
		Assert.Equal(1, result.Deleted);
		long newId = Assert.Single(result.InsertedIds);
		FeatureLayer saved = session.Saved();
		// The new feature (its copy with the value typed) now has the id the table gave it.
		Assert.Equal(newId, saved.Features.Single(f => (string)f["parcel_id"] == "P-NEW").Id);
		Assert.False(session.HasChanges);
		Assert.False(session.CanUndo);
		Assert.NotSame(saved, session.Working);

		FeatureLayer back = store.ReadTable("parcels");
		Assert.Equal(3, back.Features.Count);
		Assert.Equal("P-2B", back.Features.Single(f => f.Id == read.Features[1].Id)["parcel_id"]);
		Assert.Equal("P-NEW", back.Features.Single(f => f.Id == newId)["parcel_id"]);
		Assert.Equal(-84.0499, back.Features.Single(f => f.Id == first).Geometry.EnvelopeInternal.MinX, 9);

		// Editing goes on from the saved state without changing what was saved.
		session.SetValue(newId, "parcel_id", "P-NEWER");
		Assert.Equal("P-NEW", saved.Features.Single(f => f.Id == newId)["parcel_id"]);
		Assert.Equal(new[] { newId }, session.ChangedIds);
	}
}
