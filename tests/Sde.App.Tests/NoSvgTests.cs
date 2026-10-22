using Avalonia.Headless.XUnit;
using Sde.App.Maps;
using Sde.App.Views;

namespace Sde.App.Tests;

/// <summary>
/// The map library names Svg.Skia, but SDE leaves the Svg packages out (Svg.Custom is MS-PL, outside the licences SDE
/// ships) and never draws SVG symbols. These fail if any of them is in the app's build output or gets loaded.
/// </summary>
public class NoSvgTests
{
	private static readonly string[] SvgAssemblies = { "Svg.Skia", "Svg.Custom", "Svg.Model", "ShimSkiaSharp", "ExCSS" };

	/// <summary>src/Sde.App/bin/(the configuration these tests were built in)/net10.0.</summary>
	private static string AppOutput()
	{
		DirectoryInfo here = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
		string configuration = here.Parent!.Name;
		return Path.Combine(TestPaths.Root, "src", "Sde.App", "bin", configuration, here.Name);
	}

	[Fact]
	public void NoSvgAssembly_IsInTheAppsOutput_OrBesideTheseTests()
	{
		foreach (string folder in new[] { AppOutput(), AppContext.BaseDirectory })
		{
			Assert.True(File.Exists(Path.Combine(folder, "SDE.dll")), $"no app build in {folder}");
			Assert.True(File.Exists(Path.Combine(folder, "Mapsui.dll")), $"no map engine in {folder}");
			string[] found = Directory.GetFiles(folder, "*.dll")
				.Select(Path.GetFileNameWithoutExtension)
				.Where(n => SvgAssemblies.Contains(n, StringComparer.OrdinalIgnoreCase))
				.ToArray();
			Assert.True(found.Length == 0, $"{folder} has {string.Join(", ", found)}");
		}
		string deps = File.ReadAllText(Path.Combine(AppOutput(), "SDE.deps.json"));
		foreach (string name in SvgAssemblies)
		{
			Assert.DoesNotContain(name + ".dll", deps);
		}
	}

	[AvaloniaFact]
	public void DrawingTheMap_LoadsNoSvgAssembly()
	{
		using UiSession ui = new UiSession();
		MapTestSupport.SetUpPlaces(ui, Basemaps.UsgsTopo);
		ui.Main.Map.ShowAddressLabels = true;
		MapView view = MapTestSupport.ShowMap(ui);
		view.Map.SetView(39.1031, -84.5120, 17);
		MapTestSupport.Settle(ui, view);
		MapTestSupport.Capture(ui);

		string[] loaded = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).ToArray();
		Assert.Contains("Mapsui.Rendering.Skia", loaded);
		Assert.DoesNotContain(loaded, n => SvgAssemblies.Contains(n, StringComparer.OrdinalIgnoreCase));
	}
}
