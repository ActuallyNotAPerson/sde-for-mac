using System.Net;
using System.Net.Http.Json;
using Avalonia.Headless.XUnit;
using Sde.App.Services;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;
using Sde.Field;

namespace Sde.App.Tests;

/// <summary>The phone form's card on the Settings page, and phone changes reaching the Mac's editor.</summary>
public class PhoneFormUiTests
{
	[AvaloniaFact]
	public async Task PhoneForm_StartsWithQrAndPin_ListsPhones_AndEveryInputIsNamed()
	{
		using UiSession ui = new UiSession(height: 1600);
		PhoneFormService phone = ui.Services.PhoneForm;
		phone.Options = new FieldServerOptions { BindAddress = IPAddress.Loopback };
		ui.Main.ShowCommand.Execute(ViewModels.Page.Settings);
		ui.Pump();
		Assert.False(phone.IsRunning);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

		await phone.StartCommand.ExecuteAsync(null);
		ui.Pump();
		try
		{
			Assert.True(phone.IsRunning);
			Assert.StartsWith("http://127.0.0.1:", phone.Address);
			Assert.NotNull(phone.QrCode);
			Assert.Matches(@"^\d{3} \d{3}$", phone.Pin);
			Assert.Contains("Stops by itself in 11 h", phone.TimeLeft);
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

			PairResult paired = phone.Server.Pairing.PairWithToken(phone.Server.Pairing.Current.Token, "Test Inspector", "127.0.0.1");
			Assert.Equal(PairOutcome.Paired, paired.Outcome);
			ui.Pump();
			PairedPhoneViewModel listed = Assert.Single(phone.Phones);
			Assert.Equal("Revoke Test Inspector", listed.RevokeLabel);
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

			listed.RevokeCommand.Execute(null);
			ui.Pump();
			Assert.Empty(phone.Phones);
			Assert.Null(phone.Server.Pairing.FindSession(paired.SessionToken));
		}
		finally
		{
			await phone.StopCommand.ExecuteAsync(null);
			ui.Pump();
		}
		Assert.False(phone.IsRunning);
		Assert.Null(phone.QrCode);
	}

	[AvaloniaFact]
	public async Task PhoneChange_GoesThroughTheCoordinator_IntoTheOpenEditor()
	{
		using UiSession ui = new UiSession();
		PropertyRecord p = ui.Store.SaveProperty(new PropertyRecord { StructureTypeID = 1, StreetNumber = "7", StreetName = "Sample Lane", City = "Testville" });
		AssessmentFile f = ui.Store.CreateAssessment(p.PropertyID, applyDefaults: false);
		ui.Main.OpenAssessment(f.Assessment.AssessmentID);
		ui.Pump();

		PhoneFormService phone = ui.Services.PhoneForm;
		phone.Options = new FieldServerOptions { BindAddress = IPAddress.Loopback };
		await phone.StartCommand.ExecuteAsync(null);
		try
		{
			CookieContainer cookies = new CookieContainer();
			using HttpClient client = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = phone.Server.BaseAddress };
			HttpRequestMessage pair = new HttpRequestMessage(HttpMethod.Post, "/api/pair")
			{
				Content = JsonContent.Create(new { token = phone.Server.Pairing.Current.Token, name = "Test Inspector" }),
			};
			pair.Headers.Add(FieldServer.CsrfHeader, "1");
			Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(pair, TestContext.Current.CancellationToken)).StatusCode);

			HttpRequestMessage change = new HttpRequestMessage(HttpMethod.Post, $"/api/assessments/{f.Assessment.AssessmentID}/changes")
			{
				Content = JsonContent.Create(new { changes = new[] { new { key = "Property.City", value = "Elsewhere" } } }),
			};
			change.Headers.Add(FieldServer.CsrfHeader, "1");
			HttpResponseMessage res = await client.SendAsync(change, TestContext.Current.CancellationToken);
			Assert.Equal(HttpStatusCode.OK, res.StatusCode);
			ui.Pump();

			Assert.Equal("Elsewhere", ui.Main.Editor.Property.City);
			EditHistoryRecord h = ui.Store.GetHistory(f.Assessment.AssessmentID).First(x => x.Field == "Property.City");
			Assert.Equal(SaveVia.Phone, h.Via);
			Assert.Equal("Test Inspector", h.ChangedBy);
		}
		finally
		{
			await phone.StopCommand.ExecuteAsync(null);
		}
	}
}
