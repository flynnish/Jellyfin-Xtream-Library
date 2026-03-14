using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.SeerrFiltered.Service;

/// <summary>
/// Service to handle filtering content via Overseerr/Jellyseerr API.
/// </summary>
public class OverseerrService
{
    private HashSet<int> _allowedTmdbIds = new HashSet<int>();

    /// <summary>
    /// Refreshes the local cache of TMDb IDs from Overseerr requests that are not yet available.
    /// </summary>
    /// <param name="url">The Overseerr URL.</param>
    /// <param name="apiKey">The Overseerr API Key.</param>
    /// <returns>A task representing the async operation.</returns>
    public async Task RefreshCache(string url, string apiKey)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(apiKey))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

            var response = await client.GetFromJsonAsync<OverseerrResponse>($"{url.TrimEnd('/')}/api/v1/request?take=999&skip=0").ConfigureAwait(false);

            if (response?.Results != null)
            {
                _allowedTmdbIds = response.Results
                    .Where(r => r.Media != null && (r.Media.Status == 3 || r.Media.Status == 2))
                    .Select(r => r.Media!.TmdbId)
                    .ToHashSet();
            }
        }
        catch
        {
            // Fail silently to keep existing cache
        }
    }

    /// <summary>
    /// Checks if a TMDb ID is in the allowed list from Overseerr.
    /// </summary>
    /// <param name="tmdbId">The TMDb ID to check.</param>
    /// <returns>True if allowed.</returns>
    public bool IsAllowed(int tmdbId)
    {
        return _allowedTmdbIds.Contains(tmdbId);
    }
}

/// <summary>
/// Response model for Overseerr API.
/// </summary>
public class OverseerrResponse
{
    /// <summary>
    /// Gets or sets the list of results.
    /// </summary>
    public List<OverseerrRequest>? Results { get; set; }
}

/// <summary>
/// Request model for Overseerr.
/// </summary>
public class OverseerrRequest
{
    /// <summary>
    /// Gets or sets media info.
    /// </summary>
    public MediaData? Media { get; set; }
}

/// <summary>
/// Media data model.
/// </summary>
public class MediaData
{
    /// <summary>
    /// Gets or sets the TMDb ID.
    /// </summary>
    public int TmdbId { get; set; }

    /// <summary>
    /// Gets or sets the Overseerr media status.
    /// </summary>
    public int Status { get; set; }
}