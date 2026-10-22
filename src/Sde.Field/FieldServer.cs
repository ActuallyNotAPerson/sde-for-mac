using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.Field;

/// <summary>How to run the phone form.</summary>
public sealed class FieldServerOptions
{
	/// <summary>The port; 0 picks a free one.</summary>
	public int Port { get; init; }

	/// <summary>
	/// The address to listen on: a private LAN address (10/8, 172.16/12, 192.168/16) or loopback. Null finds the Mac's
	/// address on its active private network. Never "any" (0.0.0.0).
	/// </summary>
	public IPAddress BindAddress { get; init; }

	/// <summary>Also listen on 127.0.0.1 (same port), for trying the page on the Mac itself.</summary>
	public bool IncludeLoopback { get; init; }

	/// <summary>The server stops itself after this long (12 hours).</summary>
	public TimeSpan Lifetime { get; init; } = TimeSpan.FromHours(12);

	/// <summary>The current time in UTC for pairing codes and lockouts (tests replace it).</summary>
	public Func<DateTime> Clock { get; init; }

	/// <summary>Where uploaded photos wait while they are checked and converted. Null = a folder in the system's temp folder.</summary>
	public string TempFolder { get; init; }
}

/// <summary>
/// The phone form's web server: plain HTTP on the local network (the page says so), listening only on a private LAN
/// address (and loopback), refusing anyone whose address isn't private or loopback, and serving nothing but the pairing
/// screen until a phone has paired with the one-time QR token or PIN. Every API call needs the session cookie; every
/// call that changes something also needs the <c>X-SDE: 1</c> header, which a page on another site can't send without
/// the browser asking first (and this server never says yes). Requests naming another host are refused, so a web page
/// can't reach the server through a look-alike domain name.
/// </summary>
public sealed class FieldServer : IAsyncDisposable
{
	public const string CookieName = "sde_session";
	public const string CsrfHeader = "X-SDE";
	public const long MaxJsonBytes = 1024 * 1024;
	public const int MaxChanges = 200;

	private static readonly IReadOnlyDictionary<string, (byte[] Body, string Type)> Assets = LoadAssets();

	private readonly Func<SdeStore> _store;
	private readonly IFieldWriter _writer;
	private readonly IPhotoConverter _photos;
	private readonly object _gate = new object();
	private WebApplication _app;
	private Timer _autoStop;
	private HashSet<string> _hosts;
	private string _tempFolder;

	public FieldServer(Func<SdeStore> store, IFieldWriter writer, IPhotoConverter photos = null)
	{
		_store = store;
		_writer = writer;
		_photos = photos ?? new KeepPhotoConverter();
	}

	/// <summary>Pairing and sessions; replaced on each start.</summary>
	public PairingManager Pairing { get; private set; } = new PairingManager();

	public bool IsRunning => _app != null;

	/// <summary>The address phones use, e.g. http://192.168.1.20:52814/.</summary>
	public Uri BaseAddress { get; private set; }

	/// <summary>When the server stops itself (UTC).</summary>
	public DateTime StopsAtUtc { get; private set; }

	/// <summary>The link in the QR code: the base address, /pair and the current one-time token.</summary>
	public string PairingUrl => BaseAddress == null ? null : BaseAddress + "pair?t=" + Pairing.Current.Token;

	/// <summary>Raised (on any thread) when the server stops, with the reason ("Stopped", or the 12 hours ran out).</summary>
	public event Action<string> Stopped;

	/// <summary>Raised (on any thread) for an unexpected error while handling a request, so the app can log it.</summary>
	public event Action<Exception> Failed;

	public void Start(FieldServerOptions options) => StartAsync(options).GetAwaiter().GetResult();

	public void Stop() => StopAsync().GetAwaiter().GetResult();

	public async Task StartAsync(FieldServerOptions options)
	{
		options ??= new FieldServerOptions();
		if (IsRunning)
		{
			throw new InvalidOperationException("The phone form is already running.");
		}
		IPAddress address = options.BindAddress ?? NetworkAccess.FindLanAddress()
			?? throw new InvalidOperationException("This Mac isn't connected to a private Wi-Fi or wired network, so phones can't reach it. Join your office or field Wi-Fi and try again.");
		if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || !NetworkAccess.IsPrivateOrLoopback(address))
		{
			throw new ArgumentException("The phone form only listens on a private network address or on this computer.", nameof(options));
		}
		bool alsoLoopback = options.IncludeLoopback && !IPAddress.IsLoopback(address);
		int port = options.Port != 0 ? options.Port : NetworkAccess.FreePort(address);
		Func<DateTime> clock = options.Clock ?? (() => DateTime.UtcNow);
		Pairing = new PairingManager(clock);
		_tempFolder = options.TempFolder ?? Path.Combine(Path.GetTempPath(), "sde-phone-" + Guid.NewGuid().ToString("N")[..8]);
		_hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { HostName(address, port) };
		if (alsoLoopback)
		{
			_hosts.Add(HostName(IPAddress.Loopback, port));
			_hosts.Add("localhost:" + port);
		}

		WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
		{
			ApplicationName = typeof(FieldServer).Assembly.GetName().Name,
			ContentRootPath = AppContext.BaseDirectory,
			EnvironmentName = Environments.Production,
		});
		builder.Logging.ClearProviders();
		builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2));
		builder.WebHost.ConfigureKestrel(k =>
		{
			k.AddServerHeader = false;
			k.Limits.MaxRequestBodySize = MaxJsonBytes;
			k.Listen(address, port);
			if (alsoLoopback)
			{
				k.Listen(IPAddress.Loopback, port);
			}
		});
		WebApplication app = builder.Build();
		app.Use(Guard);
		MapEndpoints(app);
		await app.StartAsync();

		lock (_gate)
		{
			_app = app;
			BaseAddress = new Uri("http://" + HostName(address, port) + "/");
			StopsAtUtc = clock() + options.Lifetime;
			_autoStop = new Timer(_ => _ = StopAsync("The phone form stopped itself after 12 hours."), null, options.Lifetime, Timeout.InfiniteTimeSpan);
		}
	}

	public async Task StopAsync(string reason = "Stopped")
	{
		WebApplication app;
		lock (_gate)
		{
			app = _app;
			_app = null;
			_autoStop?.Dispose();
			_autoStop = null;
		}
		if (app == null)
		{
			return;
		}
		Pairing.Reset();
		try
		{
			await app.StopAsync();
		}
		finally
		{
			await app.DisposeAsync();
			BaseAddress = null;
			try
			{
				if (Directory.Exists(_tempFolder))
				{
					Directory.Delete(_tempFolder, recursive: true);
				}
			}
			catch (IOException)
			{
			}
			Stopped?.Invoke(reason);
		}
	}

	public async ValueTask DisposeAsync() => await StopAsync();

	private static string HostName(IPAddress address, int port) => address + ":" + port;

	// ───────────────────────── Guard: who may ask what ─────────────────────────

	private async Task Guard(HttpContext ctx, Func<Task> next)
	{
		HttpResponse res = ctx.Response;
		res.Headers["X-Content-Type-Options"] = "nosniff";
		res.Headers["Referrer-Policy"] = "no-referrer";
		res.Headers["X-Frame-Options"] = "DENY";
		res.Headers["Cache-Control"] = "no-store";
		res.Headers["Content-Security-Policy"] =
			"default-src 'self'; img-src 'self' data: blob:; style-src 'self'; script-src 'self'; connect-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";

		if (!NetworkAccess.IsPrivateOrLoopback(ctx.Connection.RemoteIpAddress))
		{
			await Fail(ctx, StatusCodes.Status403Forbidden, "The phone form only answers devices on a private local network.");
			return;
		}
		if (!_hosts.Contains(ctx.Request.Host.Value ?? ""))
		{
			await Fail(ctx, StatusCodes.Status400BadRequest, "Open the phone form with the address shown on the Mac.");
			return;
		}
		bool changes = !(HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method));
		if (changes && ctx.Request.Headers[CsrfHeader] != "1")
		{
			await Fail(ctx, StatusCodes.Status403Forbidden, "Refused: this request didn't come from the phone form.");
			return;
		}
		string path = ctx.Request.Path.Value ?? "";
		if (path.StartsWith("/api/", StringComparison.Ordinal) && path != "/api/pair")
		{
			PairedPhone phone = Pairing.FindSession(ctx.Request.Cookies[CookieName]);
			if (phone == null)
			{
				await Fail(ctx, StatusCodes.Status401Unauthorized, "This phone isn't paired. Pair it with the QR code or PIN on the Mac.");
				return;
			}
			ctx.Items[typeof(PairedPhone)] = phone;
		}

		try
		{
			await next();
		}
		catch (AssessmentLockedException ex)
		{
			await Fail(ctx, StatusCodes.Status409Conflict, ex.Message);
		}
		catch (FieldInputException ex)
		{
			await Fail(ctx, StatusCodes.Status400BadRequest, ex.Message);
		}
		catch (ArgumentException ex) when (ex.GetType() == typeof(ArgumentException))
		{
			await Fail(ctx, StatusCodes.Status404NotFound, "That assessment no longer exists.");
		}
		catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
		{
			await Fail(ctx, StatusCodes.Status413PayloadTooLarge, "That is too large to send. Photos can be up to 25 MB.");
		}
		catch (Exception ex) when (ex is BadHttpRequestException or JsonException or InvalidDataException)
		{
			await Fail(ctx, StatusCodes.Status400BadRequest, "The phone sent something SDE couldn't read. Reload the page and try again.");
		}
		catch (Exception ex) when (ex is not OutOfMemoryException && !ctx.RequestAborted.IsCancellationRequested)
		{
			Failed?.Invoke(ex);
			await Fail(ctx, StatusCodes.Status500InternalServerError, "Something went wrong on the Mac, so that wasn't saved. Try again; if it keeps happening, check SDE on the Mac.");
		}
	}

	private static async Task Fail(HttpContext ctx, int status, string message)
	{
		if (ctx.Response.HasStarted)
		{
			return;
		}
		ctx.Response.StatusCode = status;
		ctx.Response.Headers["Cache-Control"] = "no-store";
		await ctx.Response.WriteAsJsonAsync(new { error = message });
	}

	private static PairedPhone PhoneOf(HttpContext ctx) => (PairedPhone)ctx.Items[typeof(PairedPhone)];

	private static SaveContext ContextFor(HttpContext ctx) => new SaveContext { Via = SaveVia.Phone, User = PhoneOf(ctx).Name };

	// ───────────────────────── Endpoints ─────────────────────────

	private void MapEndpoints(WebApplication app)
	{
		app.MapGet("/", () => Asset("index.html"));
		app.MapGet("/pair", () => Asset("index.html"));
		app.MapGet("/app.js", () => Asset("app.js"));
		app.MapGet("/app.css", () => Asset("app.css"));

		app.MapPost("/api/pair", (HttpContext ctx, PairRequest req) => Pair(ctx, req));
		app.MapGet("/api/session", (HttpContext ctx) => Results.Json(new { paired = true, name = PhoneOf(ctx).Name }));
		app.MapGet("/api/lookups", () => Results.Json(FieldApi.Lookups(_store().Reference)));
		app.MapGet("/api/structures", (HttpContext ctx) => Results.Json(FieldApi.Search(_store(), ctx.Request.Query["q"].ToString())));
		app.MapPost("/api/structures", AddStructure);
		app.MapGet("/api/teams", () => Results.Json(FieldApi.Teams(_store())));
		app.MapGet("/api/teams/{id:guid}/assignments", (Guid id) => Results.Json(FieldApi.Assignments(_store(), id)));
		app.MapPost("/api/properties/{id:guid}/assessments", AddAssessment);
		app.MapGet("/api/assessments/{id:guid}", (Guid id) =>
		{
			SdeStore store = _store();
			AssessmentFile file = Load(store, id);
			return file == null ? NotFound() : Results.Json(FieldApi.Describe(store, file));
		});
		app.MapPost("/api/assessments/{id:guid}/changes", ApplyChanges);
		app.MapPost("/api/assessments/{id:guid}/photos", UploadPhoto);
	}

	private static IResult NotFound() => Results.Json(new { error = "That assessment no longer exists." }, statusCode: StatusCodes.Status404NotFound);

	private static IResult Locked(SdeStore store, Guid id) =>
		Results.Json(new { error = new AssessmentLockedException(id, store.GetStatus(id).Value, Array.Empty<FieldDiff>()).Message }, statusCode: StatusCodes.Status409Conflict);

	private static AssessmentFile Load(SdeStore store, Guid id)
	{
		AssessmentFile file = store.LoadAssessment(id);
		return file == null || !file.Assessment.Active || file.Property?.Active != true ? null : file;
	}

	private IResult Pair(HttpContext ctx, PairRequest req)
	{
		string remote = ctx.Connection.RemoteIpAddress?.ToString();
		PairResult result = !string.IsNullOrEmpty(req?.Token)
			? Pairing.PairWithToken(req.Token, req.Name, remote)
			: Pairing.PairWithPin(req?.Pin, req?.Name, remote);
		if (result.Outcome != PairOutcome.Paired)
		{
			int status = result.Outcome == PairOutcome.LockedOut ? StatusCodes.Status429TooManyRequests : StatusCodes.Status403Forbidden;
			return Results.Json(new { error = result.Message }, statusCode: status);
		}
		ctx.Response.Cookies.Append(CookieName, result.SessionToken, new CookieOptions
		{
			HttpOnly = true,
			SameSite = SameSiteMode.Strict,
			Path = "/",
			Secure = false,          // plain HTTP on the local network; see docs/PHONE-FORM.md
			IsEssential = true,
			Expires = new DateTimeOffset(StopsAtUtc, TimeSpan.Zero),
		});
		return Results.Json(new { paired = true, name = result.Phone.Name });
	}

	private async Task<IResult> AddStructure(HttpContext ctx, AddStructureRequest req)
	{
		SdeStore store = _store();
		if ((req?.Changes?.Count ?? 0) > MaxChanges)
		{
			throw new FieldInputException("Too many changes at once.");
		}
		AssessmentFile file = FieldApi.NewStructure(store, req);
		AssessmentFile saved = await _writer.AddAsync(file, ContextFor(ctx));
		return Results.Json(FieldApi.Describe(store, saved));
	}

	private async Task<IResult> AddAssessment(HttpContext ctx, Guid id)
	{
		SdeStore store = _store();
		AssessmentFile file = FieldApi.NewAssessment(store, id);
		if (file == null)
		{
			return Results.Json(new { error = "That structure no longer exists." }, statusCode: StatusCodes.Status404NotFound);
		}
		AssessmentFile saved = await _writer.AddAsync(file, ContextFor(ctx));
		return Results.Json(FieldApi.Describe(store, saved));
	}

	private async Task<IResult> ApplyChanges(HttpContext ctx, Guid id, ChangesRequest req)
	{
		SdeStore store = _store();
		AssessmentFile current = Load(store, id);
		if (current == null)
		{
			return NotFound();
		}
		if (store.IsLocked(id))
		{
			return Locked(store, id);
		}
		IReadOnlyList<ChangeDto> changes = req?.Changes ?? Array.Empty<ChangeDto>();
		if (changes.Count > MaxChanges)
		{
			throw new FieldInputException("Too many changes at once.");
		}
		// Check everything on a copy first, so a bad value never reaches the Mac's editor.
		FieldApi.Apply(current, changes, store.Reference, store.Calculator);
		AssessmentFile saved = await _writer.ApplyAsync(id, f => FieldApi.Apply(f, changes, store.Reference, store.Calculator), ContextFor(ctx));
		return Results.Json(FieldApi.Describe(store, saved));
	}

	private async Task<IResult> UploadPhoto(HttpContext ctx, Guid id)
	{
		SdeStore store = _store();
		if (Load(store, id) == null)
		{
			return NotFound();
		}
		if (store.IsLocked(id))
		{
			return Locked(store, id);
		}
		const long slack = 1024 * 1024;   // the form's other parts and boundaries
		if (ctx.Request.ContentLength > PhotoUpload.MaxBytes + slack)
		{
			return TooLarge();
		}
		IHttpMaxRequestBodySizeFeature limit = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
		if (limit is { IsReadOnly: false })
		{
			limit.MaxRequestBodySize = PhotoUpload.MaxBytes + slack;
		}
		if (!ctx.Request.HasFormContentType)
		{
			return Unsupported();
		}
		IFormCollection form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
		IFormFile photo = form.Files.GetFile("photo");
		if (photo == null || photo.Length == 0)
		{
			return Results.Json(new { error = "Choose or take a photo first." }, statusCode: StatusCodes.Status400BadRequest);
		}
		if (photo.Length > PhotoUpload.MaxBytes)
		{
			return TooLarge();
		}
		if (!(photo.ContentType ?? "").StartsWith("image/", StringComparison.OrdinalIgnoreCase))
		{
			return Unsupported();
		}
		byte[] head = new byte[16];
		int read;
		await using (Stream s = photo.OpenReadStream())
		{
			read = await s.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ctx.RequestAborted);
		}
		string extension = PhotoUpload.DetectExtension(head.AsSpan(0, read));
		if (extension == null)
		{
			return Unsupported();
		}
		string description = PairingManager.CleanText(form["description"].ToString(), 200);

		Directory.CreateDirectory(_tempFolder);
		string upload = Path.Combine(_tempFolder, Guid.NewGuid().ToString("N") + extension);
		string converted = null;
		try
		{
			await using (FileStream f = File.Create(upload))
			{
				await photo.CopyToAsync(f, ctx.RequestAborted);
			}
			try
			{
				converted = _photos.ToCompatible(upload, _tempFolder);
			}
			catch (IOException)
			{
				return Results.Json(new { error = "The Mac couldn't convert that photo. Try taking it again, or set the camera to save JPEG (Most Compatible)." },
					statusCode: StatusCodes.Status422UnprocessableEntity);
			}
			// Copied into the attachments folder here, on the web server's thread: the save itself runs on the Mac
			// app's UI thread, which a 25 MB copy would freeze.
			Sde.Core.Records.AttachmentRecord added = store.ImportPhoto(id, converted, description);
			try
			{
				AssessmentFile saved = await _writer.ApplyAsync(id, f => f.Attachments.Add(added), ContextFor(ctx));
				return Results.Json(new { photoCount = saved.Attachments.Count });
			}
			catch
			{
				// Not saved (locked meanwhile, or the save failed): don't leave the copied file behind.
				if (added != null && File.Exists(store.PhotoPath(added)))
				{
					File.Delete(store.PhotoPath(added));
				}
				throw;
			}
		}
		finally
		{
			File.Delete(upload);
			if (converted != null && converted != upload)
			{
				File.Delete(converted);
			}
		}
	}

	private static IResult TooLarge() =>
		Results.Json(new { error = "That photo is larger than 25 MB. Take it again at a lower resolution." }, statusCode: StatusCodes.Status413PayloadTooLarge);

	private static IResult Unsupported() =>
		Results.Json(new { error = "Only photos (JPEG, PNG or HEIC) can be added." }, statusCode: StatusCodes.Status415UnsupportedMediaType);

	// ───────────────────────── The page ─────────────────────────

	private static IResult Asset(string name)
	{
		(byte[] body, string type) = Assets[name];
		return Results.Bytes(body, type);
	}

	private static Dictionary<string, (byte[], string)> LoadAssets()
	{
		Assembly asm = typeof(FieldServer).Assembly;
		Dictionary<string, (byte[], string)> assets = new();
		foreach ((string name, string type) in new[]
		{
			("index.html", "text/html; charset=utf-8"),
			("app.js", "text/javascript; charset=utf-8"),
			("app.css", "text/css; charset=utf-8"),
		})
		{
			using Stream s = asm.GetManifestResourceStream("Sde.Field.wwwroot." + name) ?? throw new InvalidOperationException("Missing " + name);
			using MemoryStream m = new MemoryStream();
			s.CopyTo(m);
			assets[name] = (m.ToArray(), type);
		}
		return assets;
	}
}
