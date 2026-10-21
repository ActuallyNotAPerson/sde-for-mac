namespace Sde.Interop.Online;

/// <summary>The outcome for one item of an <see cref="OnlineBatch"/>.</summary>
/// <param name="Input">The item that was processed.</param>
/// <param name="Succeeded">True when the work finished (a null Value is still a success, e.g. "no match").</param>
/// <param name="Value">The work's result; default when it failed.</param>
/// <param name="ErrorMessage">Plain-English reason when it failed; null otherwise.</param>
public sealed record BatchResult<TIn, TOut>(TIn Input, bool Succeeded, TOut Value, string ErrorMessage);

/// <summary>Runs an online lookup over many items with a few at a time, a time limit and a retry for each.</summary>
public static class OnlineBatch
{
	/// <summary>
	/// Processes every item and returns the results in the same order as <paramref name="items"/>.
	/// A failing item doesn't stop the others. Cancelling through <paramref name="ct"/> throws <see cref="OperationCanceledException"/>.
	/// </summary>
	/// <param name="work">The lookup for one item.</param>
	/// <param name="concurrency">How many items run at once.</param>
	/// <param name="timeout">Time limit for each attempt at an item.</param>
	/// <param name="retries">Extra attempts after a failure or timeout.</param>
	/// <param name="progress">Told how many items have finished so far.</param>
	public static async Task<IReadOnlyList<BatchResult<TIn, TOut>>> RunAsync<TIn, TOut>(
		IReadOnlyList<TIn> items,
		Func<TIn, CancellationToken, Task<TOut>> work,
		int concurrency = 4,
		TimeSpan? timeout = null,
		int retries = 1,
		IProgress<int> progress = null,
		CancellationToken ct = default)
	{
		TimeSpan limit = timeout ?? TimeSpan.FromSeconds(20);
		BatchResult<TIn, TOut>[] results = new BatchResult<TIn, TOut>[items.Count];
		int done = 0;
		ParallelOptions options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = ct };

		await Parallel.ForEachAsync(Enumerable.Range(0, items.Count), options, async (int index, CancellationToken token) =>
		{
			results[index] = await RunOneAsync(items[index], work, limit, Math.Max(0, retries), token).ConfigureAwait(false);
			progress?.Report(Interlocked.Increment(ref done));
		}).ConfigureAwait(false);

		return results;
	}

	private static async Task<BatchResult<TIn, TOut>> RunOneAsync<TIn, TOut>(
		TIn item, Func<TIn, CancellationToken, Task<TOut>> work, TimeSpan limit, int retries, CancellationToken ct)
	{
		string message = null;
		for (int attempt = 0; attempt <= retries; attempt++)
		{
			ct.ThrowIfCancellationRequested();
			using CancellationTokenSource attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			attemptCts.CancelAfter(limit);
			try
			{
				TOut value = await work(item, attemptCts.Token).ConfigureAwait(false);
				return new BatchResult<TIn, TOut>(item, true, value, null);
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				throw;
			}
			catch (OperationCanceledException)
			{
				message = $"No answer within {(int)Math.Ceiling(limit.TotalSeconds)} seconds.";
			}
			catch (Exception ex)
			{
				message = ex.Message;
			}
		}
		return new BatchResult<TIn, TOut>(item, false, default, message);
	}
}
