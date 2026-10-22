using Sde.Gis.Features;
using Sde.Gis.Styles;

namespace Sde.Gis.Tests;

/// <summary>The engine-independent style model: single, categorised, graduated, labels, scale ranges and JSON.</summary>
public class StyleTests
{
	[Fact]
	public void Colors_ParseAndBlend()
	{
		Assert.True(Rgba.TryParse("#2F6FB3", out Rgba c));
		Assert.Equal(new Rgba(0x2F, 0x6F, 0xB3), c);
		Assert.Equal(new Rgba(0x11, 0x22, 0x33, 0x80), Rgba.Parse("11223380", default));
		Assert.Equal(new Rgba(0xFF, 0xAA, 0x00), Rgba.Parse("#fa0", default));
		Assert.False(Rgba.TryParse("blue", out _));
		Assert.Equal("#2F6FB3", c.ToHex());
		Assert.Equal("#11223380", new Rgba(0x11, 0x22, 0x33, 0x80).ToHex());
		Assert.Equal(new Rgba(128, 128, 128), Rgba.Lerp(new Rgba(0, 0, 0), new Rgba(255, 255, 255), 0.5));
		ColorRamp blues = ColorRamp.Named("blues");
		Assert.Equal("#EEF4FB", blues.At(0).ToHex());
		Assert.Equal("#0B4F8A", blues.At(1).ToHex());
		Assert.Equal("#7FB0DD", blues.At(0.5).ToHex());
		Assert.Equal(5, blues.Sample(5).Count);
	}

	[Fact]
	public void Single_DrawsEverythingTheSame()
	{
		LayerStyle style = LayerStyle.SingleSymbol(new Symbol { Fill = "#FF0000" });
		FeatureLayer parcels = Fixtures.SourceParcels();
		Assert.All(parcels.Features, f => Assert.Equal("#FF0000", style.SymbolFor(f).Fill));
		Assert.Single(style.Legend("Parcels"));
		Assert.Equal("Parcels", style.Legend("Parcels")[0].Label);
	}

	[Fact]
	public void Categorized_HasOneSymbolPerValue()
	{
		FeatureLayer parcels = Fixtures.SourceParcels();
		LayerStyle style = LayerStyle.Categorized(parcels, "flood_zone");
		Assert.Equal(StyleKind.Categorized, style.Kind);
		Assert.Equal(new[] { "A", "AE", "X" }, style.Categories.Select(c => c.Value));
		Assert.Equal(3, style.Categories.Select(c => c.Symbol.Fill).Distinct().Count());
		Feature ae = parcels.Features.First(f => (string)f["flood_zone"] == "AE");
		Assert.Equal(style.Categories[1].Symbol, style.SymbolFor(ae));

		style.Categories[2].Visible = false;
		Assert.Null(style.SymbolFor("X"));
		Assert.Null(style.SymbolFor("VE"));
		style.OtherSymbol = new Symbol { Fill = "#CCCCCC" };
		Assert.Equal("#CCCCCC", style.SymbolFor("VE").Fill);
		Assert.Equal(new[] { "A", "AE", "Other" }, style.Legend().Select(l => l.Label));

		LayerStyle years = LayerStyle.Categorized(parcels, "year_built");
		Assert.Equal("1951", years.Categories[0].Value);
		Assert.Equal("(no value)", years.Categories[^1].Label);
		Assert.Equal(years.Categories[^1].Symbol, years.SymbolFor(parcels.Features[6]));
		Assert.Throws<GisException>(() => LayerStyle.Categorized(parcels, "nope"));
	}

	[Fact]
	public void Graduated_UsesEqualIntervals()
	{
		FeatureLayer parcels = Fixtures.SourceParcels();
		LayerStyle style = LayerStyle.Graduated(parcels, "land_value", 4, ColorRamp.Named("Reds"));
		Assert.Equal(4, style.Classes.Count);
		Assert.Equal(21500.25, style.Classes[0].Min);
		Assert.Equal(65000.25, style.Classes[3].Max);
		Assert.Equal(10875.0, style.Classes[0].Max - style.Classes[0].Min, 1e-9);
		Assert.Equal("21,500 to 32,375", style.Classes[0].Label);
		Assert.Equal("#FDEEEA", style.Classes[0].Symbol.Fill);
		Assert.Equal("#A51E1E", style.Classes[3].Symbol.Fill);
		Assert.Same(style.Classes[0].Symbol, style.SymbolFor(21500.25));
		Assert.Same(style.Classes[1].Symbol, style.SymbolFor(32375.25));
		Assert.Same(style.Classes[3].Symbol, style.SymbolFor(65000.25));
		Assert.Null(style.SymbolFor(70000.0));
		Assert.Null(style.SymbolFor(null));
		Assert.All(parcels.Features, f => Assert.NotNull(style.SymbolFor(f)));
		GisException ex = Assert.Throws<GisException>(() => LayerStyle.Graduated(parcels, "street", 5));
		Assert.Equal("street doesn't hold numbers, so it can't be shown in graduated colours.", ex.Message);
		Assert.Single(LayerStyle.Graduated("v", 5, 5, 5).Classes);
	}

	[Fact]
	public void Labels_UseTheFieldAndScaleRange()
	{
		FeatureLayer parcels = Fixtures.SourceParcels();
		LabelStyle labels = new LabelStyle { Field = "parcel_id", Size = 9, MinScale = 10000 };
		Assert.Equal("CF-0001", labels.TextFor(parcels.Features[0]));
		Assert.Null(new LabelStyle { Field = "year_built" }.TextFor(parcels.Features[6]));
		Assert.True(labels.IsVisibleAt(5000));
		Assert.False(labels.IsVisibleAt(24000));
		labels.Enabled = false;
		Assert.Null(labels.TextFor(parcels.Features[0]));
	}

	[Fact]
	public void Display_OpacityAndScaleRange()
	{
		LayerDisplay display = new LayerDisplay { Opacity = 0.6, MinScale = 50000, MaxScale = 1000 };
		Assert.True(display.IsVisibleAt(24000));
		Assert.False(display.IsVisibleAt(100000));
		Assert.False(display.IsVisibleAt(500));
		Assert.True(new LayerDisplay().IsVisibleAt(1e9));
		Assert.False(new LayerDisplay { Opacity = 0 }.IsVisibleAt(1000));
		Assert.Equal(1.0, new LayerDisplay { Opacity = 3 }.ClampedOpacity);
	}

	[Fact]
	public void Styles_RoundTripThroughJson()
	{
		FeatureLayer parcels = Fixtures.SourceParcels();
		foreach (LayerStyle style in new[] { LayerStyle.SingleSymbol(new Symbol { Shape = PointShape.Diamond, PointSize = 12 }), LayerStyle.Categorized(parcels, "zone_code"), LayerStyle.Graduated(parcels, "land_value", 5, ColorRamp.Named("Flood depth")) })
		{
			string json = style.ToJson();
			Assert.DoesNotContain("null", json);
			LayerStyle back = LayerStyle.FromJson(json);
			Assert.Equal(json, back.ToJson());
			foreach (Feature f in parcels.Features)
			{
				Assert.Equal(style.SymbolFor(f)?.Fill, back.SymbolFor(f)?.Fill);
			}
		}
		Assert.Contains("\"kind\":\"graduated\"", LayerStyle.Graduated(parcels, "land_value", 3).ToJson());
		Assert.Contains("\"shape\":\"diamond\"", LayerStyle.SingleSymbol(new Symbol { Shape = PointShape.Diamond }).ToJson());

		LabelStyle labels = new LabelStyle { Field = "parcel_id", Halo = false, MaxScale = 500 };
		LabelStyle labelsBack = LabelStyle.FromJson(labels.ToJson());
		Assert.Equal("parcel_id", labelsBack.Field);
		Assert.False(labelsBack.Halo);
		Assert.Equal(500, labelsBack.MaxScale);

		Assert.Equal(StyleKind.Single, LayerStyle.FromJson("{ not json").Kind);
		Assert.Equal(StyleKind.Single, LayerStyle.FromJson(null).Kind);
		Assert.False(LabelStyle.FromJson("").Enabled);
	}
}
