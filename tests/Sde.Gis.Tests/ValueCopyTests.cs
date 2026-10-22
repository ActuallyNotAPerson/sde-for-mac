using NetTopologySuite.Geometries;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Gis.Analysis;
using Sde.Gis.Features;

namespace Sde.Gis.Tests;

/// <summary>Copying a polygon layer's values onto structures: the match for each structure and the value FieldInput gets.</summary>
public class ValueCopyTests
{
	private static readonly GeometryFactory F = GisSetup.Factory;

	private static Polygon Square(double x, double y, double size) => F.CreatePolygon(new[]
	{
		new Coordinate(x, y), new Coordinate(x + size, y), new Coordinate(x + size, y + size), new Coordinate(x, y + size), new Coordinate(x, y),
	});

	private static FeatureLayer Zones()
	{
		FeatureLayer zones = new FeatureLayer("zones", GeometryKind.Polygon);
		zones.AddField("FLD_ZONE", FieldType.Text);
		zones.AddField("DFIRM_ID", FieldType.Text);
		zones.AddField("STATIC_BFE", FieldType.Real);
		Feature ae = zones.AddFeature(Square(0, 0, 10), 1);
		ae["FLD_ZONE"] = "AE";
		ae["DFIRM_ID"] = "39113C";
		ae["STATIC_BFE"] = 742.5;
		Feature a12 = zones.AddFeature(Square(10, 0, 10), 2);
		a12["FLD_ZONE"] = "A12";
		Feature x = zones.AddFeature(Square(2, 2, 2), 3);
		x["FLD_ZONE"] = "X";
		x["STATIC_BFE"] = -9999.0;
		Feature odd = zones.AddFeature(Square(20, 0, 10), 4);
		odd["FLD_ZONE"] = "AREA NOT INCLUDED";
		return zones;
	}

	private static List<Feature> Points(params (double X, double Y)[] at)
	{
		FeatureLayer layer = new FeatureLayer("structures", GeometryKind.Point);
		long id = 0;
		return at.Select(p => layer.AddFeature(F.CreatePoint(new Coordinate(p.X, p.Y)), ++id)).ToList();
	}

	[Fact]
	public void FirmZones_AreMatchedToFemasChoices_AndEveryOutcomeIsSaid()
	{
		using TempDatabase db = new TempDatabase();
		ReferenceData reference = db.Store.Reference;
		FieldInfo firm = FieldCatalog.Get("Property.FIRMZone");
		Assert.Contains(firm, ValueCopy.TargetFields);
		List<ValueCopyRow> rows = ValueCopy.Plan(Points((5, 8), (15, 5), (3, 3), (25, 5), (50, 50)), Zones().Features, "FLD_ZONE", firm, reference);

		Assert.Equal(JoinOutcome.InOne, rows[0].Outcome);
		Assert.Equal("AE", rows[0].NewText);
		Assert.Equal(FirmZones.Find("AE", reference).LookupID.ToString(), rows[0].InputValue);
		Assert.True(rows[0].CanCopy);

		Assert.Equal("A1 thru A99", rows[1].NewText);
		Assert.True(rows[1].CanCopy);

		// Inside AE and the small X polygon in it: the smallest wins.
		Assert.Equal(JoinOutcome.InSeveral, rows[2].Outcome);
		Assert.Equal("X", rows[2].NewText);

		Assert.False(rows[3].CanCopy);
		Assert.Equal("\"AREA NOT INCLUDED\" isn't a flood zone FEMA's form has a choice for.", rows[3].Problem);

		Assert.Equal(JoinOutcome.None, rows[4].Outcome);
		Assert.Equal("It isn't inside any of the areas.", rows[4].Problem);
		Assert.Null(rows[4].Polygon);
	}

	[Fact]
	public void TextAndNumbers_AreCopiedAsTheyAre_AndEmptyValuesAreSaid()
	{
		using TempDatabase db = new TempDatabase();
		FieldInfo community = FieldCatalog.Get("Property.NFIPCID");
		FieldInfo bfe = FieldCatalog.Get("Property.BFE");
		List<ValueCopyRow> text = ValueCopy.Plan(Points((5, 8), (15, 5)), Zones().Features, "DFIRM_ID", community, db.Store.Reference);
		Assert.Equal("39113C", text[0].InputValue);
		Assert.Equal("The area has no value in that field.", text[1].Problem);

		List<ValueCopyRow> numbers = ValueCopy.Plan(Points((5, 8)), Zones().Features, "STATIC_BFE", bfe, db.Store.Reference);
		Assert.Equal("742.5", numbers[0].InputValue);
		Assert.Equal("742.5", numbers[0].NewText);
		(string _, string input, string problem) = ValueCopy.Convert("about 740", bfe, db.Store.Reference);
		Assert.Null(input);
		Assert.Equal("Base flood elevation (BFE) needs a number, not \"about 740\".", problem);
		Assert.Equal("1234", ValueCopy.Convert(1234L, bfe, db.Store.Reference).Input);

		// The coordinates are never a target; parcel numbers and community names are.
		Assert.DoesNotContain(ValueCopy.TargetFields, f => f.Member is nameof(PropertyRecord.Latitude) or nameof(PropertyRecord.Longitude));
		Assert.Contains(ValueCopy.TargetFields, f => f.Key == "Property.ParcelNumber");
		Assert.Contains(ValueCopy.TargetFields, f => f.Key == "Property.NFIPCommunityName");
	}

	[Fact]
	public void TheValuesWork_ThroughFieldInput()
	{
		using TempDatabase db = new TempDatabase();
		AssessmentFile file = SampleCases.RanchFlood(db.Store);
		FieldInfo firm = FieldCatalog.Get("Property.FIRMZone");
		ValueCopyRow row = ValueCopy.Plan(Points((5, 8)), Zones().Features, "FLD_ZONE", firm, db.Store.Reference).Single();
		FieldInput.Apply(file, new[] { new FieldInputChange(firm.Key, row.InputValue) }, db.Store.Reference, new AssessmentCalculator(db.Store.Reference));
		Assert.Equal("AE", FieldCatalog.Display(firm, FieldCatalog.GetValue(file, firm), db.Store.Reference));
	}
}
