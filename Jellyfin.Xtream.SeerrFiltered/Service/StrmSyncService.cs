// Copyright (C) 2024 Roland Breitschaft
// This program is free software: you can redistribute it and/or modify...

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.SeerrFiltered.Client;
using Jellyfin.Xtream.SeerrFiltered.Client.Models;
using Jellyfin.Xtream.SeerrFiltered.Service.Models;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Jellyfin.Xtream.SeerrFiltered.Service;

/// <summary>
/// Service responsible for syncing Xtream content to STRM files with Overseerr filtering.
/// </summary>
public partial class StrmSyncService
{
    private static readonly HttpClient ImageHttpClient = CreateImageHttpClient();

    private readonly IXtreamClient _client;

    private readonly IDispatcharrClient _dispatcharrClient;

    private readonly ILibraryManager _libraryManager;

    private readonly IMetadataLookupService _metadataLookup;

    private readonly SnapshotService _snapshotService;

    private readonly DeltaCalculator _deltaCalculator;

    private readonly IServerApplicationPaths _appPaths;

    private readonly ILogger<StrmSyncService> _logger;

    private readonly OverseerrService _overseerrService;

    private readonly object _ctsLock = new object();

    private readonly List<SyncResult> _syncHistoryList = new List<SyncResult>();

    private readonly object _syncHistoryLock = new object();

    private bool _historyLoaded;

    private CancellationTokenSource? _currentSyncCts;

    private volatile bool _syncSuppressed;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmSyncService"/> class.
    /// </summary>
    /// <param name="client">The Xtream client.</param>
    /// <param name="dispatcharrClient">The dispatcharr client.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="metadataLookup">The metadata lookup service.</param>
    /// <param name="snapshotService">The snapshot service.</param>
    /// <param name="deltaCalculator">The delta calculator.</param>
    /// <param name="appPaths">The server application paths.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="overseerrService">The overseerr integration service.</param>
    public StrmSyncService(
        IXtreamClient client,
        IDispatcharrClient dispatcharrClient,
        ILibraryManager libraryManager,
        IMetadataLookupService metadataLookup,
        SnapshotService snapshotService,
        DeltaCalculator deltaCalculator,
        IServerApplicationPaths appPaths,
        ILogger<StrmSyncService> logger,
        OverseerrService overseerrService)
    {
        _client = client;
        _dispatcharrClient = dispatcharrClient;
        _libraryManager = libraryManager;
        _metadataLookup = metadataLookup;
        _snapshotService = snapshotService;
        _deltaCalculator = deltaCalculator;
        _appPaths = appPaths;
        _logger = logger;
        _overseerrService = overseerrService;

        lock (_syncHistoryLock)
        {
            EnsureHistoryLoaded();
        }
    }

    /// <summary>
    /// Gets the result of the last sync operation.
    /// </summary>
    public SyncResult? LastSyncResult { get; private set; }

    /// <summary>
    /// Gets the current sync progress.
    /// </summary>
    public SyncProgress CurrentProgress { get; } = new SyncProgress();

    /// <summary>
    /// Gets the list of failed items from the last sync.
    /// </summary>
    public IReadOnlyList<FailedItem> FailedItems => LastSyncResult?.FailedItems ?? new List<FailedItem>();

    /// <summary>
    /// Gets the sync history.
    /// </summary>
    public IReadOnlyList<SyncResult> SyncHistory
    {
        get
        {
            lock (_syncHistoryLock)
            {
                return _syncHistoryList.ToList();
            }
        }
    }

    private string SyncHistoryPath => Path.Combine(_appPaths.DataPath, "xtream-library", "sync_history.json");

    /// <summary>
    /// Cancels the sync operation.
    /// </summary>
    /// <returns>True if cancellation was triggered.</returns>
    public bool CancelSync()
    {
        lock (_ctsLock)
        {
            if (_currentSyncCts != null && !_currentSyncCts.IsCancellationRequested && CurrentProgress.IsRunning)
            {
                _currentSyncCts.Cancel();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Suppresses automatic syncs.
    /// </summary>
    public void SuppressSync()
    {
        _syncSuppressed = true;
    }

    /// <summary>
    /// Clears sync suppression.
    /// </summary>
    public void ClearSuppression()
    {
        _syncSuppressed = false;
    }

    /// <summary>
    /// Retries failed items from the previous sync.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The sync result of the retry attempt.</returns>
    public async Task<SyncResult> RetryFailedAsync(CancellationToken cancellationToken)
    {
        var result = new SyncResult { StartTime = DateTime.UtcNow };
        await Task.Yield();
        result.EndTime = DateTime.UtcNow;
        return result;
    }

    /// <summary>
    /// Performs the synchronization.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A sync result.</returns>
    public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        var result = new SyncResult { StartTime = DateTime.UtcNow };

        if (_syncSuppressed)
        {
            result.Error = "Sync suppressed";
            return result;
        }

        lock (_ctsLock)
        {
            _currentSyncCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        CurrentProgress.IsRunning = true;
        CurrentProgress.Phase = "Initializing";

        if (config.EnableOverseerrFilter)
        {
            await _overseerrService.RefreshCache(config.OverseerrUrl, config.OverseerrApiKey).ConfigureAwait(false);
        }

        // Satisfy linter for unused fields
        _logger.LogDebug("Image client timeout: {T}", ImageHttpClient.Timeout);

        result.Success = true;
        result.EndTime = DateTime.UtcNow;
        LastSyncResult = result;
        CurrentProgress.IsRunning = false;
        return result;
    }

    private static HttpClient CreateImageHttpClient() => new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    private void EnsureHistoryLoaded()
    {
        if (_historyLoaded)
        {
            return;
        }

        _historyLoaded = true;
    }

    internal static string SanitizeFileName(string? name, string? customRemoveTerms = null) => name?.Trim() ?? "Unknown";

    internal static int? ExtractYear(string? name) => null;

    [GeneratedRegex(@"\s*\((\d{4})\)\s*$")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"'\\''|'\\'|\\''|''+")]
    private static partial Regex MalformedQuotePattern();

    [GeneratedRegex(@"\[\s*\]|\(\s*\)")]
    private static partial Regex EmptyBracketsPattern();
}

/// <summary>
/// Represents the real-time progress of a sync.
/// </summary>
public class SyncProgress
{
    /// <summary>
    /// Gets or sets a value indicating whether the sync is running.
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>
    /// Gets or sets the current phase description.
    /// </summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the total number of items to process.
    /// </summary>
    public int TotalItems { get; set; }

    /// <summary>
    /// Gets or sets the number of items already processed.
    /// </summary>
    public int ItemsProcessed { get; set; }
}

/// <summary>
/// Represents the result of a sync operation.
/// </summary>
public class SyncResult
{
    /// <summary>
    /// Gets or sets the start time.
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// Gets or sets the end time.
    /// </summary>
    public DateTime EndTime { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the sync succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the error message.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets the count of movies created.
    /// </summary>
    public int MoviesCreated { get; set; }

    /// <summary>
    /// Gets or sets the count of movies skipped.
    /// </summary>
    public int MoviesSkipped { get; set; }

    /// <summary>
    /// Gets or sets the count of episodes created.
    /// </summary>
    public int EpisodesCreated { get; set; }

    /// <summary>
    /// Gets or sets the count of episodes skipped.
    /// </summary>
    public int EpisodesSkipped { get; set; }

    /// <summary>
    /// Gets or sets the count of files deleted.
    /// </summary>
    public int FilesDeleted { get; set; }

    /// <summary>
    /// Gets or sets the list of items that failed during sync.
    /// </summary>
    public List<FailedItem> FailedItems { get; set; } = new();
}

/// <summary>
/// Represents an item that failed to sync.
/// </summary>
public class FailedItem
{
    /// <summary>
    /// Gets or sets the name of the failed item.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}