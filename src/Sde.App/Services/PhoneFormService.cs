using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QRCoder;
using Sde.Core.Assessments;
using Sde.Data;
using Sde.Field;

namespace Sde.App.Services;

/// <summary>Saves the phone form's changes through the write coordinator, so the editor shows them at once.</summary>
public sealed class AppFieldWriter(AppServices app) : IFieldWriter
{
	public Task<AssessmentFile> ApplyAsync(Guid assessmentId, Action<AssessmentFile> change, SaveContext context) =>
		app.Writes.ApplyFromBackgroundAsync(assessmentId, change, context);

	public Task<AssessmentFile> AddAsync(AssessmentFile file, SaveContext context) =>
		Dispatcher.UIThread.InvokeAsync(() => app.Writes.Write(Array.Empty<Guid>(), () =>
		{
			app.Store.SaveAssessment(file, context);
			return file;
		})).GetTask();
}

/// <summary>HEIC to JPEG with macOS's sips (<see cref="PhotoConversion"/>); elsewhere photos are kept as they are.</summary>
public sealed class MacPhotoConverter : IPhotoConverter
{
	public string ToCompatible(string path, string tempFolder) =>
		OperatingSystem.IsMacOS() ? PhotoConversion.Compatible(path, tempFolder) : path;
}

/// <summary>A paired phone in the Settings list, with Revoke.</summary>
public sealed partial class PairedPhoneViewModel(PairedPhone phone, Action<PairedPhoneViewModel> revoke) : ObservableObject
{
	public PairedPhone Phone { get; } = phone;

	public string Name => Phone.Name;

	public string Details => "Paired at " + Phone.PairedUtc.ToLocalTime().ToString("h:mm tt", CultureInfo.GetCultureInfo("en-US"))
		+ (string.IsNullOrEmpty(Phone.RemoteAddress) ? "" : " from " + Phone.RemoteAddress);

	public string RevokeLabel => "Revoke " + Name;

	[RelayCommand]
	private void Revoke() => revoke(this);
}

/// <summary>
/// The phone form, as the Mac runs it: Start and Stop, the QR code and PIN to pair a phone, the paired phones (with
/// Revoke), and how long until it stops itself. Lives as long as the app (not the Settings page), keeps the Mac awake
/// while running (caffeinate), and is stopped when the app quits. Off until someone presses Start.
/// </summary>
public sealed partial class PhoneFormService : ObservableObject
{
	private readonly AppServices _app;
	private readonly FieldServer _server;
	private readonly DispatcherTimer _timer;
	private PairingManager _pairing;
	private string _shownToken;
	private Process _caffeinate;

	public PhoneFormService(AppServices app)
	{
		_app = app;
		_server = new FieldServer(() => _app.Store, new AppFieldWriter(app), new MacPhotoConverter());
		_server.Stopped += reason => Dispatcher.UIThread.Post(() => OnStopped(reason));
		_server.Failed += ex => AppLog.Write("Phone form request failed", ex);
		_timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
		_timer.Tick += (_, _) => Refresh();
	}

	/// <summary>The web server itself (for tests and diagnostics).</summary>
	public FieldServer Server => _server;

	/// <summary>How to listen; null finds the Mac's private network address (tests listen on 127.0.0.1).</summary>
	public FieldServerOptions Options { get; set; }

	public ObservableCollection<PairedPhoneViewModel> Phones { get; } = new();

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsStopped))]
	[NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(NewCodeCommand))]
	public partial bool IsRunning { get; private set; }

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(StartCommand))]
	public partial bool IsStarting { get; private set; }

	public bool IsStopped => !IsRunning;

	/// <summary>The address to type on a phone, e.g. http://192.168.1.20:52814/.</summary>
	[ObservableProperty]
	public partial string Address { get; private set; }

	[ObservableProperty]
	public partial string Pin { get; private set; }

	/// <summary>The pairing link as a QR code.</summary>
	[ObservableProperty]
	public partial Bitmap QrCode { get; private set; }

	[ObservableProperty]
	public partial string TimeLeft { get; private set; }

	/// <summary>"Too many wrong PINs…" while PIN pairing is paused; empty otherwise.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasPinWarning))]
	public partial string PinWarning { get; private set; } = "";

	public bool HasPinWarning => !string.IsNullOrEmpty(PinWarning);

	/// <summary>Why it stopped last (e.g. after 12 hours); empty while running.</summary>
	[ObservableProperty]
	public partial string LastStopped { get; private set; } = "";

	[ObservableProperty]
	public partial bool HasPhones { get; private set; }

	private bool CanStart => !IsRunning && !IsStarting;

	[RelayCommand(CanExecute = nameof(CanStart))]
	public async Task StartAsync()
	{
		IsStarting = true;
		try
		{
			FieldServerOptions options = Options ?? new FieldServerOptions();
			await Task.Run(() => _server.StartAsync(options));
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't start the phone form", ex);
			return;
		}
		finally
		{
			IsStarting = false;
		}
		_pairing = _server.Pairing;
		_pairing.Changed += OnPairingChanged;
		LastStopped = "";
		Address = _server.BaseAddress.ToString();
		IsRunning = true;
		KeepAwake();
		_timer.Start();
		Refresh();
	}

	[RelayCommand(CanExecute = nameof(IsRunning))]
	public async Task StopAsync() => await _server.StopAsync();

	/// <summary>Makes a new QR code and PIN now (the old ones stop working).</summary>
	[RelayCommand(CanExecute = nameof(IsRunning))]
	private void NewCode() => _pairing?.Renew();

	/// <summary>Stops the server as the app quits (waits briefly so phones get a clean refusal).</summary>
	public void StopForQuit()
	{
		if (_server.IsRunning)
		{
			_server.StopAsync("SDE quit").Wait(TimeSpan.FromSeconds(3));
		}
		StopKeepingAwake();
	}

	private void OnPairingChanged() => Dispatcher.UIThread.Post(Refresh);

	private void OnStopped(string reason)
	{
		if (_pairing != null)
		{
			_pairing.Changed -= OnPairingChanged;
			_pairing = null;
		}
		_timer.Stop();
		StopKeepingAwake();
		IsRunning = false;
		Address = null;
		Pin = null;
		QrCode = null;
		_shownToken = null;
		TimeLeft = null;
		PinWarning = "";
		Phones.Clear();
		HasPhones = false;
		LastStopped = reason == "Stopped" ? "" : reason;
	}

	/// <summary>Brings the code, phones and countdown up to date (every second, and when a phone pairs).</summary>
	private void Refresh()
	{
		if (!IsRunning || _pairing == null || !_server.IsRunning)
		{
			return;
		}
		PairingCode code = _pairing.Current;
		if (code.Token != _shownToken)
		{
			_shownToken = code.Token;
			Pin = code.Pin[..3] + " " + code.Pin[3..];
			QrCode = MakeQrCode(_server.BaseAddress + "pair?t=" + code.Token);
		}
		TimeSpan left = _server.StopsAtUtc - DateTime.UtcNow;
		TimeLeft = left <= TimeSpan.Zero ? "Stopping…" : $"Stops by itself in {(int)left.TotalHours} h {left.Minutes:00} min";
		PinWarning = _pairing.PinLockedUntil is DateTime until
			? $"Too many wrong PINs: PIN pairing is paused until {until.ToLocalTime().ToString("h:mm tt", CultureInfo.GetCultureInfo("en-US"))}. The QR code still works."
			: "";

		IReadOnlyList<PairedPhone> phones = _pairing.Phones;
		if (!phones.Select(p => p.Id).SequenceEqual(Phones.Select(p => p.Phone.Id)))
		{
			Phones.Clear();
			foreach (PairedPhone p in phones)
			{
				Phones.Add(new PairedPhoneViewModel(p, vm => _pairing?.Revoke(vm.Phone.Id)));
			}
			HasPhones = Phones.Count > 0;
		}
	}

	/// <summary>The QR code for <paramref name="text"/> as a bitmap.</summary>
	internal static Bitmap MakeQrCode(string text)
	{
		using QRCodeGenerator generator = new QRCodeGenerator();
		using QRCodeData data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
		byte[] png = new PngByteQRCode(data).GetGraphic(8);
		using MemoryStream stream = new MemoryStream(png);
		return new Bitmap(stream);
	}

	/// <summary>Keeps the Mac from sleeping while the phone form runs (caffeinate ends by itself when SDE does).</summary>
	private void KeepAwake()
	{
		if (!OperatingSystem.IsMacOS() || _caffeinate != null)
		{
			return;
		}
		try
		{
			ProcessStartInfo psi = new ProcessStartInfo("/usr/bin/caffeinate") { UseShellExecute = false };
			foreach (string a in new[] { "-i", "-w", Environment.ProcessId.ToString(CultureInfo.InvariantCulture) })
			{
				psi.ArgumentList.Add(a);
			}
			_caffeinate = Process.Start(psi);
		}
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
		{
			AppLog.Write("Couldn't start caffeinate", ex);
		}
	}

	private void StopKeepingAwake()
	{
		if (_caffeinate == null)
		{
			return;
		}
		try
		{
			if (!_caffeinate.HasExited)
			{
				_caffeinate.Kill();
			}
		}
		catch (InvalidOperationException)
		{
		}
		_caffeinate.Dispose();
		_caffeinate = null;
	}
}
