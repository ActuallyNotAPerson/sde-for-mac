using System.Net;
using Sde.Field;

namespace Sde.Field.Tests;

/// <summary>Pairing codes, PIN lockout, sessions, and which client addresses are let in.</summary>
public class PairingTests
{
	private DateTime _now = new DateTime(2026, 10, 20, 15, 0, 0, DateTimeKind.Utc);

	private PairingManager NewManager() => new PairingManager(() => _now);

	[Fact]
	public void Token_WorksOnce()
	{
		PairingManager m = NewManager();
		string token = m.Current.Token;
		PairResult first = m.PairWithToken(token, "Test Inspector", "192.168.1.30");
		Assert.Equal(PairOutcome.Paired, first.Outcome);
		Assert.Equal("Test Inspector", first.Phone.Name);
		Assert.Same(first.Phone, m.FindSession(first.SessionToken));

		PairResult again = m.PairWithToken(token, "Someone Else", "192.168.1.31");
		Assert.Equal(PairOutcome.Invalid, again.Outcome);
		Assert.NotEqual(token, m.Current.Token);
		Assert.Single(m.Phones);
	}

	[Fact]
	public void Token_ExpiresAfterTenMinutes()
	{
		PairingManager m = NewManager();
		PairingCode code = m.Current;
		_now += TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1);
		PairResult r = m.PairWithToken(code.Token, "Test Inspector", "10.0.0.5");
		Assert.Equal(PairOutcome.Expired, r.Outcome);
		Assert.Null(r.SessionToken);
		Assert.NotEqual(code.Token, m.Current.Token);
		Assert.Empty(m.Phones);
	}

	[Fact]
	public void Pin_LocksAfterFiveWrongTries_ForTenMinutes()
	{
		PairingManager m = NewManager();
		string pin = m.Current.Pin;
		string wrong = pin == "000000" ? "111111" : "000000";
		for (int i = 0; i < PairingManager.MaxPinAttempts - 1; i++)
		{
			Assert.Equal(PairOutcome.Invalid, m.PairWithPin(wrong, "Test Inspector", "192.168.1.30").Outcome);
		}
		Assert.Equal(PairOutcome.LockedOut, m.PairWithPin(wrong, "Test Inspector", "192.168.1.30").Outcome);
		// Locked: even the right PIN is refused, but the QR token still works.
		Assert.Equal(PairOutcome.LockedOut, m.PairWithPin(pin, "Test Inspector", "192.168.1.30").Outcome);
		Assert.NotNull(m.PinLockedUntil);

		_now += TimeSpan.FromMinutes(9);
		Assert.Equal(PairOutcome.LockedOut, m.PairWithPin(m.Current.Pin, "Test Inspector", "192.168.1.30").Outcome);
		_now += TimeSpan.FromMinutes(1);
		Assert.Null(m.PinLockedUntil);
		Assert.Equal(PairOutcome.Paired, m.PairWithPin(m.Current.Pin, "Test Inspector", "192.168.1.30").Outcome);
	}

	[Fact]
	public void Name_IsRequired_AndCleaned()
	{
		PairingManager m = NewManager();
		Assert.Equal(PairOutcome.Invalid, m.PairWithToken(m.Current.Token, "   ", "127.0.0.1").Outcome);
		PairResult r = m.PairWithToken(m.Current.Token, "  Test\u0007 Inspector with a really very long name indeed  ", "127.0.0.1");
		Assert.Equal(PairOutcome.Paired, r.Outcome);
		Assert.Equal("Test Inspector with a really very long n", r.Phone.Name);
	}

	[Fact]
	public void Revoke_EndsTheSession_AndResetEndsAll()
	{
		PairingManager m = NewManager();
		PairResult a = m.PairWithToken(m.Current.Token, "Phone A", "192.168.1.30");
		PairResult b = m.PairWithPin(m.Current.Pin, "Phone B", "192.168.1.31");
		Assert.Equal(2, m.Phones.Count);
		Assert.True(m.Revoke(a.Phone.Id));
		Assert.Null(m.FindSession(a.SessionToken));
		Assert.NotNull(m.FindSession(b.SessionToken));
		m.Reset();
		Assert.Null(m.FindSession(b.SessionToken));
		Assert.Null(m.FindSession("not a session"));
	}

	[Theory]
	[InlineData("10.1.2.3", true)]
	[InlineData("172.16.0.1", true)]
	[InlineData("172.31.255.254", true)]
	[InlineData("192.168.0.10", true)]
	[InlineData("127.0.0.1", true)]
	[InlineData("::1", true)]
	[InlineData("::ffff:192.168.1.5", true)]
	[InlineData("172.15.0.1", false)]
	[InlineData("172.32.0.1", false)]
	[InlineData("192.169.0.1", false)]
	[InlineData("169.254.10.10", false)]
	[InlineData("8.8.8.8", false)]
	[InlineData("100.64.0.1", false)]
	[InlineData("::ffff:8.8.8.8", false)]
	[InlineData("fe80::1", false)]
	[InlineData("fd00::1", false)]
	[InlineData("2001:db8::1", false)]
	[InlineData("0.0.0.0", false)]
	public void OnlyPrivateAndLoopbackClients_AreAllowed(string address, bool allowed)
	{
		Assert.Equal(allowed, NetworkAccess.IsPrivateOrLoopback(IPAddress.Parse(address)));
	}

	[Fact]
	public void NoAddress_IsNotAllowed() => Assert.False(NetworkAccess.IsPrivateOrLoopback(null));

	[Fact]
	public void LanAddress_IsPrivate_WhenFound()
	{
		IPAddress lan = NetworkAccess.FindLanAddress();
		Assert.True(lan == null || NetworkAccess.IsPrivateLan(lan));
	}
}
