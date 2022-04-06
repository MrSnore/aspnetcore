// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.AspNetCore.OutputCaching;

/// <summary>
/// Represents a store for cached responses.
/// </summary>
public interface IOutputCacheStore
{
    /// <summary>
    /// Evicts cached responses by tag.
    /// </summary>
    /// <param name="tag">The tag to evict.</param>
    ValueTask EvictByTagAsync(string tag);

    /// <summary>
    /// Gets the cached response for the given key, if it exists.
    /// If no cached response exists for the given key, <c>null</c> is returned.
    /// </summary>
    /// <param name="key">The cache key to look up.</param>
    /// <returns>The response cache entry if it exists; otherwise <c>null</c>.</returns>
    ValueTask<OutputCacheEntry?> GetAsync(string key);

    /// <summary>
    /// Stores the given response in the response cache.
    /// </summary>
    /// <param name="key">The cache key to store the response under.</param>
    /// <param name="entry">The response cache entry to store.</param>
    /// <param name="validFor">The amount of time the entry will be kept in the cache before expiring, relative to now.</param>
    ValueTask SetAsync(string key, OutputCacheEntry entry, TimeSpan validFor);
}