using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;
using Sde.Field;

namespace Sde.Field.Tests;

/// <summary>Converts "HEIC" by writing a small JPEG next to it, and remembers what it was asked to convert.</summary>
internal sealed class RecordingConverter : IPhotoConverter
{
	public List<string> Converted { get; } = new();

	public string ToCompatible(string path, string tempFolder)
	{
		if (Path.GetExtension(path) != ".heic")
		{
			return path;
		}
		Converted.Add(path);
		string jpeg = Path.Combine(tempFolder, Path.GetFileNameWithoutExtension(path) + "-converted.jpg");
		File.WriteAllBytes(jpeg, PhoneSession.Jpeg(64));
		return jpeg;
	}
}

/// <summary>A database with one structure, the phone form running on 127.0.0.1, and a "phone" (an HttpClient with cookies).</summary>
internal sealed class PhoneSession : IAsyncDisposable
{
	public PhoneSession(TimeSpan? lifetime = null)
	{
		Db = new TempDatabase();
		PropertyRecord p = Db.Store.SaveProperty(new PropertyRecord { StructureTypeID = 1, StreetNumber = "101", StreetName = "Sample Road", City = "Testville", ParcelNumber = "P-0001" });
		Assessment = Db.Store.CreateAssessment(p.PropertyID, applyDefaults: false);
		Server = new FieldServer(() => Db.Store, new DirectFieldWriter(() => Db.Store), Converter);
		Server.Start(new FieldServerOptions
		{
			BindAddress = IPAddress.Loopback,
			Clock = () => Now,
			Lifetime = lifetime ?? TimeSpan.FromHours(12),
			TempFolder = Path.Combine(Db.Folder, "phone-temp"),
		});
		Cookies = new CookieContainer();
		Client = new HttpClient(new HttpClientHandler { CookieContainer = Cookies, UseCookies = true }) { BaseAddress = Server.BaseAddress };
	}

	public TempDatabase Db { get; }
	public SdeStore Store => Db.Store;
	public AssessmentFile Assessment { get; }
	public Guid AssessmentId => Assessment.Assessment.AssessmentID;
	public FieldServer Server { get; }
	public RecordingConverter Converter { get; } = new RecordingConverter();
	public CookieContainer Cookies { get; }
	public HttpClient Client { get; }
	public DateTime Now { get; set; } = DateTime.UtcNow;

	public async Task<HttpResponseMessage> PairAsync(string name = "Test Inspector")
	{
		HttpResponseMessage res = await Post("/api/pair", new { token = Server.Pairing.Current.Token, name });
		Assert.Equal(HttpStatusCode.OK, res.StatusCode);
		return res;
	}

	public Task<HttpResponseMessage> Post(string path, object body, bool csrf = true)
	{
		HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
		if (csrf)
		{
			req.Headers.Add(FieldServer.CsrfHeader, "1");
		}
		return Client.SendAsync(req, TestContext.Current.CancellationToken);
	}

	public Task<HttpResponseMessage> Change(string key, string value) =>
		Post($"/api/assessments/{AssessmentId}/changes", new { changes = new[] { new { key, value } } });

	public Task<HttpResponseMessage> Upload(byte[] bytes, string fileName, string contentType, bool csrf = true)
	{
		MultipartFormDataContent form = new MultipartFormDataContent();
		ByteArrayContent file = new ByteArrayContent(bytes);
		file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
		form.Add(file, "photo", fileName);
		form.Add(new StringContent("Front of house"), "description");
		HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, $"/api/assessments/{AssessmentId}/photos") { Content = form };
		if (csrf)
		{
			req.Headers.Add(FieldServer.CsrfHeader, "1");
		}
		return Client.SendAsync(req, TestContext.Current.CancellationToken);
	}

	public static async Task<JsonElement> Json(HttpResponseMessage res) =>
		await res.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

	public static async Task<string> Error(HttpResponseMessage res) => (await Json(res)).GetProperty("error").GetString();

	/// <summary>A file that starts like a JPEG.</summary>
	public static byte[] Jpeg(int size)
	{
		byte[] b = new byte[size];
		b[0] = 0xFF;
		b[1] = 0xD8;
		b[2] = 0xFF;
		b[3] = 0xE0;
		return b;
	}

	/// <summary>A file that starts like an iPhone HEIC photo.</summary>
	public static byte[] Heic(int size)
	{
		byte[] b = new byte[size];
		b[3] = 0x18;
		Encoding.ASCII.GetBytes("ftypheic").CopyTo(b, 4);
		return b;
	}

	public async ValueTask DisposeAsync()
	{
		Client.Dispose();
		await Server.StopAsync();
		Db.Dispose();
	}
}

/// <summary>The phone form over real HTTP: who gets in, what is saved and how, and what is refused.</summary>
public class FieldServerTests
{
	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	[Fact]
	public async Task Page_IsServed_AndSaysTrafficIsNotEncrypted()
	{
		await using PhoneSession s = new PhoneSession();
		HttpResponseMessage res = await s.Client.GetAsync("/", Ct);
		Assert.Equal(HttpStatusCode.OK, res.StatusCode);
		string html = await res.Content.ReadAsStringAsync(Ct);
		Assert.Contains("not encrypted", html);
		Assert.Contains("capture=\"environment\"", html);
		Assert.Contains("default-src 'self'", res.Headers.GetValues("Content-Security-Policy").Single());
		Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync("/app.js", Ct)).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync("/pair?t=x", Ct)).StatusCode);
		Assert.DoesNotContain("—", html);
	}

	[Fact]
	public async Task Api_NeedsThePairingCookie()
	{
		await using PhoneSession s = new PhoneSession();
		Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.GetAsync("/api/session", Ct)).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.GetAsync("/api/structures?q=", Ct)).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await s.Change("Property.City", "Elsewhere")).StatusCode);
		// A made-up cookie doesn't help.
		s.Cookies.Add(s.Server.BaseAddress, new Cookie(FieldServer.CookieName, "made-up"));
		Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.GetAsync("/api/lookups", Ct)).StatusCode);
	}

	[Fact]
	public async Task Pairing_ByToken_SetsAStrictHttpOnlyCookie_AndTheTokenWorksOnce()
	{
		await using PhoneSession s = new PhoneSession();
		string token = s.Server.Pairing.Current.Token;
		Assert.Contains("/pair?t=" + token, s.Server.PairingUrl);
		HttpResponseMessage res = await s.Post("/api/pair", new { token, name = "Test Inspector" });
		Assert.Equal(HttpStatusCode.OK, res.StatusCode);
		string cookie = res.Headers.GetValues("Set-Cookie").Single();
		Assert.StartsWith(FieldServer.CookieName + "=", cookie);
		Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);
		// The cookie value is 256 bits (43 base64url characters).
		Assert.Equal(43, cookie.Split(';')[0].Length - FieldServer.CookieName.Length - 1);

		JsonElement session = await PhoneSession.Json(await s.Client.GetAsync("/api/session", Ct));
		Assert.Equal("Test Inspector", session.GetProperty("name").GetString());

		using HttpClient other = new HttpClient { BaseAddress = s.Server.BaseAddress };
		HttpRequestMessage again = new HttpRequestMessage(HttpMethod.Post, "/api/pair") { Content = JsonContent.Create(new { token, name = "Other" }) };
		again.Headers.Add(FieldServer.CsrfHeader, "1");
		Assert.Equal(HttpStatusCode.Forbidden, (await other.SendAsync(again, Ct)).StatusCode);
		Assert.Single(s.Server.Pairing.Phones);
	}

	[Fact]
	public async Task Pairing_ByToken_Expires()
	{
		await using PhoneSession s = new PhoneSession();
		string token = s.Server.Pairing.Current.Token;
		s.Now += TimeSpan.FromMinutes(11);
		HttpResponseMessage res = await s.Post("/api/pair", new { token, name = "Test Inspector" });
		Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
		Assert.Contains("expired", await PhoneSession.Error(res));
	}

	[Fact]
	public async Task Pairing_ByPin_LocksAfterFiveWrongTries()
	{
		await using PhoneSession s = new PhoneSession();
		string pin = s.Server.Pairing.Current.Pin;
		string wrong = pin == "123456" ? "654321" : "123456";
		for (int i = 0; i < 4; i++)
		{
			Assert.Equal(HttpStatusCode.Forbidden, (await s.Post("/api/pair", new { pin = wrong, name = "Test Inspector" })).StatusCode);
		}
		Assert.Equal(HttpStatusCode.TooManyRequests, (await s.Post("/api/pair", new { pin = wrong, name = "Test Inspector" })).StatusCode);
		Assert.Equal(HttpStatusCode.TooManyRequests, (await s.Post("/api/pair", new { pin, name = "Test Inspector" })).StatusCode);
		s.Now += TimeSpan.FromMinutes(10);
		Assert.Equal(HttpStatusCode.OK, (await s.Post("/api/pair", new { pin = s.Server.Pairing.Current.Pin, name = "Test Inspector" })).StatusCode);
	}

	[Fact]
	public async Task Changes_NeedTheCsrfHeader()
	{
		await using PhoneSession s = new PhoneSession();
		// Even pairing needs it.
		Assert.Equal(HttpStatusCode.Forbidden, (await s.Post("/api/pair", new { token = s.Server.Pairing.Current.Token, name = "X" }, csrf: false)).StatusCode);
		await s.PairAsync();
		HttpResponseMessage res = await s.Post($"/api/assessments/{s.AssessmentId}/changes",
			new { changes = new[] { new { key = "Property.City", value = "Elsewhere" } } }, csrf: false);
		Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await s.Upload(PhoneSession.Jpeg(100), "a.jpg", "image/jpeg", csrf: false)).StatusCode);
		Assert.Equal("Testville", s.Store.GetProperty(s.Assessment.Property.PropertyID).City);
	}

	[Fact]
	public async Task OtherHostNames_AreRefused()
	{
		await using PhoneSession s = new PhoneSession();
		HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, "/");
		req.Headers.Host = "rebind.example.com";
		Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.SendAsync(req, Ct)).StatusCode);
	}

	[Fact]
	public async Task FieldChanges_AreSaved_WithPhoneInHistory()
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync("Test Inspector");
		HttpResponseMessage res = await s.Post($"/api/assessments/{s.AssessmentId}/changes", new
		{
			changes = new[]
			{
				new { key = "Assessment.CauseofDamageID", value = LookupIds.CauseFlood.ToString() },
				new { key = "Assessment.DepthOfFloodAboveFFE", value = "2.5" },
				new { key = "Property.City", value = "Elsewhere" },
			},
		});
		Assert.Equal(HttpStatusCode.OK, res.StatusCode);
		JsonElement view = await PhoneSession.Json(res);
		List<string> keys = view.GetProperty("steps").EnumerateArray().SelectMany(st => st.GetProperty("fields").EnumerateArray())
			.Select(f => f.GetProperty("key").GetString()).ToList();
		Assert.Contains("Assessment.DepthOfFloodAboveFFE", keys);   // applies now that the cause is flood

		AssessmentFile saved = s.Store.LoadAssessment(s.AssessmentId);
		Assert.Equal(2.5m, saved.Assessment.DepthOfFloodAboveFFE);
		Assert.Equal("Elsewhere", saved.Property.City);
		List<EditHistoryRecord> history = s.Store.GetHistory(s.AssessmentId).Where(h => !h.IsEvent).ToList();
		EditHistoryRecord city = history.Single(h => h.Field == "Property.City");
		Assert.Equal(SaveVia.Phone, city.Via);
		Assert.Equal("Test Inspector", city.ChangedBy);
		Assert.Equal("Elsewhere", city.NewValue);
	}

	[Fact]
	public async Task BadValues_AreRefused_InPlainEnglish_AndNothingIsSaved()
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		HttpResponseMessage res = await s.Change("Property.YearOfConstruction", "nineteen fifty");
		Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
		Assert.Equal("Year built: type a number.", await PhoneSession.Error(res));
		Assert.Equal(HttpStatusCode.BadRequest, (await s.Change("Property.FoundationTypeID", "999999")).StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, (await s.Change("Property.Nonexistent", "1")).StatusCode);
		// Doesn't apply: flood depth when the cause isn't flood.
		HttpResponseMessage na = await s.Change("Assessment.DepthOfFloodAboveFFE", "3");
		Assert.Equal(HttpStatusCode.BadRequest, na.StatusCode);
		Assert.Contains("doesn't apply", await PhoneSession.Error(na));
		Assert.Null(s.Store.LoadAssessment(s.AssessmentId).Property.YearOfConstruction);
	}

	[Fact]
	public async Task LockedAssessment_Returns409_WithThePlainEnglishMessage()
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		s.Store.SetStatus(s.AssessmentId, AssessmentStatus.Final);
		HttpResponseMessage res = await s.Change("Property.City", "Elsewhere");
		Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
		string error = await PhoneSession.Error(res);
		Assert.Contains("Final", error);
		Assert.Contains("Reopen it first", error);
		Assert.Equal(HttpStatusCode.Conflict, (await s.Upload(PhoneSession.Jpeg(100), "a.jpg", "image/jpeg")).StatusCode);
		JsonElement view = await PhoneSession.Json(await s.Client.GetAsync($"/api/assessments/{s.AssessmentId}", Ct));
		Assert.True(view.GetProperty("locked").GetBoolean());
		Assert.Equal("Testville", s.Store.GetProperty(s.Assessment.Property.PropertyID).City);
	}

	[Fact]
	public async Task Photo_IsAdded_WithHistory()
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		HttpResponseMessage res = await s.Upload(PhoneSession.Jpeg(4096), "IMG_0001.JPG", "image/jpeg");
		Assert.Equal(HttpStatusCode.OK, res.StatusCode);
		Assert.Equal(1, (await PhoneSession.Json(res)).GetProperty("photoCount").GetInt32());
		AttachmentRecord a = Assert.Single(s.Store.LoadAssessment(s.AssessmentId).Attachments);
		Assert.EndsWith(".jpg", a.FileName);
		Assert.Equal("Front of house", a.Description);
		Assert.Equal(4096, new FileInfo(s.Store.PhotoPath(a)).Length);
		Assert.Contains(s.Store.GetHistory(s.AssessmentId), h => h.Via == SaveVia.Phone && h.Field == AssessmentDiff.PhotoKey);
		Assert.Empty(s.Converter.Converted);
	}

	[Fact]
	public async Task Heic_GoesThroughTheConverter()
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		HttpResponseMessage res = await s.Upload(PhoneSession.Heic(2048), "IMG_0002.HEIC", "image/heic");
		Assert.Equal(HttpStatusCode.OK, res.StatusCode);
		Assert.Single(s.Converter.Converted);
		AttachmentRecord a = Assert.Single(s.Store.LoadAssessment(s.AssessmentId).Attachments);
		Assert.EndsWith(".jpg", a.FileName);
		// The upload and the converted copy are cleaned up.
		Assert.Empty(Directory.GetFiles(Path.Combine(s.Db.Folder, "phone-temp")));
	}

	[Fact]
	public async Task Upload_OnlyTakesPhotos_UpTo25MB()
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		HttpResponseMessage tooBig = await s.Upload(PhoneSession.Jpeg((int)PhotoUpload.MaxBytes + 1), "big.jpg", "image/jpeg");
		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
		Assert.Contains("25 MB", await PhoneSession.Error(tooBig));

		// Says it's an image but isn't one.
		HttpResponseMessage fake = await s.Upload(Encoding.UTF8.GetBytes("<script>alert(1)</script>"), "x.jpg", "image/jpeg");
		Assert.Equal(HttpStatusCode.UnsupportedMediaType, fake.StatusCode);
		// A real JPEG sent as something else.
		Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await s.Upload(PhoneSession.Jpeg(100), "x.pdf", "application/pdf")).StatusCode);
		Assert.Empty(s.Store.LoadAssessment(s.AssessmentId).Attachments);
	}

	[Fact]
	public async Task Lookups_HaveEveryField_WithLabelsKindsAndChoices()
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		JsonElement lookups = await PhoneSession.Json(await s.Client.GetAsync("/api/lookups", Ct));
		Assert.Equal(8, lookups.GetProperty("steps").GetArrayLength());
		List<JsonElement> fields = lookups.GetProperty("fields").EnumerateArray().ToList();
		Assert.Equal(FieldCatalog.All.Count, fields.Count);
		JsonElement cause = fields.Single(f => f.GetProperty("key").GetString() == "Assessment.CauseofDamageID");
		Assert.Equal("Cause of damage", cause.GetProperty("label").GetString());
		Assert.Equal("lookup", cause.GetProperty("kind").GetString());
		Assert.Contains(cause.GetProperty("options").EnumerateArray(), o => o.GetProperty("id").GetString() == LookupIds.CauseFlood.ToString());
		JsonElement year = fields.Single(f => f.GetProperty("key").GetString() == "Property.YearOfConstruction");
		Assert.Equal("number", year.GetProperty("kind").GetString());
		Assert.Equal(0, year.GetProperty("options").GetArrayLength());
		JsonElement type = fields.Single(f => f.GetProperty("key").GetString() == "Property.StructureTypeID");
		Assert.Equal(2, type.GetProperty("options").GetArrayLength());
	}

	[Fact]
	public async Task Assessment_ShowsOnlyFieldsThatApply()
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		JsonElement view = await PhoneSession.Json(await s.Client.GetAsync($"/api/assessments/{s.AssessmentId}", Ct));
		Assert.Equal("101 Sample Road", view.GetProperty("address").GetString());
		List<string> keys = view.GetProperty("steps").EnumerateArray().SelectMany(st => st.GetProperty("fields").EnumerateArray())
			.Select(f => f.GetProperty("key").GetString()).ToList();
		Assert.Contains("Property.FoundationTypeID", keys);          // residential
		Assert.DoesNotContain("Property.StructureUseID", keys);      // non-residential only
		Assert.DoesNotContain("Assessment.DepthOfFlood", keys);      // flood only
		Assert.Equal(HttpStatusCode.NotFound, (await s.Client.GetAsync($"/api/assessments/{Guid.NewGuid()}", Ct)).StatusCode);
	}

	[Fact]
	public async Task Search_AndAddStructure()
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync("Test Inspector");
		JsonElement found = await PhoneSession.Json(await s.Client.GetAsync("/api/structures?q=p-0001", Ct));
		Assert.Equal(s.AssessmentId.ToString(), Assert.Single(found.EnumerateArray()).GetProperty("assessmentId").GetString());
		Assert.Empty((await PhoneSession.Json(await s.Client.GetAsync("/api/structures?q=nowhere", Ct))).EnumerateArray());

		HttpResponseMessage res = await s.Post("/api/structures", new
		{
			structureType = 2,
			changes = new[]
			{
				new { key = "Property.StreetNumber", value = "202" },
				new { key = "Property.StreetName", value = "Example Avenue" },
			},
		});
		Assert.Equal(HttpStatusCode.OK, res.StatusCode);
		JsonElement view = await PhoneSession.Json(res);
		Assert.Equal("Non-residential", view.GetProperty("structureType").GetString());
		Guid id = Guid.Parse(view.GetProperty("assessmentId").GetString());
		AssessmentFile added = s.Store.LoadAssessment(id);
		Assert.Equal("Example Avenue", added.Property.StreetName);
		EditHistoryRecord created = Assert.Single(s.Store.GetHistory(id), h => h.IsEvent);
		Assert.Equal(SaveVia.Phone, created.Via);
		Assert.Equal("Test Inspector", created.ChangedBy);
		Assert.Single((await PhoneSession.Json(await s.Client.GetAsync("/api/structures?q=example", Ct))).EnumerateArray());

		Assert.Equal(HttpStatusCode.BadRequest, (await s.Post("/api/structures", new { structureType = 3 })).StatusCode);
	}

	[Fact]
	public async Task Revoked_Phone_IsLockedOut()
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync("/api/session", Ct)).StatusCode);
		s.Server.Pairing.Revoke(s.Server.Pairing.Phones.Single().Id);
		Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.GetAsync("/api/session", Ct)).StatusCode);
	}

	[Fact]
	public async Task Server_StopsItself_WhenItsTimeIsUp()
	{
		await using PhoneSession s = new PhoneSession(lifetime: TimeSpan.FromMilliseconds(300));
		TaskCompletionSource<string> stopped = new TaskCompletionSource<string>();
		s.Server.Stopped += r => stopped.TrySetResult(r);
		string reason = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
		Assert.Contains("12 hours", reason);
		Assert.False(s.Server.IsRunning);
	}

	[Fact]
	public void Server_NeverListensOnEveryAddress_OrAPublicOne()
	{
		using TempDatabase db = new TempDatabase();
		FieldServer server = new FieldServer(() => db.Store, new DirectFieldWriter(() => db.Store));
		Assert.Throws<ArgumentException>(() => server.Start(new FieldServerOptions { BindAddress = IPAddress.Any }));
		Assert.Throws<ArgumentException>(() => server.Start(new FieldServerOptions { BindAddress = IPAddress.Parse("8.8.8.8") }));
		Assert.False(server.IsRunning);
	}
}
