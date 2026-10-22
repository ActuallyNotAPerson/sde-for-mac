using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetTopologySuite.Geometries;
using Sde.App.Maps;
using Sde.App.Services;
using Sde.Gis;
using Sde.Gis.Features;
using Sde.Gis.Online;

namespace Sde.App.ViewModels.Sheets;

/// <summary>
/// Add layer, then From an ArcGIS web address: paste a FeatureServer or MapServer layer's address, check it, and
/// download its features for the area in view (or the whole layer) into the layers GeoPackage.
/// </summary>
public sealed partial class OnlineLayerSheetViewModel : SheetViewModel
{
	private readonly AppServices _app;
	private readonly MapExtent _extent;
	private readonly Func<FeatureLayer, string, string, Task<MapLayerViewModel>> _add;

	/// <param name="extent">The area the map shows now.</param>
	/// <param name="add">Saves and shows the downloaded layer (features, address, copyright).</param>
	public OnlineLayerSheetViewModel(AppServices app, MapExtent extent, Func<FeatureLayer, string, string, Task<MapLayerViewModel>> add)
		: base("Add a layer from ArcGIS")
	{
		_app = app;
		_extent = extent;
		_add = add;
	}

	public override SheetSize Size => SheetSize.Medium;

	/// <summary>The layer's address, such as https://…/FeatureServer/0.</summary>
	[ObservableProperty]
	public partial string Url { get; set; }

	partial void OnUrlChanged(string value)
	{
		Info = null;
		Problem = null;
	}

	/// <summary>What the service said about the layer, once checked.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasInfo), nameof(InfoText), nameof(CopyrightText), nameof(HasCopyright))]
	public partial ArcGisLayerInfo Info { get; set; }

	public bool HasInfo => Info != null;

	/// <summary>"Parcels: areas, 12 fields."</summary>
	public string InfoText => Info == null ? null : $"{Info.Name}: {Kind(Info.GeometryKind)}, {Info.Fields.Count} field{(Info.Fields.Count == 1 ? "" : "s")}.";

	public string CopyrightText => string.IsNullOrWhiteSpace(Info?.Copyright) ? null : "Credit: " + Info.Copyright.Trim();

	public bool HasCopyright => CopyrightText != null;

	/// <summary>True to download the whole layer; false (the default) for the area the map shows now.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsAreaInView))]
	public partial bool WholeLayer { get; set; }

	public bool IsAreaInView
	{
		get => !WholeLayer;
		set => WholeLayer = !value;
	}

	/// <summary>Why the address couldn't be used, in plain English, or null.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasProblem))]
	public partial string Problem { get; set; }

	public bool HasProblem => Problem != null;

	[ObservableProperty]
	public partial bool IsBusy { get; set; }

	/// <summary>"Downloading…" while busy.</summary>
	[ObservableProperty]
	public partial string Status { get; set; }

	public override bool CanCancel => !IsBusy;

	private ArcGisRestClient Client() => new ArcGisRestClient(_app.HttpClientFactory());

	/// <summary>Asks the service about the layer (name, kind, fields).</summary>
	[RelayCommand]
	private async Task Check()
	{
		Problem = null;
		IsBusy = true;
		Status = "Checking the address…";
		try
		{
			Info = await Client().GetLayerInfoAsync(Url, CancellationToken.None);
		}
		catch (GisException ex)
		{
			Problem = ex.Message;
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
		{
			Problem = "The ArcGIS service didn't answer. Check the address and the internet connection.";
		}
		finally
		{
			IsBusy = false;
			Status = null;
		}
	}

	/// <summary>Downloads the features and adds the layer.</summary>
	[RelayCommand]
	private async Task Add()
	{
		if (Info == null)
		{
			await Check();
			if (Info == null)
			{
				return;
			}
		}
		Problem = null;
		IsBusy = true;
		Status = "Downloading the features…";
		string url = Info.Url;
		Envelope area = WholeLayer || _extent.IsEmpty ? null : new Envelope(_extent.MinLongitude, _extent.MaxLongitude, _extent.MinLatitude, _extent.MaxLatitude);
		try
		{
			ArcGisDownload download = await Client().DownloadAsync(url, area, new ArcGisQueryOptions(), CancellationToken.None);
			if (download.Layer.Features.Count == 0)
			{
				Problem = WholeLayer ? "The layer has no features." : "The layer has no features in the area the map shows. Move the map, or choose the whole layer.";
				return;
			}
			if (download.Truncated)
			{
				download.Layer.Warnings.Add("Only part of the layer was downloaded: the service stopped sending features. Zoom in and add it again for a smaller area.");
			}
			Status = "Saving…";
			MapLayerViewModel added = await _add(download.Layer, url, Info.Copyright);
			IsBusy = false;
			Close(added);
		}
		catch (GisException ex)
		{
			Problem = ex.Message;
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
		{
			Problem = "The ArcGIS service didn't answer. Check the address and the internet connection.";
		}
		finally
		{
			IsBusy = false;
			Status = null;
		}
	}

	private static string Kind(GeometryKind kind) => kind switch
	{
		GeometryKind.Point => "points",
		GeometryKind.Line => "lines",
		GeometryKind.Polygon => "areas",
		_ => "features",
	};
}
