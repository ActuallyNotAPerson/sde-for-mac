namespace Sde.Tests.Data;

/// <summary>The committed FEMA format facts (tests/fixtures/fema-facts) still match the decompiled code.</summary>
public class FemaFactsTests
{
	[Fact]
	public void Snapshot_MatchesDecomp()
	{
		if (Environment.GetEnvironmentVariable("SDE_WRITE_FEMA_FACTS") == "1")
		{
			FemaFacts.WriteSnapshot();
		}
		foreach (string model in FemaFacts.DalModels)
		{
			Assert.Equal(FemaFacts.DalModelFromDecomp(model), FemaFacts.DalModel(model));
		}
		List<string> headers = FemaFacts.ExcelHeadersFromDecomp();
		Assert.Equal(218, headers.Count);
		Assert.Equal(headers, FemaFacts.ExcelHeaders());
	}
}
