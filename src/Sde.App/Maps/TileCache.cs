using System.Net;
using System.Reflection;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Sde.App.Maps;

/// <summary>
/// Map tiles for the Map page. A tile is looked up in memory, then in a folder on disk (so a place you looked at
/// yesterday still shows with no internet), and only then downloaded. Downloads run a few at a time off the screen's
/// thread, and the map is told to redraw as each tile arrives.
/// </summary>
public sealed class TileCache
{
	/// <summary>Tiles on disk older than this are downloaded again (the old one is kept if that fails).</summary>
	public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

	private const int MaxInMemory = 400;
	private const int MaxDownloadsAtOnce = 4;
	private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

	private readonly string _folder;
	private readonly Func<string, CancellationToken, Task<byte[]>> _download;
	private readonly Dictionary<string, Bitmap> _memory = new();
	private readonly Queue<string> _order = new();
	private readonly HashSet<string> _loading = new();
	private readonly Dictionary<string, DateTime> _failed = new();
	private readonly SemaphoreSlim _slots = new SemaphoreSlim(MaxDownloadsAtOnce);
	private readonly CancellationTokenSource _stop = new CancellationTokenSource();

	/// <param name="folder">Where tiles are kept on disk (created when the first one is saved).</param>
	/// <param name="download">Gets the bytes at a web address; null uses <see cref="DownloadAsync"/> with SDE's own HTTP client.</param>
	public TileCache(string folder, Func<string, CancellationToken, Task<byte[]>> download = null)
	{
		_folder = folder;
		_download = download ?? ((url, cancel) => DownloadAsync(Http.Client, url, cancel));
	}

	/// <summary>
	/// How SDE names itself to the tile server, with the app's version: OpenStreetMap's tile rules ask every app to say
	/// who it is.
	/// </summary>
	public static string UserAgent { get; } = "SDE-for-Mac/"
		+ (typeof(TileCache).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.1")
		+ " (Substantial Damage Estimator)";

	/// <summary>
	/// An HTTP client for map tiles that sends <see cref="UserAgent"/>. With no handler it uses the network; tests pass
	/// one that answers without it.
	/// </summary>
	public static HttpClient CreateClient(HttpMessageHandler handler = null)
	{
		HttpClient c = handler == null ? new HttpClient() : new HttpClient(handler);
		c.Timeout = TimeSpan.FromSeconds(15);
		c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
		return c;
	}

	/// <summary>Gets one tile's bytes. An answer other than success throws HttpRequestException with its status code.</summary>
	public static async Task<byte[]> DownloadAsync(HttpClient client, string url, CancellationToken cancel)
	{
		using HttpResponseMessage response = await client.GetAsync(url, cancel);
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadAsByteArrayAsync(cancel);
	}

	/// <summary>Raised on the screen's thread when a tile has arrived or could not be had, so the map should redraw.</summary>
	public event Action Changed;

	/// <summary>Raised (once per failed tile) with a plain-English reason when a tile could not be downloaded.</summary>
	public event Action<string> DownloadFailed;

	/// <summary>Tiles that could not be downloaded since the last <see cref="ResetFailures"/>.</summary>
	public int FailureCount { get; private set; }

	/// <summary>The tile if it is in memory; otherwise null, and it is loaded in the background.</summary>
	public Bitmap TryGet(Basemap basemap, int z, int x, int y)
	{
		if (!basemap.HasTiles)
		{
			return null;
		}
		string key = Key(basemap, z, x, y);
		if (_memory.TryGetValue(key, out Bitmap bitmap))
		{
			return bitmap;
		}
		if (_loading.Contains(key) || _failed.TryGetValue(key, out DateTime when) && DateTime.UtcNow - when < RetryAfterFailure)
		{
			return null;
		}
		_loading.Add(key);
		_ = Task.Run(() => LoadAsync(basemap, z, x, y, key));
		return null;
	}

	/// <summary>Forgets which tiles failed, so the next draw tries them again.</summary>
	public void ResetFailures()
	{
		_failed.Clear();
		FailureCount = 0;
	}

	/// <summary>Stops downloads that are still running (when the page is closed).</summary>
	public void Stop() => _stop.Cancel();

	private static string Key(Basemap b, int z, int x, int y) => $"{b.Id}/{z}/{x}/{y}";

	private string PathOf(string key) => System.IO.Path.Combine(_folder, key.Replace('/', System.IO.Path.DirectorySeparatorChar) + ".tile");

	private async Task LoadAsync(Basemap basemap, int z, int x, int y, string key)
	{
		byte[] bytes = null;
		Bitmap bitmap = null;
		string error = null;
		try
		{
			string path = PathOf(key);
			byte[] stale = null;
			if (File.Exists(path))
			{
				byte[] onDisk = await File.ReadAllBytesAsync(path, _stop.Token);
				if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < MaxAge)
				{
					bytes = onDisk;
				}
				else
				{
					stale = onDisk;
				}
			}
			if (bytes == null)
			{
				await _slots.WaitAsync(_stop.Token);
				try
				{
					bytes = await _download(basemap.TileUrl(z, x, y), _stop.Token);
				}
				catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !_stop.IsCancellationRequested)
				{
					bytes = stale;
					error = DownloadProblem(ex);
				}
				finally
				{
					_slots.Release();
				}
				if (bytes != null && error == null)
				{
					Save(path, bytes);
				}
			}
			if (bytes != null)
			{
				using MemoryStream stream = new MemoryStream(bytes);
				bitmap = new Bitmap(stream);
			}
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
		{
			bitmap = null;
			error = "A map tile couldn't be read.";
		}
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			_loading.Remove(key);
			if (bitmap != null)
			{
				Remember(key, bitmap);
			}
			else
			{
				_failed[key] = DateTime.UtcNow;
				FailureCount++;
			}
			if (error != null)
			{
				DownloadFailed?.Invoke(error);
			}
			Changed?.Invoke();
		});
	}

	/// <summary>Why a tile couldn't be downloaded, in plain English.</summary>
	private static string DownloadProblem(Exception ex)
	{
		if (ex is HttpRequestException { StatusCode: HttpStatusCode status })
		{
			// The map service answered, so the connection works: say what it answered instead.
			int code = (int)status;
			return status switch
			{
				HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"The map service refused the request (HTTP {code}).",
				HttpStatusCode.NotFound => $"The map service couldn't find this part of the map (HTTP {code}).",
				_ => $"The map service couldn't send this part of the map (HTTP {code}).",
			};
		}
		return ex is HttpRequestException ? "The map service didn't answer. Check the internet connection." : "The map service took too long to answer.";
	}

	private void Remember(string key, Bitmap bitmap)
	{
		_memory[key] = bitmap;
		_order.Enqueue(key);
		while (_order.Count > MaxInMemory)
		{
			string old = _order.Dequeue();
			if (_memory.Remove(old, out Bitmap gone))
			{
				gone.Dispose();
			}
		}
	}

	/// <summary>Writes through a temporary file so a half-written tile is never read back.</summary>
	private static void Save(string path, byte[] bytes)
	{
		try
		{
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
			string temp = path + ".part";
			File.WriteAllBytes(temp, bytes);
			File.Move(temp, path, overwrite: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// A tile that can't be kept is still shown this time.
		}
	}

	/// <summary>Holds the one HTTP client every tile download shares, made on first use.</summary>
	private static class Http
	{
		public static readonly HttpClient Client = CreateClient();
	}
}
