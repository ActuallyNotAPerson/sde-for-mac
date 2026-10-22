using Sde.Data;

namespace Sde.App.Services;

/// <summary>
/// The event chosen in the sidebar's switcher, which scopes Structures, the Map, the Dashboard, exports, letters and
/// the web map to that event's assessments. "All events" (no event) shows everything. The choice is remembered per
/// database (MacSetting <see cref="SettingKey"/>).
/// </summary>
public sealed class EventScope
{
	/// <summary>The per-database setting that remembers the chosen event.</summary>
	public const string SettingKey = "Ui.Event";

	private HashSet<Guid> _members;

	/// <summary>The chosen event's ID, or null for all events.</summary>
	public Guid? EventId => Event?.EventID;

	/// <summary>The chosen event, or null for all events.</summary>
	public EventRecord Event { get; private set; }

	public bool IsAll => Event == null;

	/// <summary>Raised after the scope changes (another event, or the database moved under it).</summary>
	public event Action Changed;

	/// <summary>True when the assessment is in the chosen event (always, for all events). A structure with no assessment is in no event.</summary>
	public bool Includes(Guid? assessmentId) => _members == null || (assessmentId is Guid id && _members.Contains(id));

	/// <summary>Chooses an event (null: all events), remembers it for this database and raises <see cref="Changed"/>.</summary>
	public void Set(SdeStore store, Guid? eventId)
	{
		Load(store, eventId);
		try
		{
			store.SetSetting(SettingKey, EventId?.ToString("D"));
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Couldn't remember the chosen event", ex);
		}
		Changed?.Invoke();
	}

	/// <summary>
	/// Takes up the event remembered for a database that was just opened (all events when there is none). Raises
	/// nothing: opening a database already reloads every page.
	/// </summary>
	public void Restore(SdeStore store)
	{
		string saved = store.GetSetting<string>(SettingKey);
		Load(store, Guid.TryParse(saved, out Guid id) ? id : null);
	}

	/// <summary>
	/// Reads the chosen event's members again after the data changed (an assessment added to it or taken out, the
	/// event renamed or deleted). Returns true when the scope itself changed.
	/// </summary>
	public bool Refresh(SdeStore store)
	{
		Guid? before = EventId;
		int count = _members?.Count ?? -1;
		Load(store, before);
		return before != EventId || count != (_members?.Count ?? -1);
	}

	/// <summary>
	/// The active assessments an "all" export should cover: null (every one) for all events, else the chosen event's.
	/// </summary>
	public IReadOnlyList<Guid> ExportIds(SdeStore store) => IsAll ? null
		: store.ListStructures().Where(s => s.AssessmentID != null && Includes(s.AssessmentID)).Select(s => s.AssessmentID!.Value).ToList();

	/// <summary>"" for all events; otherwise a sentence saying exports cover only the chosen event.</summary>
	public string ExportNote => IsAll ? "" : $"Only {Event.Label} is exported, because it is chosen in the sidebar. Choose All events to export everything.";

	private void Load(SdeStore store, Guid? eventId)
	{
		Event = eventId is Guid id ? store.GetEvent(id) : null;
		_members = Event == null ? null : store.AssessmentsInEvent(Event.EventID);
	}
}
