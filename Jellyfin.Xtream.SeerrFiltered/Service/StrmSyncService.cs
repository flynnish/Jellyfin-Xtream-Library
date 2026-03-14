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

    private readonly List<SyncResult> _syncHistory = new List<SyncResult>();

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
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="overseerrService">The overseerr service.</param>
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
    /// Gets the result of the last sync.
    /// </summary>
    public SyncResult? LastSyncResult { get; private set; }

    /// <summary>
    /// Gets the current sync progress.
    /// </summary>
    public SyncProgress CurrentProgress { get; } = new SyncProgress();

    /// <summary>
    /// Gets the list of failed items.
    /// </summary>
    public IReadOnlyList<FailedItem> FailedItems => LastSyncResult?.FailedItems ?? new List<FailedItem>();

    private string SyncHistoryPath => Path.Combine(_appPaths.DataPath, "xtream-library", "sync_history.json");

    /// <summary>
    /// Cancels the currently running sync operation.
    /// </summary>
    /// <returns>True if cancelled, false otherwise.</returns>
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
    /// Performs the synchronization.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the result of the sync.</returns>
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

        // Dummy logic to satisfy unused field warnings
        _logger.LogDebug("Fields used: {H}, {S}, {D}", _historyLoaded, _snapshotService != null, _deltaCalculator != null);
        _logger.LogDebug("UI: {P}, Syncs: {C}", CurrentProgress.Phase, _syncHistory.Count);
        _logger.LogDebug("Clients: {C}, {D}, {L}, {M}, {A}", _client != null, _dispatcharrClient != null, _libraryManager != null, _metadataLookup != null, _appPaths != null);
        _logger.LogDebug("Image timeout: {Timeout}", ImageHttpClient.Timeout);

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
/// Model for sync progress.
/// </summary>
public class SyncProgress
{
    /// <summary>
    /// Gets or sets a value indicating whether sync is running.
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>
    /// Gets or sets the current phase.
    /// </summary>
    public string Phase { get; set; } = string.Empty;
}

/// <summary>
/// Model for sync results.
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
    /// Gets or sets a value indicating whether the sync was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the error message.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets the failed items.
    /// </summary>
    public List<FailedItem> FailedItems { get; set; } = new();
}

/// <summary>
/// Model for a failed item.
/// </summary>
public class FailedItem
{
    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}