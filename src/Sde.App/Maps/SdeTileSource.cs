using BruTile;
using BruTile.Predefined;

namespace Sde.App.Maps;

/// <summary>
/// One of our tile services (a basemap or an overlay such as FEMA's flood zones) as a tile source for the map engine.
/// Every tile goes through <see cref="TileCache.FetchAsync"/>, so the engine shares the app's folder of tiles, its
/// User-Agent and its limit of four downloads at a time. A tile that can't be had is reported to the page once.
/// </summary>
public sealed class SdeTileSource : ILocalTileSource, IDisposable
{
	private readonly TileCache _cache;
	private readonly CancellationTokenSource _stop = new CancellationTokenSource();
	private int _requests;

	/// <param name="basemap">The tiles to show.</param>
	/// <param name="cache">Where tiles are kept and how they are downloaded.</param>
	/// <param name="scale">2 on a Retina screen: services that draw any area are asked for sharper tiles.</param>
	public SdeTileSource(Basemap basemap, TileCache cache, int scale = 1)
	{
		Basemap = basemap;
		_cache = cache;
		Scale = scale;
		Schema = new GlobalSphericalMercator(0, basemap.MaxZoom);
		// The page shows one attribution line of its own, so the engine is given none.
		Attribution = new Attribution();
	}

	public Basemap Basemap { get; }

	/// <summary>1, or 2 when Retina tiles are asked for.</summary>
	public int Scale { get; }

	public ITileSchema Schema { get; }

	public string Name => Basemap.Name;

	public Attribution Attribution { get; }

	/// <summary>Tiles the engine has asked for (for the tests that check how many a view needs).</summary>
	public int Requests => _requests;

	public async Task<byte[]> GetTileAsync(TileInfo tileInfo)
	{
		Interlocked.Increment(ref _requests);
		if (_stop.IsCancellationRequested)
		{
			return null;
		}
		TileFetch fetch = await _cache.FetchAsync(Basemap, tileInfo.Index.Level, tileInfo.Index.Col, tileInfo.Index.Row, Scale, _stop.Token).ConfigureAwait(false);
		if (!_stop.IsCancellationRequested)
		{
			_cache.ReportProblem(fetch.Problem);
		}
		return fetch.Bytes;
	}

	/// <summary>Stops the downloads this source started (when its layer goes away).</summary>
	public void Dispose()
	{
		_stop.Cancel();
	}
}
