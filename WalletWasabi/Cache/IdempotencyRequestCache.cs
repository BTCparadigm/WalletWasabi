using Microsoft.Extensions.Caching.Memory;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.WabiSabi.Backend.Models;
using WalletWasabi.WabiSabi.Crypto;

namespace WalletWasabi.Cache;

public class IdempotencyRequestCache
{
	public IdempotencyRequestCache(IMemoryCache cache)
	{
		ResponseCache = cache;
	}

	public delegate Task<TResponse> ProcessRequestDelegateAsync<TRequest, TResponse>(TRequest sender, CancellationToken cancellationToken);

	/// <summary>Timeout specifying how long a request response can stay in memory.</summary>
	private static TimeSpan CacheTimeout { get; } = TimeSpan.FromMinutes(5);

	private static TimeSpan RequestTimeout { get; } = TimeSpan.FromMinutes(1);

	/// <summary>Guards <see cref="ResponseCache"/>.</summary>
	/// <remarks>Unfortunately, <see cref="CacheExtensions.GetOrCreate{TItem}(IMemoryCache, object, Func{ICacheEntry, TItem})"/> is not atomic.</remarks>
	/// <seealso href="https://github.com/dotnet/runtime/issues/36499"/>
	private object ResponseCacheLock { get; } = new();

	/// <remarks>Guarded by <see cref="ResponseCacheLock"/>.</remarks>
	private IMemoryCache ResponseCache { get; }

	/// <typeparam name="TRequest">
	/// <see langword="record"/>s are preferred as <see cref="object.GetHashCode"/>
	/// and <see cref="object.Equals(object?)"/> are generated for <see langword="record"/> types automatically.
	/// </typeparam>
	/// <typeparam name="TResponse">Type associated with <typeparamref name="TRequest"/>. The correspondence should be 1:1 mapping.</typeparam>
	public Task<TResponse> GetCachedResponseAsync<TRequest, TResponse>(TRequest request, ProcessRequestDelegateAsync<TRequest, TResponse> action, CancellationToken cancellationToken = default)
		where TRequest : notnull
	{
		MemoryCacheEntryOptions options = new()
		{
			AbsoluteExpiration = DateTimeOffset.UtcNow.Add(CacheTimeout),
		};

		return GetCachedResponseAsync(request, action, options, cancellationToken);
	}

	/// <typeparam name="TRequest">
	/// <see langword="record"/>s are preferred as <see cref="object.GetHashCode"/>
	/// and <see cref="object.Equals(object?)"/> are generated for <see langword="record"/> types automatically.
	/// </typeparam>
	/// <typeparam name="TResponse">Type associated with <typeparamref name="TRequest"/>. The correspondence should be 1:1 mapping.</typeparam>
	public async Task<TResponse> GetCachedResponseAsync<TRequest, TResponse>(TRequest request, ProcessRequestDelegateAsync<TRequest, TResponse> action, MemoryCacheEntryOptions options, CancellationToken cancellationToken)
		where TRequest : notnull
	{
		bool callAction = false;
		TaskCompletionSource<TResponse> responseTcs;

		while (true)
		{
			lock (ResponseCacheLock)
			{
				if (!ResponseCache.TryGetValue(request, out responseTcs))
				{
					callAction = true;
					responseTcs = new();
					ResponseCache.Set(request, responseTcs, options);
				}
			}

			TResponse result;

			if (callAction)
			{
				try
				{
					result = await action(request, cancellationToken).ConfigureAwait(false);
					responseTcs.SetResult(result);
					return result;
				}
				catch (Exception e)
				{
					responseTcs.SetException(e);

					// To avoid unobserved exception.
					await responseTcs.Task.ConfigureAwait(false);
				}
			}
			else
			{
				using CancellationTokenSource timeoutCts = new(RequestTimeout);
				using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

				try
				{
					// All requests where this idempotent behavior (i.e. retry behavior) makes sense are mutex/CPU bound on the server,
					// and perform no IO or network requests, so the only reason that processing of a request should ever time out is if we have a deadlock type bug.
					result = await responseTcs.Task.WithAwaitCancellationAsync(linkedCts.Token).ConfigureAwait(false);
					return result;
				}
				catch (OperationCanceledException e)
				{
					if (e.CancellationToken == cancellationToken)
					{
						// Cancelled by application shutting down or when the HTTP request is cancelled.
						throw;
					}
				}
				catch (Exception e) when (e is WabiSabiProtocolException or WabiSabiCryptoException)
				{
					throw;
				}
				catch (Exception)
				{
					lock (ResponseCacheLock)
					{
						ResponseCache.Remove(request);
					}
				}
			}
		}
	}

	/// <remarks>
	/// For testing purposes only.
	/// <para>Note that if there is a simultaneous request for the cache key, it is not stopped and its result is discarded.</para>
	/// </remarks>
	internal void Remove<TRequest>(TRequest cacheKey)
	{
		lock (ResponseCacheLock)
		{
			ResponseCache.Remove(cacheKey);
		}
	}
}
