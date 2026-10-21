using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Sde.Core.Assessments;
using Sde.Data;
using Sde.Mcp;

namespace Sde.Mcp.Tests;

/// <summary>
/// The server running in this process over two in-memory pipes, with the SDK's own client connected to it as
/// "test-assistant". The database is a <see cref="TempDatabase"/> (or one given), which the test can also change
/// directly through <see cref="Store"/>.
/// </summary>
internal sealed class McpSession : IAsyncDisposable
{
	public const string ClientName = "test-assistant";

	private readonly bool _ownsDatabase;
	private IHost _host;

	private McpSession(TempDatabase db, bool ownsDatabase)
	{
		Db = db;
		_ownsDatabase = ownsDatabase;
	}

	public TempDatabase Db { get; }

	public SdeStore Store => Db.Store;

	public SdeMcpContext Context { get; private set; }

	public McpClient Client { get; private set; }

	/// <summary>Starts a server on <paramref name="db"/> (a new database when null) and connects a client.</summary>
	public static async Task<McpSession> StartAsync(TempDatabase db = null, bool allowEdits = false, string processPath = "/Applications/SDE.app/Contents/MacOS/SDE")
	{
		McpSession session = new McpSession(db ?? new TempDatabase(), db == null);
		if (allowEdits)
		{
			AiAssistantSettings.SetAllowEdits(session.Store, true);
		}
		await session.ConnectAsync(processPath);
		return session;
	}

	private async Task ConnectAsync(string processPath)
	{
		Pipe toServer = new Pipe();
		Pipe toClient = new Pipe();
		Context = SdeMcpContext.Open(Db.Database.FilePath, processPath);
		_host = SdeMcpServer.BuildHost(Context, new SdeMcpOptions
		{
			DatabasePath = Db.Database.FilePath,
			ProcessPath = processPath,
			Input = toServer.Reader.AsStream(),
			Output = toClient.Writer.AsStream(),
			ConsoleLogLevel = LogLevel.Warning,
		});
		await _host.StartAsync(TestContext.Current.CancellationToken);
		Client = await McpClient.CreateAsync(
			new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
			new McpClientOptions { ClientInfo = new Implementation { Name = ClientName, Version = "1.0" } },
			cancellationToken: TestContext.Current.CancellationToken);
	}

	/// <summary>Calls a tool and returns its result (which may be an error result).</summary>
	public async Task<CallToolResult> CallAsync(string tool, object args = null)
	{
		Dictionary<string, object> arguments = args == null
			? new Dictionary<string, object>()
			: JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(args));
		return await Client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);
	}

	/// <summary>Calls a tool that must succeed and returns its structured content.</summary>
	public async Task<JsonElement> CallOkAsync(string tool, object args = null)
	{
		CallToolResult result = await CallAsync(tool, args);
		Assert.True(result.IsError != true, $"{tool} failed: {Text(result)}");
		Assert.NotNull(result.StructuredContent);
		return result.StructuredContent.Value;
	}

	/// <summary>Calls a tool that must be refused and returns the message.</summary>
	public async Task<string> CallErrorAsync(string tool, object args = null)
	{
		CallToolResult result = await CallAsync(tool, args);
		Assert.True(result.IsError == true, $"{tool} should have been refused but returned: {Text(result)}");
		return Text(result);
	}

	public static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));

	/// <summary>Disconnects and stops the server, keeping the database (to start another session on it).</summary>
	public async Task StopAsync()
	{
		if (Client != null)
		{
			await Client.DisposeAsync();
			Client = null;
		}
		if (_host != null)
		{
			await _host.StopAsync(TestContext.Current.CancellationToken);
			_host.Dispose();
			_host = null;
		}
		Context?.Dispose();
		Context = null;
	}

	public async ValueTask DisposeAsync()
	{
		await StopAsync();
		if (_ownsDatabase)
		{
			Db.Dispose();
		}
	}
}

/// <summary>Small helpers for reading tool results.</summary>
internal static class Json
{
	public static string Str(this JsonElement e, string name) =>
		e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

	public static decimal? Dec(this JsonElement e, string name) =>
		e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

	public static int Int(this JsonElement e, string name) => e.GetProperty(name).GetInt32();

	public static List<JsonElement> List(this JsonElement e, string name) =>
		e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : new List<JsonElement>();

	/// <summary>The value of a field in a get_assessment result.</summary>
	public static JsonElement Field(this JsonElement assessment, string key) =>
		assessment.List("steps").SelectMany(s => s.List("fields")).Single(f => f.Str("key") == key);

	public static Guid Id(this AssessmentFile f) => f.Assessment.AssessmentID;
}
