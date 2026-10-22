using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Dapper;
using Sde.Data;
using Sde.Mcp;

namespace Sde.Mcp.Tests;

/// <summary>
/// The server as a program: arguments, databases it refuses, and the real "SDE mcp" process, whose stdout must carry
/// nothing but JSON-RPC while its logs go to stderr.
/// </summary>
public sealed class ServerTests
{
	[Theory]
	[InlineData("--db /tmp/a.sqlite", "/tmp/a.sqlite")]
	[InlineData("--db=/tmp/b.sqlite", "/tmp/b.sqlite")]
	public void Reads_the_database_argument(string args, string expected)
	{
		Assert.True(SdeMcpServer.TryParseArguments(args.Split(' '), out string db, out string error), error);
		Assert.Equal(expected, db);
	}

	[Theory]
	[InlineData("")]
	[InlineData("--db")]
	[InlineData("--database x")]
	public void Refuses_missing_or_unknown_arguments(string args)
	{
		Assert.False(SdeMcpServer.TryParseArguments(args.Split(' ', StringSplitOptions.RemoveEmptyEntries), out _, out string error));
		Assert.False(string.IsNullOrWhiteSpace(error));
	}

	[Theory]
	[InlineData("claude-code", "claude-code")]
	[InlineData("claude-ai", "claude-ai")]
	[InlineData(null, "AI assistant")]
	[InlineData("  ", "AI assistant")]
	[InlineData("Evil\n\"name\"; DROP TABLE", "Evilname DROP TABLE")]
	public void Names_the_assistant_plainly(string name, string expected) => Assert.Equal(expected, EditTools.ClientName(name));

	[Fact]
	public void Gives_the_setup_to_copy_for_claude()
	{
		string app = "/Applications/SDE.app/Contents/MacOS/SDE";
		string db = "/Users/pat/Library/Application Support/SDE/sde.sqlite";
		Assert.Equal("claude mcp add sde -- \"/Applications/SDE.app/Contents/MacOS/SDE\" mcp --db \"/Users/pat/Library/Application Support/SDE/sde.sqlite\"",
			AiAssistantSettings.ClaudeCodeCommand(app, db));
		Assert.Equal("claude mcp add sde -- \"/a\" mcp --db \"/b/\\\"odd\\\" \\$name\"", AiAssistantSettings.ClaudeCodeCommand("/a", "/b/\"odd\" $name"));
		using JsonDocument config = JsonDocument.Parse(AiAssistantSettings.ClaudeDesktopConfig(app, db));
		JsonElement sde = config.RootElement.GetProperty("mcpServers").GetProperty("sde");
		Assert.Equal(app, sde.GetProperty("command").GetString());
		Assert.Equal(new[] { "mcp", "--db", db }, sde.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
	}

	[Fact]
	public async Task A_database_that_needs_an_upgrade_is_refused_and_left_alone()
	{
		using TempDatabase db = new TempDatabase();
		MakeOld(db);
		Assert.True(SdeDatabase.NeedsUpgrade(db.Database.FilePath));
		await using (McpSession s = await McpSession.StartAsync(db, allowEdits: false))
		{
			Assert.NotNull(s.Context.Problem);
			Assert.Contains("Open this database in SDE once first, then reconnect.", s.Client.ServerInstructions);
			Assert.Contains("Open this database in SDE once first", await s.CallErrorAsync("find_structures"));
			Assert.Contains("Open this database in SDE once first", await s.CallErrorAsync("set_status", new { assessmentId = Guid.NewGuid().ToString(), status = "Reviewed" }));
		}
		Assert.True(SdeDatabase.NeedsUpgrade(db.Database.FilePath));
	}

	[Fact]
	public void A_missing_database_is_named_in_the_refusal()
	{
		using TempDatabase db = new TempDatabase();
		string path = Path.Combine(db.Folder, "nothing-here.sqlite");
		SdeMcpContext context = SdeMcpContext.Open(path, "/Applications/SDE.app/Contents/MacOS/SDE");
		Assert.Contains(path, context.Problem);
		Assert.False(File.Exists(path));
		context.Dispose();
	}

	[Fact]
	public async Task Running_from_app_translocation_is_warned_about()
	{
		await using McpSession s = await McpSession.StartAsync(processPath: "/private/var/folders/x/AppTranslocation/ABC/d/SDE.app/Contents/MacOS/SDE");
		Assert.Contains("App Translocation", s.Client.ServerInstructions);
		Assert.Null(SdeMcpContext.TranslocationWarning("/Applications/SDE.app/Contents/MacOS/SDE"));
	}

	[Fact]
	public async Task The_real_process_writes_only_json_rpc_to_stdout()
	{
		using TempDatabase db = new TempDatabase();
		SampleCases.RanchFlood(db.Store);
		ProcessRun run = await RunServerAsync(db.Database.FilePath,
			"""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"process-test","version":"1.0"}}}""",
			"""{"jsonrpc":"2.0","method":"notifications/initialized"}""",
			"""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
			"""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"find_structures","arguments":{"query":"river"}}}""",
			"""{"jsonrpc":"2.0","id":4,"method":"prompts/list"}""",
			"""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"qa_check","arguments":{}}}""");

		Assert.Equal(0, run.ExitCode);
		Assert.NotEmpty(run.StdoutLines);
		foreach (string line in run.StdoutLines)
		{
			using JsonDocument doc = JsonDocument.Parse(line);
			Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
		}
		JsonElement init = run.Response(1);
		Assert.Equal("sde", init.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
		Assert.Contains(db.Database.FilePath, init.GetProperty("result").GetProperty("instructions").GetString());
		Assert.Equal(ReadToolTests.ReadOnlyTools.Length + ReadToolTests.OutputTools.Length + ReadToolTests.EditTools.Length,
			run.Response(2).GetProperty("result").GetProperty("tools").GetArrayLength());
		JsonElement found = run.Response(3).GetProperty("result").GetProperty("structuredContent");
		Assert.Equal(1, found.GetProperty("total").GetInt32());
		Assert.Equal(3, run.Response(4).GetProperty("result").GetProperty("prompts").GetArrayLength());
		Assert.True(run.Response(5).GetProperty("result").GetProperty("structuredContent").GetProperty("total").GetInt32() > 0);

		// The logs went to stderr and to SDE's log folder, and the app never loaded its windows (no Dock icon).
		Assert.Contains("AI assistant server (MCP)", run.Stderr);
		Assert.Contains("Loaded assemblies:", run.Stderr);
		Assert.DoesNotContain("Avalonia", run.Stderr.Split('\n').Single(l => l.Contains("Loaded assemblies:")));
		string log = Assert.Single(Directory.GetFiles(Path.Combine(db.Folder, "Logs"), "sde-mcp-*.log"));
		Assert.Contains("AI assistant server (MCP)", File.ReadAllText(log));
	}

	[Fact]
	public async Task The_real_process_keeps_stdout_pure_while_it_makes_outputs()
	{
		using TempDatabase db = new TempDatabase();
		Guid id = SampleCases.RanchFlood(db.Store).Assessment.AssessmentID;
		ProcessRun run = await RunServerAsync(db.Database.FilePath,
			"""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"process-test","version":"1.0"}}}""",
			"""{"jsonrpc":"2.0","method":"notifications/initialized"}""",
			"""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"make_report","arguments":{"assessmentIds":["ID"],"kind":"detailed"}}}""".Replace("ID", id.ToString()),
			"""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"make_web_map","arguments":{"privacy":"public"}}}""",
			"""{"jsonrpc":"2.0","id":4,"method":"prompts/get","params":{"name":"owner_letter_draft","arguments":{"assessmentId":"ID"}}}""".Replace("ID", id.ToString()));

		Assert.Equal(0, run.ExitCode);
		foreach (string line in run.StdoutLines)
		{
			using JsonDocument doc = JsonDocument.Parse(line);
			Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
		}
		string pdf = run.Response(2).GetProperty("result").GetProperty("structuredContent").GetProperty("path").GetString();
		Assert.Equal(Path.Combine(db.Folder, "AI outputs"), Path.GetDirectoryName(pdf));
		Assert.Equal("%PDF", Encoding.ASCII.GetString(File.ReadAllBytes(pdf), 0, 4));
		Assert.True(File.Exists(run.Response(3).GetProperty("result").GetProperty("structuredContent").GetProperty("indexPath").GetString()));
		Assert.Contains("123 River Street", run.Response(4).GetProperty("result").GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString());
	}

	[Fact]
	public async Task The_real_process_refuses_a_database_that_needs_an_upgrade()
	{
		using TempDatabase db = new TempDatabase();
		MakeOld(db);
		ProcessRun run = await RunServerAsync(db.Database.FilePath,
			"""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"process-test","version":"1.0"}}}""",
			"""{"jsonrpc":"2.0","method":"notifications/initialized"}""",
			"""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"find_structures","arguments":{}}}""");
		Assert.Equal(0, run.ExitCode);
		JsonElement result = run.Response(2).GetProperty("result");
		Assert.True(result.GetProperty("isError").GetBoolean());
		Assert.Contains("Open this database in SDE once first, then reconnect.", result.GetProperty("content")[0].GetProperty("text").GetString());
		Assert.Contains("Open this database in SDE once first", run.Stderr);
		Assert.True(SdeDatabase.NeedsUpgrade(db.Database.FilePath));
	}

	[Fact]
	public async Task The_real_process_explains_a_missing_database_argument()
	{
		using TempDatabase db = new TempDatabase();
		ProcessRun run = await RunAsync(new[] { SdeMcpServer.Command }, Array.Empty<string>(), db.Database.FilePath);
		Assert.Equal(2, run.ExitCode);
		Assert.Empty(run.StdoutLines);
		Assert.Contains("--db", run.Stderr);
	}

	/// <summary>Leaves the database as an older SDE left it, so opening it would update its tables.</summary>
	private static void MakeOld(TempDatabase db) =>
		db.Database.InTransaction((c, tx) => c.Execute("UPDATE SchemaInfo SET Value = '2' WHERE Key = 'MacSchemaVersion'", transaction: tx));

	private sealed record ProcessRun(int ExitCode, List<string> StdoutLines, string Stderr)
	{
		public JsonElement Response(int id) => StdoutLines.Select(l => JsonDocument.Parse(l).RootElement)
			.Single(e => e.TryGetProperty("id", out JsonElement i) && i.ValueKind == JsonValueKind.Number && i.GetInt32() == id);
	}

	private static Task<ProcessRun> RunServerAsync(string database, params string[] messages) =>
		RunAsync(new[] { SdeMcpServer.Command, "--db", database }, messages, database);

	/// <summary>
	/// Runs the built app (src/Sde.App/bin/&lt;configuration&gt;/net10.0/SDE) with <paramref name="args"/>, sends each
	/// message as a line on stdin and waits for its response, then closes stdin and waits for the process to end.
	/// </summary>
	/// <param name="logBeside">SDE writes its logs into a Logs folder beside this database instead of ~/Library/Logs/SDE.</param>
	private static async Task<ProcessRun> RunAsync(string[] args, string[] messages, string logBeside)
	{
		CancellationToken ct = TestContext.Current.CancellationToken;
		string configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
		string app = Path.Combine(TestPaths.Root, "src", "Sde.App", "bin", configuration, "net10.0", OperatingSystem.IsWindows() ? "SDE.exe" : "SDE");
		Assert.True(File.Exists(app), "Build the app first: " + app);
		ProcessStartInfo start = new ProcessStartInfo(app)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			StandardOutputEncoding = new UTF8Encoding(false),
			StandardErrorEncoding = new UTF8Encoding(false),
		};
		foreach (string a in args)
		{
			start.ArgumentList.Add(a);
		}
		// The framework-dependent build finds .NET where this test runs.
		start.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
		start.Environment["SDE_DATABASE"] = logBeside;
		using Process p = Process.Start(start)!;
		StringBuilder stderr = new StringBuilder();
		p.ErrorDataReceived += (_, e) =>
		{
			if (e.Data != null)
			{
				lock (stderr)
				{
					stderr.AppendLine(e.Data);
				}
			}
		};
		p.BeginErrorReadLine();
		string Stderr()
		{
			lock (stderr)
			{
				return stderr.ToString();
			}
		}
		List<string> lines = new List<string>();
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(TimeSpan.FromSeconds(60));
		try
		{
			foreach (string message in messages)
			{
				await p.StandardInput.WriteLineAsync(message.AsMemory(), timeout.Token);
				await p.StandardInput.FlushAsync(timeout.Token);
				using JsonDocument sent = JsonDocument.Parse(message);
				if (!sent.RootElement.TryGetProperty("id", out JsonElement id))
				{
					continue;
				}
				while (true)
				{
					string line = await p.StandardOutput.ReadLineAsync(timeout.Token);
					if (line == null)
					{
						Assert.Fail("The server closed stdout early. stderr:\n" + Stderr());
					}
					lines.Add(line);
					using JsonDocument got = JsonDocument.Parse(line);
					if (got.RootElement.TryGetProperty("id", out JsonElement gotId) && gotId.ValueKind == JsonValueKind.Number && gotId.GetInt32() == id.GetInt32())
					{
						break;
					}
				}
			}
			p.StandardInput.Close();
			string rest = await p.StandardOutput.ReadToEndAsync(timeout.Token);
			lines.AddRange(rest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
			await p.WaitForExitAsync(timeout.Token);
		}
		catch (OperationCanceledException)
		{
			p.Kill(entireProcessTree: true);
			Assert.Fail("The server didn't answer or stop in time. stderr:\n" + Stderr());
		}
		p.WaitForExit();
		return new ProcessRun(p.ExitCode, lines, Stderr());
	}
}
