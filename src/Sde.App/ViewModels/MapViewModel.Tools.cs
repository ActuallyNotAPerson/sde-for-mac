using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetTopologySuite.Geometries;
using Sde.App.Maps;
using Sde.App.Services;
using Sde.Data;
using Sde.Gis;
using Sde.Gis.Analysis;

namespace Sde.App.ViewModels;

/// <summary>A saved view of the map: its name and area, with Go and Remove for its row in the bookmarks list.</summary>
public sealed partial class MapBookmarkViewModel(MapBookmarkRecord record, Action<MapBookmarkViewModel> go, Action<MapBookmarkViewModel> remove) : ObservableObject
{
	public MapBookmarkRecord Record { get; } = record;

	public Guid Id => Record.BookmarkID;

	public string Name => Record.Name;

	/// <summary>The area the map showed, in longitude and latitude.</summary>
	public MapExtent Extent => new MapExtent(Record.MinX, Record.MinY, Record.MaxX, Record.MaxY);

	public string GoName => "Go to " + Name;

	public string RemoveName => "Remove the bookmark " + Name;

	[RelayCommand]
	private void Go() => go(this);

	[RelayCommand]
	private void Remove() => remove(this);
}

/// <summary>The Map page's measure tool and bookmarks.</summary>
public sealed partial class MapViewModel
{
	private static readonly CultureInfo US = CultureInfo.GetCultureInfo("en-US");

	// ───────────── Measure ─────────────

	/// <summary>False: clicks draw a line whose length is measured. True: they draw an area.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Sketch), nameof(MeasureKindIndex))]
	public partial bool MeasureArea { get; set; }

	/// <summary>The Distance | Area switch (0 or 1).</summary>
	public int MeasureKindIndex
	{
		get => MeasureArea ? 1 : 0;
		set => MeasureArea = value == 1;
	}

	partial void OnMeasureAreaChanged(bool value) => Measurement = null;

	/// <summary>The length or area of what has been drawn so far, in feet, miles, acres or square feet.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasMeasurement))]
	public partial string Measurement { get; set; }

	public bool HasMeasurement => Measurement != null;

	/// <summary>How distances and areas are worked out, for the tool's fine print.</summary>
	public string MeasureMethod => Measure.Method;

	private void UpdateMeasurement(IReadOnlyList<LatLon> points, bool finished)
	{
		Coordinate[] coords = points.Select(p => new Coordinate(p.Longitude, p.Latitude)).ToArray();
		if (!MeasureArea)
		{
			Measurement = coords.Length < 2
				? coords.Length == 0 ? null : "Click the next point."
				: "Distance: " + Length(Measure.Length(GisSetup.Factory.CreateLineString(coords)));
			return;
		}
		if (coords.Length < 3)
		{
			Measurement = coords.Length == 0 ? null : "Click at least three corners.";
			return;
		}
		Polygon area = GisSetup.Factory.CreatePolygon(coords.Append(coords[0]).ToArray());
		double squareMetres = Measure.Area(area.IsValid ? area : area.Buffer(0));
		double perimeter = Measure.Length(area.ExteriorRing);
		Measurement = $"Area: {Area(squareMetres)}{Environment.NewLine}Perimeter: {Length(perimeter)}";
	}

	/// <summary>"412 ft", or "1.24 mi (6,547 ft)" from a tenth of a mile.</summary>
	public static string Length(double metres)
	{
		double feet = Measure.MetresToFeet(metres);
		double miles = feet / 5280;
		return miles < 0.1
			? feet.ToString("N0", US) + " ft"
			: miles.ToString("N2", US) + " mi (" + feet.ToString("N0", US) + " ft)";
	}

	/// <summary>"0.42 acres (18,295 sq ft)".</summary>
	public static string Area(double squareMetres)
	{
		double acres = Measure.SquareMetresToAcres(squareMetres);
		double squareFeet = Measure.SquareMetresToSquareFeet(squareMetres);
		string acreText = acres < 10 ? acres.ToString("N2", US) : acres.ToString("N1", US);
		return $"{acreText} acre{(acreText == "1.00" ? "" : "s")} ({squareFeet.ToString("N0", US)} sq ft)";
	}

	/// <summary>Clears the measurement (the map forgets the shape).</summary>
	[RelayCommand]
	private void ClearMeasurement()
	{
		Measurement = null;
		ClearSketchRequested?.Invoke();
	}

	// ───────────── Bookmarks ─────────────

	/// <summary>The saved views of this database's map, oldest first.</summary>
	public ObservableCollection<MapBookmarkViewModel> Bookmarks { get; } = new();

	public bool HasBookmarks => Bookmarks.Count > 0;

	/// <summary>The name for the next bookmark (blank: "View 3").</summary>
	[ObservableProperty]
	public partial string NewBookmarkName { get; set; }

	/// <summary>The map should show exactly this area (a bookmark).</summary>
	public event Action<MapExtent> ShowViewRequested;

	private void ReadBookmarks()
	{
		Bookmarks.Clear();
		try
		{
			foreach (MapBookmarkRecord row in _app.Store.GetMapBookmarks())
			{
				Bookmarks.Add(new MapBookmarkViewModel(row, GoToBookmark, RemoveBookmark));
			}
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Couldn't read the map bookmarks", ex);
		}
		OnPropertyChanged(nameof(HasBookmarks));
	}

	/// <summary>Saves the area the map shows now as a bookmark.</summary>
	[RelayCommand]
	private void AddBookmark()
	{
		if (ViewExtent.IsEmpty)
		{
			return;
		}
		ReadLayers();
		string name = string.IsNullOrWhiteSpace(NewBookmarkName) ? $"View {Bookmarks.Count + 1}" : NewBookmarkName.Trim();
		MapExtent e = ViewExtent;
		try
		{
			MapBookmarkRecord row = _app.Store.SaveMapBookmark(new MapBookmarkRecord
			{
				Name = name,
				MinX = e.MinLongitude,
				MinY = e.MinLatitude,
				MaxX = e.MaxLongitude,
				MaxY = e.MaxLatitude,
			});
			Bookmarks.Add(new MapBookmarkViewModel(row, GoToBookmark, RemoveBookmark));
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't save the bookmark", ex);
			return;
		}
		NewBookmarkName = null;
		OnPropertyChanged(nameof(HasBookmarks));
		Message = $"Saved this view as {name}.";
	}

	private void GoToBookmark(MapBookmarkViewModel bookmark) => ShowViewRequested?.Invoke(bookmark.Extent);

	private void RemoveBookmark(MapBookmarkViewModel bookmark)
	{
		try
		{
			_app.Store.DeleteMapBookmark(bookmark.Id);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't remove the bookmark", ex);
			return;
		}
		Bookmarks.Remove(bookmark);
		OnPropertyChanged(nameof(HasBookmarks));
		Message = $"Removed the bookmark {bookmark.Name}.";
	}
}
