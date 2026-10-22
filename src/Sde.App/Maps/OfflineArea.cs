namespace Sde.App.Maps;

/// <summary>What keeping an area offline did.</summary>
/// <param name="Kept">Map pieces now on this Mac (downloaded or already there).</param>
/// <param name="Failed">Pieces that couldn't be downloaded.</param>
/// <param name="MaxZoom">The closest zoom kept.</param>
/// <param name="Problem">Why some failed, in plain English, or null.</param>
public sealed record OfflineAreaResult(int Kept, int Failed, int MaxZoom, string Problem);

/// <summary>
/// Keeping a map area on this Mac for working without a connection (C5): the USGS background map's pieces (tiles) for
/// zooms 12 to 17 inside an area, at most about 5,000 of them, saved in the tile cache the map already reads. Only USGS
/// maps: OpenStreetMap's tile rules forbid downloading areas in bulk. Pieces kept this way are still used offline after
/// the cache's 30 days, until a connection lets SDE fetch fresh ones.
/// </summary>
public static class OfflineArea
{
	public const int MinZoom = 12;

	public const int MaxZoom = 17;

	/// <summary>The most pieces one area may take (about 100 MB of USGS tiles).</summary>
	public const int MaxTiles = 5000;

	/// <summary>The background maps an area can be kept for (USGS only).</summary>
	public static readonly IReadOnlyList<Basemap> Choices = new[] { Basemaps.UsgsTopo, Basemaps.UsgsImagery };

	/// <summary>The USGS map to keep for the map now shown: the same one when it is a USGS map, else the topographic map.</summary>
	public static Basemap For(Basemap shown) => Choices.FirstOrDefault(b => b.Id == shown?.Id) ?? Basemaps.UsgsTopo;

	/// <summary>The pieces covering the area at zooms 12 to <paramref name="maxZoom"/>, nearest zooms last.</summary>
	public static List<(int Z, int X, int Y)> Tiles(double west, double south, double east, double north, int maxZoom = MaxZoom)
	{
		List<(int, int, int)> tiles = new List<(int, int, int)>();
		for (int z = MinZoom; z <= maxZoom; z++)
		{
			(int x0, int y0) = TileAt(west, north, z);
			(int x1, int y1) = TileAt(east, south, z);
			for (int x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
			{
				for (int y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++)
				{
					tiles.Add((z, x, y));
				}
			}
		}
		return tiles;
	}

	/// <summary>
	/// The closest zoom (12 to 17, and no closer than the map goes) whose pieces for the area fit under
	/// <see cref="MaxTiles"/>; null when even zoom 12 is too many (the area is too large).
	/// </summary>
	public static int? FittingZoom(double west, double south, double east, double north, Basemap basemap)
	{
		for (int z = Math.Min(MaxZoom, basemap?.MaxZoom ?? MaxZoom); z >= MinZoom; z--)
		{
			if (Count(west, south, east, north, z) <= MaxTiles)
			{
				return z;
			}
		}
		return null;
	}

	/// <summary>How many pieces cover the area at zooms 12 to <paramref name="maxZoom"/>.</summary>
	public static long Count(double west, double south, double east, double north, int maxZoom)
	{
		long count = 0;
		for (int z = MinZoom; z <= maxZoom; z++)
		{
			(int x0, int y0) = TileAt(west, north, z);
			(int x1, int y1) = TileAt(east, south, z);
			count += (long)(Math.Abs(x1 - x0) + 1) * (Math.Abs(y1 - y0) + 1);
		}
		return count;
	}

	/// <summary>Fetches every piece (from disk when already kept and fresh) and reports how many are done.</summary>
	public static async Task<OfflineAreaResult> KeepAsync(TileCache cache, Basemap basemap, IReadOnlyList<(int Z, int X, int Y)> tiles, IProgress<int> progress, CancellationToken cancel)
	{
		int done = 0;
		int failed = 0;
		string problem = null;
		await Parallel.ForEachAsync(tiles, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancel }, async (t, ct) =>
		{
			TileFetch fetch = await cache.FetchAsync(basemap, t.Z, t.X, t.Y, 1, ct);
			if (fetch.Bytes == null)
			{
				Interlocked.Increment(ref failed);
				problem ??= fetch.Problem;
			}
			progress?.Report(Interlocked.Increment(ref done));
		});
		return new OfflineAreaResult(tiles.Count - failed, failed, tiles.Count == 0 ? MinZoom : tiles.Max(t => t.Z), problem);
	}

	private static (int X, int Y) TileAt(double longitude, double latitude, int z)
	{
		double n = Math.Pow(2, z);
		int x = (int)Math.Floor(WebMercator.U(longitude) * n);
		int y = (int)Math.Floor(WebMercator.V(Math.Clamp(latitude, -WebMercator.MaxLatitude, WebMercator.MaxLatitude)) * n);
		return (Math.Clamp(x, 0, (int)n - 1), Math.Clamp(y, 0, (int)n - 1));
	}
}
