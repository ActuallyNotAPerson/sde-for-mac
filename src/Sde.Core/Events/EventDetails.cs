namespace Sde.Core.Events;

/// <summary>
/// What is known about a disaster event (from the Mac-only Event table): its name, kind, disaster declaration number
/// and dates. The event summary and its PDF show them.
/// </summary>
public sealed record EventDetails(string Name, string Kind, string DisasterNumber, DateTime? Start, DateTime? End)
{
	/// <summary>"Sep 14, 2026 to Sep 20, 2026", "From Sep 14, 2026", or null when no dates are set.</summary>
	public string DateRange => (Start, End) switch
	{
		(DateTime s, DateTime e) when s.Date == e.Date => s.ToString("MMM d, yyyy"),
		(DateTime s, DateTime e) => $"{s:MMM d, yyyy} to {e:MMM d, yyyy}",
		(DateTime s, null) => $"From {s:MMM d, yyyy}",
		(null, DateTime e) => $"Until {e:MMM d, yyyy}",
		_ => null,
	};
}
