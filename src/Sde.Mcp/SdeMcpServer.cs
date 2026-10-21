using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Sde.Mcp;

/// <summary>How to run the server. The app fills in the database and its log folder; tests also give the streams.</summary>
public sealed class SdeMcpOptions
{
	/// <summary>The database to serve (<c>--db</c>).</summary>
	public string DatabasePath { get; init; }

	/// <summary>SDE's log folder; the server writes sde-mcp-yyyy-MM-dd.log there. Null writes no log file.</summary>
	public string LogFolder { get; init; }

	/// <summary>The running program's path, checked for macOS App Translocation.</summary>
	public string ProcessPath { get; init; } = Environment.ProcessPath;

	/// <summary>Where requests come from; null is stdin.</summary>
	public Stream Input { get; init; }

	/// <summary>Where responses go; null is stdout.</summary>
	public Stream Output { get; init; }

	/// <summary>How much goes to stderr. The assistant keeps stderr as the server's log, so the default is everything.</summary>
	public LogLevel ConsoleLogLevel { get; init; } = LogLevel.Trace;
}

/// <summary>
/// <c>SDE mcp --db &lt;path&gt;</c>: SDE's Model Context Protocol server for AI assistants, over stdin and stdout.
/// stdout carries only JSON-RPC messages; everything else (logs, stray Console output) goes to stderr, which Claude
/// Desktop keeps in ~/Library/Logs/Claude/mcp-server-sde.log.
/// </summary>
public static class SdeMcpServer
{
	/// <summary>The command-line verb: <c>SDE mcp --db &lt;path&gt;</c>.</summary>
	public const string Command = "mcp";

	/// <summary>The server's name as assistants see it.</summary>
	public const string ServerName = "sde";

	/// <summary>
	/// How results are written: the SDK's defaults (camelCase, empty values left out), but with ×, ÷, ' and &amp; written
	/// as themselves, since the text goes to an assistant, not into a web page.
	/// </summary>
	public static readonly JsonSerializerOptions Json = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	public const string Usage = "Usage: SDE mcp --db <path to the SDE database>\nRuns SDE's server for AI assistants (Claude Desktop, Claude Code) on stdin and stdout. SDE's Settings › AI assistants has the exact setup to copy.";

	/// <summary>
	/// Runs the server until the assistant disconnects. <paramref name="args"/> are the arguments after "mcp". Returns
	/// the process exit code: 0 after a normal end, 2 for wrong arguments.
	/// </summary>
	public static int Run(string[] args, string logFolder)
	{
		// The transport writes JSON-RPC to the real stdout itself (it opens the stream directly). From here on, anything
		// else written to Console.Out goes to stderr, so nothing can corrupt the protocol.
		Console.SetOut(Console.Error);
		if (!TryParseArguments(args, out string database, out string error))
		{
			Console.Error.WriteLine(error);
			Console.Error.WriteLine(Usage);
			return 2;
		}
		AppDomain.CurrentDomain.UnhandledException += (_, e) => Console.Error.WriteLine("SDE's AI assistant server stopped unexpectedly: " + e.ExceptionObject);
		try
		{
			return RunAsync(new SdeMcpOptions { DatabasePath = database, LogFolder = logFolder }).GetAwaiter().GetResult();
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine("SDE's AI assistant server stopped: " + ex);
			return 1;
		}
	}

	/// <summary>Reads <c>--db &lt;path&gt;</c> (or <c>--db=&lt;path&gt;</c>).</summary>
	public static bool TryParseArguments(string[] args, out string database, out string error)
	{
		database = null;
		error = null;
		for (int i = 0; i < (args?.Length ?? 0); i++)
		{
			string a = args[i];
			if (a is "--db" or "-db")
			{
				if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
				{
					error = "--db needs the path of an SDE database.";
					return false;
				}
				database = args[++i];
			}
			else if (a.StartsWith("--db=", StringComparison.Ordinal))
			{
				database = a["--db=".Length..];
			}
			else if (a is "--help" or "-h")
			{
				error = "";
				return false;
			}
			else
			{
				error = $"SDE mcp doesn't know \"{a}\".";
				return false;
			}
		}
		if (string.IsNullOrWhiteSpace(database))
		{
			error = "SDE mcp needs --db and the path of the SDE database to serve.";
			return false;
		}
		database = ExpandHome(database.Trim());
		return true;
	}

	private static string ExpandHome(string path) =>
		path == "~" || path.StartsWith("~/", StringComparison.Ordinal)
			? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "")
			: path;

	/// <summary>Opens the database and serves it until the assistant disconnects or <paramref name="cancellationToken"/> fires.</summary>
	public static async Task<int> RunAsync(SdeMcpOptions options, CancellationToken cancellationToken = default)
	{
		using SdeMcpContext context = SdeMcpContext.Open(options.DatabasePath, options.ProcessPath);
		using IHost host = BuildHost(context, options);
		ILogger log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Sde.Mcp");
		log.LogInformation("SDE {Version} AI assistant server (MCP) for {Database}, process {Process}", Version, context.DatabasePath, options.ProcessPath);
		if (context.Problem != null)
		{
			log.LogError("This database can't be served; every tool will say so: {Problem}", context.Problem);
		}
		else
		{
			log.LogInformation("AI edits are {State} for this database (Settings › AI assistants)", context.EditsAllowed ? "on" : "off");
		}
		if (context.Warning != null)
		{
			log.LogWarning("{Warning}", context.Warning);
		}
		log.LogTrace("Loaded assemblies: {Assemblies}", string.Join(", ", AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).OrderBy(n => n)));
		await host.RunAsync(cancellationToken).ConfigureAwait(false);
		log.LogInformation("The assistant disconnected; the server has stopped.");
		return 0;
	}

	/// <summary>The generic host with logging to stderr (and the log file) and the MCP server with SDE's tools.</summary>
	internal static IHost BuildHost(SdeMcpContext context, SdeMcpOptions options)
	{
		HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
		{
			// No appsettings.json, environment or command-line configuration, and no default log providers (one of
			// them writes to stdout).
			DisableDefaults = true,
			ApplicationName = "SDE",
			ContentRootPath = AppContext.BaseDirectory,
		});
		builder.Logging.ClearProviders();
		builder.Logging.SetMinimumLevel(LogLevel.Trace);
		builder.Logging.AddFilter<ConsoleLoggerProvider>(level => level >= options.ConsoleLogLevel);
		builder.Logging.AddSimpleConsole(o =>
		{
			o.SingleLine = true;
			o.TimestampFormat = "HH:mm:ss ";
			o.ColorBehavior = LoggerColorBehavior.Disabled;
		});
		builder.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
		if (!string.IsNullOrEmpty(options.LogFolder))
		{
			builder.Logging.AddProvider(new FileLoggerProvider(options.LogFolder));
		}
		builder.Services.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = true);
		builder.Services.AddSingleton(context);

		IMcpServerBuilder mcp = builder.Services.AddMcpServer(o =>
		{
			o.ServerInfo = new Implementation { Name = ServerName, Title = "SDE for Mac", Version = Version };
			o.ServerInstructions = context.Instructions;
		});
		if (options.Input != null || options.Output != null)
		{
			mcp.WithStreamServerTransport(options.Input ?? Console.OpenStandardInput(), options.Output ?? Console.OpenStandardOutput());
		}
		else
		{
			mcp.WithStdioServerTransport();
		}
		Assembly tools = typeof(SdeMcpServer).Assembly;
		mcp.WithToolsFromAssembly(tools, Json).WithResourcesFromAssembly(tools);
		mcp.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, cancellationToken) =>
		{
			try
			{
				return await next(request, cancellationToken).ConfigureAwait(false);
			}
			catch (McpException ex) when (ex is not McpProtocolException)
			{
				// A refusal the tool explains itself (edits off, a locked assessment, a value that doesn't fit): an
				// ordinary error result in the tool's own words, not an unhandled exception in the logs.
				ILogger log = request.Services?.GetService<ILoggerFactory>()?.CreateLogger("Sde.Mcp");
				log?.LogInformation("{Tool} refused: {Message}", request.Params?.Name, ex.Message);
				return Refusal(ex.Message);
			}
			catch (Microsoft.Data.Sqlite.SqliteException ex)
			{
				ILogger log = request.Services?.GetService<ILoggerFactory>()?.CreateLogger("Sde.Mcp");
				log?.LogWarning(ex, "{Tool} couldn't use the database", request.Params?.Name);
				return Refusal($"SDE couldn't use the database just now ({ex.Message}). Try again in a moment.");
			}
		}));
		return builder.Build();
	}

	/// <summary>A tool result that says why nothing was done.</summary>
	private static CallToolResult Refusal(string message) => new CallToolResult
	{
		IsError = true,
		Content = [new TextContentBlock { Text = message }],
	};

	/// <summary>SDE's version ("2.5.0"), from the app that runs the server.</summary>
	public static string Version
	{
		get
		{
			Version v = (Assembly.GetEntryAssembly() ?? typeof(SdeMcpServer).Assembly).GetName().Version ?? new Version(0, 0, 0);
			return $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
		}
	}
}
