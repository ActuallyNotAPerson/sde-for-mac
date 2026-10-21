# SDE for AI assistants (MCP): developer notes

> Draft for developers. The user guide chapter comes with the Settings › AI assistants panel.

SDE includes a [Model Context Protocol](https://modelcontextprotocol.io/) server, so an AI assistant such as Claude
Desktop or Claude Code can read an SDE database and, when allowed, edit it. It is the app itself run with a verb:

```
SDE.app/Contents/MacOS/SDE mcp --db "<path to the database>"
```

It speaks JSON-RPC on stdin and stdout and shows no window or Dock icon (`Program.Main` branches before Avalonia
loads). The code is in `src/Sde.Mcp`, built on the official C# SDK (`ModelContextProtocol`, Apache-2.0).

## Connecting an assistant

**Claude Code**, from a terminal:

```
claude mcp add sde -- "/Applications/SDE.app/Contents/MacOS/SDE" mcp --db "/Users/you/Library/Application Support/SDE/sde.sqlite"
```

Add `--scope user` to make it available in every project.

**Claude Desktop**: Settings, Developer, Edit Config, then add an entry to `claude_desktop_config.json`
(`~/Library/Application Support/Claude/`) and restart Claude Desktop:

```json
{
  "mcpServers": {
    "sde": {
      "command": "/Applications/SDE.app/Contents/MacOS/SDE",
      "args": ["mcp", "--db", "/Users/you/Library/Application Support/SDE/sde.sqlite"]
    }
  }
}
```

Run SDE from the Applications folder. A copy opened straight from Downloads runs from a temporary path (App
Translocation) that changes every time; the server warns about it. Open the database in SDE once before connecting:
the server never upgrades a database's tables and refuses one that needs it.

## What it can do

| Tool | What it does |
| --- | --- |
| `find_structures` | Lists structures with their assessment ID, address, owner, status and result. Filters: text (like the Structures search box), status, structure type, result, % damaged range, has or missing coordinates. Paged. |
| `get_assessment` | Every field that applies, with labels and display values, by editor step; damage by element; result; status; photo count. |
| `explain_result` | How the percent damaged was worked out, in plain English, from a fresh run of FEMA's calculation. |
| `get_history` | The assessment's edit history, newest first. Paged. |
| `get_field_options` | What a field accepts on one assessment, with the choices offered for it now. |
| `field_help` | The editor's field help, with the FEMA P-784 page. |
| `update_fields` | Changes fields through `SdeStore.Apply` and `FieldInput`, checked exactly as the phone form checks them. |
| `set_status` | Draft, Reviewed or Appeal. Final and Letter sent stay a person's decision. |

Resources: `sde://assessment/{id}` and `sde://field-help`. More tools (events, QA, reports, layers) come with the
features they belong to.

## Safety

- Read tools are always on and use read-only SQLite connections.
- Edits work only while the database's `Mcp.AllowEdits` setting is on (`AiAssistantSettings`), read again on every
  call. Each edit call is one `SaveContext.Batch` with `Via = "AI assistant"` and `User = "<client name> (AI)"`, so
  `AiAssistantSettings.RecentChanges` can list it and `AiAssistantSettings.Revert` (fields through
  `SdeStore.RevertBatch`, plus the status) can undo it.
- The first edit of a session takes a backup named "before AI edits".
- Final and Letter sent assessments are refused, and so is reopening one. Nothing can be deleted, restored,
  imported, exported to FEMA's format or marked as letter sent, and Settings can't be changed.
- The server holds a shared lease on the database, so SDE won't restore a backup or move it underneath.
- Tool descriptions and the server's instructions say that free text in the database is data, never instructions.
  Results are paged and long text is shortened to stay under Claude Code's tool-result limit.

## Logs

stdout carries only JSON-RPC; every log line goes to stderr, which Claude Desktop keeps in
`~/Library/Logs/Claude/mcp-server-sde.log`. The server also writes `sde-mcp-yyyy-MM-dd.log` (information and up) to
SDE's log folder, `~/Library/Logs/SDE`.

## Testing

`tests/Sde.Mcp.Tests` calls every tool through the SDK's client over in-memory pipes, and spawns the built app's
`SDE mcp` to check that stdout is pure JSON-RPC. By hand, the
[MCP Inspector](https://github.com/modelcontextprotocol/inspector) works too:
`npx @modelcontextprotocol/inspector "/Applications/SDE.app/Contents/MacOS/SDE" mcp --db <scratch database>`.
