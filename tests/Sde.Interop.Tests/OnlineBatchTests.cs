using Sde.Interop.Online;

namespace Sde.Interop.Tests;

public class OnlineBatchTests
{
	[Fact]
	public async Task Results_KeepInputOrder_EvenWhenWorkFinishesOutOfOrder()
	{
		List<int> items = Enumerable.Range(0, 12).ToList();
		IReadOnlyList<BatchResult<int, int>> results = await OnlineBatch.RunAsync<int, int>(items, async (i, ct) =>
		{
			await Task.Delay((12 - i) * 5, ct);
			return i * 10;
		});

		Assert.Equal(items, results.Select(r => r.Input));
		Assert.Equal(items.Select(i => i * 10), results.Select(r => r.Value));
		Assert.All(results, r => Assert.True(r.Succeeded));
	}

	[Fact]
	public async Task Concurrency_IsCapped()
	{
		int running = 0;
		int peak = 0;
		await OnlineBatch.RunAsync<int, int>(Enumerable.Range(0, 20).ToList(), async (i, ct) =>
		{
			int now = Interlocked.Increment(ref running);
			int seen;
			while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen)
			{
			}
			await Task.Delay(20, ct);
			Interlocked.Decrement(ref running);
			return i;
		}, concurrency: 3);

		Assert.InRange(peak, 2, 3);
	}

	[Fact]
	public async Task Retry_RunsAgainAfterAFailure()
	{
		int calls = 0;
		IReadOnlyList<BatchResult<string, string>> results = await OnlineBatch.RunAsync<string, string>(new[] { "a" }, (s, ct) =>
		{
			if (Interlocked.Increment(ref calls) == 1)
			{
				throw new InvalidOperationException("first try fails");
			}
			return Task.FromResult(s + "!");
		}, retries: 1);

		Assert.Equal(2, calls);
		Assert.True(results[0].Succeeded);
		Assert.Equal("a!", results[0].Value);
	}

	[Fact]
	public async Task Failure_AfterRetries_ReportsMessage_AndOthersStillRun()
	{
		int calls = 0;
		IReadOnlyList<BatchResult<int, int>> results = await OnlineBatch.RunAsync<int, int>(new[] { 1, 2, 3 }, (i, ct) =>
		{
			if (i == 2)
			{
				Interlocked.Increment(ref calls);
				throw new OnlineServiceException("The service is down.", "detail");
			}
			return Task.FromResult(i);
		}, retries: 2);

		Assert.Equal(3, calls);
		Assert.True(results[0].Succeeded);
		Assert.False(results[1].Succeeded);
		Assert.Equal("The service is down.", results[1].ErrorMessage);
		Assert.True(results[2].Succeeded);
	}

	[Fact]
	public async Task PerItemTimeout_FailsThatItemOnly()
	{
		IReadOnlyList<BatchResult<int, int>> results = await OnlineBatch.RunAsync<int, int>(new[] { 1, 2 }, async (i, ct) =>
		{
			if (i == 1)
			{
				await Task.Delay(Timeout.Infinite, ct);
			}
			return i;
		}, timeout: TimeSpan.FromMilliseconds(80), retries: 0);

		Assert.False(results[0].Succeeded);
		Assert.Equal("No answer within 1 seconds.", results[0].ErrorMessage);
		Assert.True(results[1].Succeeded);
	}

	[Fact]
	public async Task Cancel_StopsTheBatch()
	{
		using CancellationTokenSource cts = new CancellationTokenSource();
		int started = 0;
		Task<IReadOnlyList<BatchResult<int, int>>> run = OnlineBatch.RunAsync<int, int>(Enumerable.Range(0, 50).ToList(), async (i, ct) =>
		{
			if (Interlocked.Increment(ref started) == 3)
			{
				cts.Cancel();
			}
			await Task.Delay(30, ct);
			return i;
		}, concurrency: 2, ct: cts.Token);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
		Assert.True(started < 50);
	}

	[Fact]
	public async Task Progress_CountsFinishedItems()
	{
		List<int> reported = new List<int>();
		SyncProgress progress = new SyncProgress(reported);
		await OnlineBatch.RunAsync<int, int>(new[] { 1, 2, 3, 4 }, (i, ct) => Task.FromResult(i), concurrency: 1, progress: progress);
		Assert.Equal(new[] { 1, 2, 3, 4 }, reported);
	}

	[Fact]
	public async Task EmptyInput_ReturnsEmpty()
	{
		Assert.Empty(await OnlineBatch.RunAsync<int, int>(new List<int>(), (i, ct) => Task.FromResult(i)));
	}

	private sealed class SyncProgress : IProgress<int>
	{
		private readonly List<int> _list;

		public SyncProgress(List<int> list)
		{
			_list = list;
		}

		public void Report(int value)
		{
			lock (_list)
			{
				_list.Add(value);
			}
		}
	}
}
