using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Sde.App.Maps;
using Sde.App.ViewModels;
using Sde.App.Views;
using AColor = Avalonia.Media.Color;

namespace Sde.App.Tests;

/// <summary>
/// The Map page on the map engine (Mapsui): our own clicks and keyboard, tiles through our cache, the theme's colours,
/// one attribution line, labels in the embedded Inter, and the engine stopped when the page closes.
/// </summary>
public class MapEngineTests
{
	private static void Click(UiSession ui, MapView view, Point onMap, RawInputModifiers modifiers = RawInputModifiers.None)
	{
		Point p = MapTestSupport.InWindow(ui, view, onMap);
		ui.Window.MouseDown(p, MouseButton.Left, modifiers);
		ui.Window.MouseUp(p, MouseButton.Left, modifiers);
		ui.Pump();
	}

	[AvaloniaFact]
	public void TwoQuickClicks_OnTwoDots_AreBothSeen_AndADotIsHitFrom11PixelsAway()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		MapView view = MapTestSupport.ShowMap(ui);
		ui.Main.Map.SetModeCommand.Execute(MapMode.SetLocation);
		ui.Pump();
		List<Guid> clicked = new List<Guid>();
		view.Map.MarkerClicked += (id, add) => clicked.Add(id);
		MapPointViewModel[] dots = ui.Main.Map.Visible.ToArray();
		Assert.Equal(2, dots.Length);

		// Straight after each other (the engine itself drops a second tap within 300 ms).
		Click(ui, view, view.Map.ToScreen(dots[0].Latitude, dots[0].Longitude));
		Click(ui, view, view.Map.ToScreen(dots[1].Latitude, dots[1].Longitude));
		Assert.Equal(new[] { dots[0].Id, dots[1].Id }, clicked);
		Assert.Same(dots[1], ui.Main.Map.PlaceTarget);

		// 9 px off the dot's centre still hits (the dot itself is 6.5 px across); 14 px off is empty map.
		view.Map.SetView(dots[0].Latitude, dots[0].Longitude, 16);
		ui.Pump();
		Point centre = view.Map.ToScreen(dots[0].Latitude, dots[0].Longitude);
		Assert.Equal(dots[0].Id, view.Map.MarkerAt(centre + new Point(9, 0)));
		Assert.Equal(dots[0].Id, view.Map.MarkerAt(centre + new Point(0, -10.5)));
		Assert.Null(view.Map.MarkerAt(centre + new Point(14, 0)));
	}

	[AvaloniaFact]
	public void ArrowKeysPan_AndEqualsAndMinusZoom_WhenTheMapHasFocus()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		MapView view = MapTestSupport.ShowMap(ui);
		view.Map.SetView(39.1, -84.5, 14);
		view.Map.Focus();
		ui.Pump();
		Assert.True(view.Map.IsFocused);

		Point before = view.Map.ToScreen(39.1, -84.5);
		ui.Window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
		ui.Pump();
		Point after = view.Map.ToScreen(39.1, -84.5);
		Assert.Equal(before.X + 80, after.X, 3);
		Assert.Equal(before.Y, after.Y, 3);
		ui.Window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
		ui.Pump();
		Assert.Equal(after.Y + 80, view.Map.ToScreen(39.1, -84.5).Y, 3);

		ui.Window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.None);
		ui.Pump();
		Assert.Equal(15, view.Map.Zoom, 6);
		ui.Window.KeyPressQwerty(PhysicalKey.Minus, RawInputModifiers.None);
		ui.Window.KeyPressQwerty(PhysicalKey.Minus, RawInputModifiers.None);
		ui.Pump();
		Assert.Equal(13, view.Map.Zoom, 6);
	}

	[AvaloniaFact]
	public void ADragWithCommand_DrawsABox_AndTheMapDoesNotMove_WhileAPlainDragMovesIt()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		MapView view = MapTestSupport.ShowMap(ui);
		ui.Main.Map.Visible.First();
		Point a = view.Map.ToScreen(39.1031, -84.5120);
		Point b = view.Map.ToScreen(39.1100, -84.5000);
		double lat = view.Map.CenterLatitude;
		double lon = view.Map.CenterLongitude;
		Point from = MapTestSupport.InWindow(ui, view, new Point(Math.Min(a.X, b.X) - 20, Math.Min(a.Y, b.Y) - 20));
		Point to = MapTestSupport.InWindow(ui, view, new Point(Math.Max(a.X, b.X) + 20, Math.Max(a.Y, b.Y) + 20));
		ui.Window.MouseDown(from, MouseButton.Left, RawInputModifiers.Meta);
		for (int i = 1; i <= 8; i++)
		{
			ui.Window.MouseMove(from + (to - from) * (i / 8.0), RawInputModifiers.Meta | RawInputModifiers.LeftMouseButton);
		}
		ui.Window.MouseUp(to, MouseButton.Left, RawInputModifiers.Meta);
		ui.Pump();
		Assert.Equal(2, ui.Main.Structures.CheckedCount);
		Assert.Equal(lat, view.Map.CenterLatitude, 9);
		Assert.Equal(lon, view.Map.CenterLongitude, 9);

		Point start = MapTestSupport.InWindow(ui, view, new Point(200, 200));
		ui.Window.MouseDown(start, MouseButton.Left);
		for (int i = 1; i <= 8; i++)
		{
			ui.Window.MouseMove(start + new Point(i * 10, i * 5), RawInputModifiers.LeftMouseButton);
		}
		ui.Window.MouseUp(start + new Point(80, 40), MouseButton.Left);
		ui.Pump();
		Assert.True(view.Map.CenterLongitude < lon, "a plain drag to the right moves the map west");
	}

	[AvaloniaFact]
	public void OneView_AsksForFewTiles_ThroughOurCache_AndTheyComeFromDiskNextTime()
	{
		FakeTiles.Requested.Clear();
		using UiSession ui = new UiSession(width: 1100, height: 760);
		MapTestSupport.SetUpPlaces(ui, Basemaps.UsgsTopo);
		MapView view = MapTestSupport.ShowMap(ui);
		MapTestSupport.Settle(ui, view);
		// A view of its own, away from the one the page opened on.
		lock (FakeTiles.Requested)
		{
			FakeTiles.Requested.Clear();
		}
		int downloadsBefore = ui.Main.Map.Tiles.Downloads;
		view.Map.SetView(39.30, -84.20, 15);
		MapTestSupport.Settle(ui, view);

		List<string> asked;
		lock (FakeTiles.Requested)
		{
			// Only this view's (another test's map may still be finishing its downloads elsewhere).
			asked = FakeTiles.Requested.Where(u => NearView(u, 39.30, -84.20)).ToList();
		}
		// Exactly the tiles that cover the view at its zoom level, plus those covering it one level up (shown while the
		// sharp ones load), and no more. The engine's default asked for 35 tiles for one 800 x 600 view.
		int Covering(int z)
		{
			MapExtent e = view.Map.Extent;
			int n = 1 << z;
			int x0 = (int)Math.Floor(WebMercator.U(e.MinLongitude) * n), x1 = (int)Math.Floor(WebMercator.U(e.MaxLongitude) * n);
			int y0 = (int)Math.Floor(WebMercator.V(e.MaxLatitude) * n), y1 = (int)Math.Floor(WebMercator.V(e.MinLatitude) * n);
			return (x1 - x0 + 1) * (y1 - y0 + 1);
		}
		Assert.All(asked, u => Assert.StartsWith("https://basemap.nationalmap.gov/arcgis/rest/services/USGSTopo/MapServer/tile/", u));
		Assert.Equal(Covering(15), asked.Count(u => u.Contains("/tile/15/")));
		Assert.Equal(Covering(14), asked.Count(u => u.Contains("/tile/14/")));
		Assert.Equal(Covering(15) + Covering(14), asked.Count);
		Assert.Equal(asked.Count, ui.Main.Map.Tiles.Downloads - downloadsBefore);
		Assert.All(asked, u => Assert.Contains("/USGSTopo/", u));
		Assert.Equal(asked.Count, asked.Distinct().Count());

		// The paper colour of the fake tiles is drawn where there are no dots.
		WriteableBitmap frame = MapTestSupport.Capture(ui);
		Point empty = MapTestSupport.InWindow(ui, view, new Point(30, 30));
		AColor ground = MapTestSupport.At(frame, empty.X, empty.Y);
		Assert.True(MapTestSupport.Near(ground, AColor.FromRgb(236, 232, 220)) || MapTestSupport.Near(ground, AColor.FromRgb(214, 208, 192)), $"{ground}");

		// The page closes and opens again: the tiles come from the folder on disk, not the network.
		ui.Main.ShowCommand.Execute(Page.Structures);
		ui.Pump();
		view = MapTestSupport.ShowMap(ui);
		MapTestSupport.Settle(ui, view);
		int downloads = ui.Main.Map.Tiles.Downloads;
		int disk = ui.Main.Map.Tiles.DiskReads;
		view.Map.SetView(39.30, -84.20, 15);
		MapTestSupport.Settle(ui, view);
		Assert.Equal(downloads, ui.Main.Map.Tiles.Downloads);
		Assert.True(ui.Main.Map.Tiles.DiskReads >= disk + asked.Count);
	}

	/// <summary>True when a {z}/{y}/{x} tile address is within about 15 km of a place.</summary>
	private static bool NearView(string url, double latitude, double longitude)
	{
		string[] parts = url.Split('/');
		if (parts.Length < 3 || !int.TryParse(parts[^3], out int z) || !int.TryParse(parts[^2], out int y) || !int.TryParse(parts[^1], out int x))
		{
			return false;
		}
		double n = 1 << z;
		double lat = WebMercator.Latitude((y + 0.5) / n);
		double lon = WebMercator.Longitude((x + 0.5) / n);
		return Math.Abs(lat - latitude) < 0.15 && Math.Abs(lon - longitude) < 0.15;
	}

	[Fact]
	public void FloodZoneTiles_AskForEachTilesBox_InWebMercator_AndDoubleSizeOnRetina()
	{
		Basemap nfhl = TileOverlays.FloodZones;
		Assert.True(nfhl.HasRetinaTiles);
		Assert.False(Basemaps.UsgsTopo.HasRetinaTiles);
		Assert.Equal("-20037508.343,-20037508.343,20037508.343,20037508.343", Basemap.Bbox(0, 0, 0));
		Assert.Equal("-20037508.343,0,0,20037508.343", Basemap.Bbox(1, 0, 0));
		string url = nfhl.TileUrl(1, 1, 1);
		Assert.Equal("https://hazards.fema.gov/arcgis/rest/services/public/NFHL/MapServer/export?bbox=0,-20037508.343,20037508.343,0&bboxSR=3857&imageSR=3857&size=256,256&dpi=96&format=png32&transparent=true&f=image&layers=show:28", url);
		string retina = nfhl.TileUrl(1, 1, 1, scale: 2);
		Assert.Contains("&size=512,512&dpi=192&", retina);
		// A {z}/{x}/{y} basemap has no sharper tiles to ask for.
		Assert.Equal(Basemaps.UsgsTopo.TileUrl(3, 2, 1), Basemaps.UsgsTopo.TileUrl(3, 2, 1, scale: 2));
		Assert.Equal("https://basemap.nationalmap.gov/arcgis/rest/services/USGSTopo/MapServer/tile/3/1/2", Basemaps.UsgsTopo.TileUrl(3, 2, 1));
	}

	[AvaloniaFact]
	public void TheDots_AreInTheThemesColours_AndChangeWithTheTheme()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		MapView view = MapTestSupport.ShowMap(ui);
		MapPointViewModel dot = ui.Main.Map.Visible.First();
		ui.Main.Map.Selected = dot;
		view.Map.SetView(dot.Latitude, dot.Longitude, 16);
		ui.Pump();

		foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
		{
			Application.Current!.RequestedThemeVariant = theme;
			ui.Pump();
			WriteableBitmap frame = MapTestSupport.Capture(ui);
			Point centre = MapTestSupport.InWindow(ui, view, view.Map.ToScreen(dot.Latitude, dot.Longitude));
			AColor fill = MapTestSupport.At(frame, centre.X, centre.Y);
			Assert.True(MapTestSupport.Near(fill, MapTestSupport.ThemeColour("Status" + dot.Class + "Fg")), $"{theme}: dot {fill}");
			// The ring around the structure picked in the list is the accent colour.
			AColor ring = MapTestSupport.At(frame, centre.X + 12.5, centre.Y);
			Assert.True(MapTestSupport.Near(ring, MapTestSupport.ThemeColour("Accent"), 40), $"{theme}: ring {ring}");
			// Bare map is the theme's subtle background.
			Point bare = MapTestSupport.InWindow(ui, view, new Point(20, 20));
			Assert.True(MapTestSupport.Near(MapTestSupport.At(frame, bare.X, bare.Y), MapTestSupport.ThemeColour("SubtleBg"), 3), $"{theme}: background");
		}
		Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
	}

	[AvaloniaFact]
	public void TheEngine_HasNoDebugOverlaysOrAttributionWidgets_AndStopsWhenThePageCloses()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui, Basemaps.UsgsTopo);
		MapView view = MapTestSupport.ShowMap(ui);
		MapCanvas map = view.Map;
		Assert.Empty(map.EngineMap.Widgets);
		Assert.All(map.EngineMap.Layers, l => Assert.False(l.Attribution?.Enabled ?? false, l.Name));
		Assert.Equal("Map data: U.S. Geological Survey, The National Map", ui.Main.Map.Attribution);

		ui.Main.ShowCommand.Execute(Page.Dashboard);
		ui.Pump();
		Assert.True(map.IsDisposed);
		Assert.Null(map.EngineMap);
		// The engine's redraw loop was unhooked first, so it can't reach for the UI thread after the page has gone.
		Assert.True(map.RedrawLoopUnhooked);

		MapView again = MapTestSupport.ShowMap(ui);
		Assert.NotSame(map, again.Map);
		Assert.False(again.Map.IsDisposed);
		Assert.Equal(2, again.Map.Markers.Count);
	}

	[AvaloniaFact]
	public void Labels_UseTheEmbeddedInter_AndOnlyShowZoomedIn()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui);
		ui.Main.Map.ShowAddressLabels = true;
		MapView view = MapTestSupport.ShowMap(ui);
		Assert.Equal("Inter", MapLabelRenderer.Typeface.FamilyName);
		MapPointViewModel dot = ui.Main.Map.Visible.First();
		Assert.Contains(view.Map.Markers, m => m.Id == dot.Id && m.Label == dot.Address);

		int Ink(double zoom)
		{
			view.Map.SetView(dot.Latitude, dot.Longitude, zoom);
			ui.Pump();
			WriteableBitmap frame = MapTestSupport.Capture(ui);
			Point p = MapTestSupport.InWindow(ui, view, view.Map.ToScreen(dot.Latitude, dot.Longitude));
			AColor text = MapTestSupport.ThemeColour("TextPrimary");
			int dark = 0;
			for (double y = p.Y - 26; y < p.Y - 8; y++)
			{
				for (double x = p.X - 40; x < p.X + 40; x++)
				{
					dark += MapTestSupport.Near(MapTestSupport.At(frame, x, y), text, 40) ? 1 : 0;
				}
			}
			return dark;
		}
		Assert.True(Ink(17) > 20, "no address label at zoom 17");
		Assert.Equal(0, Ink(13));
	}

	[AvaloniaFact]
	public void Labels_InTwoMapsOrPrints_DontTakeEachOthersPlace()
	{
		// Every map and every print has its own engine renderer, each counting frames from 0, but the label renderer is
		// shared: two maps' first frames (or two worksheet maps in a row) must not see each other's labels as taken.
		MapLabelStyle style = new MapLabelStyle { Text = _ => "100 River Street" };
		Mapsui.Layers.MemoryLayer layer = new Mapsui.Layers.MemoryLayer();
		Mapsui.Viewport viewport = new Mapsui.Viewport(0, 0, 1, 0, 400, 300);
		using Mapsui.Rendering.RenderService service = new Mapsui.Rendering.RenderService();
		using SkiaSharp.SKSurface first = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(400, 300));
		using SkiaSharp.SKSurface second = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(400, 300));
		SkiaSharp.SKCanvas one = first.Canvas;
		SkiaSharp.SKCanvas two = second.Canvas;
		MapLabelRenderer labels = new MapLabelRenderer();

		Assert.True(labels.Draw(one, viewport, layer, new Mapsui.Layers.PointFeature(0, 0), style, service, 0));
		// In the same frame a label that would overlap gives way.
		Assert.False(labels.Draw(one, viewport, layer, new Mapsui.Layers.PointFeature(5, 0), style, service, 0));
		// Another map's frame 0 draws its label in the same place.
		Assert.True(labels.Draw(two, viewport, layer, new Mapsui.Layers.PointFeature(0, 0), style, service, 0));
		// And the first map's next frame starts empty.
		Assert.True(labels.Draw(one, viewport, layer, new Mapsui.Layers.PointFeature(5, 0), style, service, 1));
	}
}
