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
    private HashSet<string> _allowedTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Refreshes the local cache of tagged titles from Overseerr.
    /// </summary>
    /// <param name="url">The Overseerr URL.</param>
    /// <param name="apiKey">The Overseerr API Key.</param>
    /// <param name="tag">The tag to filter by.</param>
    /// <returns>A task representing the async operation.</returns>
    public async Task RefreshCache(string url, string apiKey, string tag)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(apiKey))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
            var response = await client.GetFromJsonAsync<OverseerrResponse>($"{url.TrimEnd('/')}/api/v1/request?take=500&skip=0").ConfigureAwait(false);

            if (response != null && response.Results != null)
            {
                _allowedTitles = response.Results
                    .Where(r => (r.Media != null && r.Media.Tags != null && r.Media.Tags.Any(t => t.Label.Equals(tag, StringComparison.OrdinalIgnoreCase))) ||
                                (r.MediaInfo != null && r.MediaInfo.Tags != null && r.MediaInfo.Tags.Any(t => t.Label.Equals(tag, StringComparison.OrdinalIgnoreCase))))
                    .Select(r => r.MediaInfo?.Title ?? r.Media?.Title)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
            }
        }
        catch
        {
            // Fail silently to keep existing cache
        }
    }

    /// <summary>
    /// Checks if a title is allowed based on the Overseerr cache.
    /// </summary>
    /// <param name="xtreamName">The name from Xtream Codes.</param>
    /// <returns>True if allowed.</returns>
    public bool IsAllowed(string xtreamName)
    {
        if (_allowedTitles.Count == 0)
        {
            return false;
        }

        return _allowedTitles.Any(t => xtreamName.Contains(t, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// Gets or sets extended media info.
    /// </summary>
    public MediaData? MediaInfo { get; set; }
}

/// <summary>
/// Media data model.
/// </summary>
public class MediaData
{
    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the tags.
    /// </summary>
    public List<Tag>? Tags { get; set; }
}

/// <summary>
/// Tag model.
/// </summary>
public class Tag
{
    /// <summary>
    /// Gets or sets the label.
    /// </summary>
    public string Label { get; set; } = string.Empty;
}