using System.Security.Cryptography;
using System.Text;

namespace Sde.Field;

/// <summary>The code a phone pairs with: a one-time token (in the QR code) and a 6-digit PIN, valid together for 10 minutes.</summary>
public sealed record PairingCode(string Token, string Pin, DateTime ExpiresUtc);

/// <summary>A phone that has paired. <see cref="Name"/> is what the inspector typed; edit history shows it as the person.</summary>
public sealed class PairedPhone
{
	internal PairedPhone(string name, string remoteAddress, DateTime pairedUtc)
	{
		Name = name;
		RemoteAddress = remoteAddress;
		PairedUtc = pairedUtc;
		LastSeenUtc = pairedUtc;
	}

	public Guid Id { get; } = Guid.NewGuid();

	public string Name { get; }

	public string RemoteAddress { get; }

	public DateTime PairedUtc { get; }

	public DateTime LastSeenUtc { get; internal set; }
}

public enum PairOutcome
{
	Paired,
	/// <summary>Wrong token or PIN, or a missing name.</summary>
	Invalid,
	/// <summary>The right token or PIN, but more than 10 minutes old.</summary>
	Expired,
	/// <summary>Too many wrong PINs; PIN pairing is paused.</summary>
	LockedOut,
}

/// <summary>The result of a pairing attempt: on success, the phone and the session cookie's value.</summary>
public sealed record PairResult(PairOutcome Outcome, string Message, PairedPhone Phone = null, string SessionToken = null);

/// <summary>
/// Pairing and sessions for the phone form. A pairing code (token + PIN) works once and for 10 minutes; a new one is made
/// as soon as it is used or expires. Five wrong PINs pause PIN pairing for 10 minutes (the QR code still works). A
/// paired phone gets a random 256-bit session token; only its SHA-256 hash is kept, and every session ends when the
/// server stops or the Mac revokes the phone. Thread-safe.
/// </summary>
public sealed class PairingManager
{
	public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
	public static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(10);
	public const int MaxPinAttempts = 5;
	public const int MaxPhones = 20;
	public const int MaxNameLength = 40;

	private readonly object _gate = new object();
	private readonly Func<DateTime> _clock;
	private readonly Dictionary<string, PairedPhone> _sessions = new Dictionary<string, PairedPhone>(StringComparer.Ordinal);
	private PairingCode _code;
	private int _wrongPins;
	private DateTime? _lockedUntil;

	/// <param name="clock">The current time in UTC (tests replace it).</param>
	public PairingManager(Func<DateTime> clock = null)
	{
		_clock = clock ?? (() => DateTime.UtcNow);
		_code = NewCode();
	}

	/// <summary>Raised (on any thread) when the pairing code changes or a phone pairs or is revoked.</summary>
	public event Action Changed;

	/// <summary>The code to show on the Mac; a new one once the old one has expired.</summary>
	public PairingCode Current
	{
		get
		{
			bool changed = false;
			PairingCode code;
			lock (_gate)
			{
				if (_clock() >= _code.ExpiresUtc)
				{
					_code = NewCode();
					changed = true;
				}
				code = _code;
			}
			if (changed)
			{
				Changed?.Invoke();
			}
			return code;
		}
	}

	/// <summary>When PIN pairing is paused until (UTC), or null.</summary>
	public DateTime? PinLockedUntil
	{
		get
		{
			lock (_gate)
			{
				return _lockedUntil is DateTime t && t > _clock() ? t : null;
			}
		}
	}

	public IReadOnlyList<PairedPhone> Phones
	{
		get
		{
			lock (_gate)
			{
				return _sessions.Values.OrderBy(p => p.PairedUtc).ToList();
			}
		}
	}

	/// <summary>Replaces the pairing code now (the Mac's "New code" button).</summary>
	public void Renew()
	{
		lock (_gate)
		{
			_code = NewCode();
		}
		Changed?.Invoke();
	}

	/// <summary>Pairs with the token from the QR code.</summary>
	public PairResult PairWithToken(string token, string name, string remoteAddress)
	{
		PairResult result;
		lock (_gate)
		{
			if (!SameSecret(token, _code.Token))
			{
				return new PairResult(PairOutcome.Invalid, "That pairing link isn't valid any more. Scan the QR code on the Mac again.");
			}
			result = Complete(name, remoteAddress);
		}
		Changed?.Invoke();
		return result;
	}

	/// <summary>Pairs with the 6-digit PIN shown on the Mac.</summary>
	public PairResult PairWithPin(string pin, string name, string remoteAddress)
	{
		PairResult result;
		lock (_gate)
		{
			DateTime now = _clock();
			if (_lockedUntil is DateTime until)
			{
				if (until > now)
				{
					return new PairResult(PairOutcome.LockedOut, $"Too many wrong PINs. Try again in {Math.Max(1, (int)Math.Ceiling((until - now).TotalMinutes))} minutes, or scan the QR code on the Mac.");
				}
				_lockedUntil = null;
				_wrongPins = 0;
			}
			if (!SameSecret((pin ?? "").Replace(" ", "").Trim(), _code.Pin))
			{
				_wrongPins++;
				if (_wrongPins >= MaxPinAttempts)
				{
					_lockedUntil = now + LockoutTime;
					return new PairResult(PairOutcome.LockedOut, "Too many wrong PINs. PIN pairing is paused for 10 minutes; you can still scan the QR code on the Mac.");
				}
				return new PairResult(PairOutcome.Invalid, "That PIN isn't right. Check the PIN on the Mac and try again.");
			}
			_wrongPins = 0;
			result = Complete(name, remoteAddress);
		}
		Changed?.Invoke();
		return result;
	}

	/// <summary>Called with the lock held, once the token or PIN matched.</summary>
	private PairResult Complete(string name, string remoteAddress)
	{
		DateTime now = _clock();
		if (now >= _code.ExpiresUtc)
		{
			_code = NewCode();
			return new PairResult(PairOutcome.Expired, "That pairing code has expired. Use the new QR code or PIN on the Mac.");
		}
		string clean = CleanName(name);
		if (clean == null)
		{
			return new PairResult(PairOutcome.Invalid, "Type a name for this phone (your name is best), so the Mac's edit history can show who made each change.");
		}
		if (_sessions.Count >= MaxPhones)
		{
			return new PairResult(PairOutcome.Invalid, $"{MaxPhones} phones are already paired. Revoke one on the Mac first.");
		}
		// The code is used up: the next phone needs a new one.
		_code = NewCode();
		string session = Secret(32);
		PairedPhone phone = new PairedPhone(clean, remoteAddress, now);
		_sessions[Hash(session)] = phone;
		return new PairResult(PairOutcome.Paired, "Paired.", phone, session);
	}

	/// <summary>The phone a session cookie belongs to (and notes that it was seen), or null.</summary>
	public PairedPhone FindSession(string sessionToken)
	{
		if (string.IsNullOrEmpty(sessionToken) || sessionToken.Length > 100)
		{
			return null;
		}
		lock (_gate)
		{
			if (_sessions.TryGetValue(Hash(sessionToken), out PairedPhone phone))
			{
				phone.LastSeenUtc = _clock();
				return phone;
			}
			return null;
		}
	}

	/// <summary>Ends a phone's session; its next request is refused and it has to pair again.</summary>
	public bool Revoke(Guid phoneId)
	{
		bool removed;
		lock (_gate)
		{
			string key = _sessions.FirstOrDefault(kv => kv.Value.Id == phoneId).Key;
			removed = key != null && _sessions.Remove(key);
		}
		if (removed)
		{
			Changed?.Invoke();
		}
		return removed;
	}

	/// <summary>Ends every session and makes a new code (the server stopped).</summary>
	public void Reset()
	{
		lock (_gate)
		{
			_sessions.Clear();
			_wrongPins = 0;
			_lockedUntil = null;
			_code = NewCode();
		}
		Changed?.Invoke();
	}

	/// <summary>Trims a phone name and removes control characters; null when nothing is left.</summary>
	internal static string CleanName(string name) => CleanText(name, MaxNameLength);

	/// <summary>Trims text, removes control characters and cuts it to <paramref name="max"/> characters; null when nothing is left.</summary>
	internal static string CleanText(string text, int max)
	{
		if (text == null)
		{
			return null;
		}
		string clean = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
		if (clean.Length > max)
		{
			clean = clean[..max].Trim();
		}
		return clean.Length == 0 ? null : clean;
	}

	private PairingCode NewCode() =>
		new PairingCode(Secret(32), RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6"), _clock() + CodeLifetime);

	/// <summary>A random URL-safe secret of <paramref name="bytes"/> bytes.</summary>
	internal static string Secret(int bytes) =>
		Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

	private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

	/// <summary>Compares two secrets in constant time.</summary>
	private static bool SameSecret(string given, string expected)
	{
		if (string.IsNullOrEmpty(given) || expected == null)
		{
			return false;
		}
		return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
	}
}
