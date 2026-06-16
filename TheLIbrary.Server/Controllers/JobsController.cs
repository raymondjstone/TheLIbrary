using Microsoft.AspNetCore.Mvc;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/jobs")]
public class JobsController : ControllerBase
{
    private readonly SeriesOrganizerService _organizer;
    private readonly UnzipService _unzip;
    private readonly AuthorFolderDisambiguatorService _disambiguator;
    private readonly SameNameAuthorService _sameNames;
    private readonly PhysicalAuthorStarService _physicalStars;
    private readonly OpenLibraryMetadataCacheService _metadataCache;
    private readonly UnknownFolderFlattenerService _flattenUnknown;
    private readonly UnknownDuplicateRemovalService _dedupeUnknown;
    private readonly AuthorDuplicateRemovalService _dedupeAuthorFiles;
    private readonly ManualBookPromotionService _promoteManualBooks;
    private readonly UnknownAuthorAdoptionService _adoptUnknownAuthors;
    private readonly StarredAuthorRefreshService _refreshStarred;
    private readonly ForeignArchiveService _archiveForeign;
    private readonly LinkedAuthorMergeService _mergeLinkedAuthors;
    private readonly BookIntegrityService _checkIntegrity;
    private readonly StaleFileCleanupService _staleFiles;
    private readonly ContentScanService _contentScan;
    private readonly UntrackedAuthorAssignmentService _assignAuthors;
    private readonly TheLibrary.Server.Services.Search.FullTextSearchService _fullText;
    private readonly AuthorPruneService _pruneAuthors;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly OpenLibraryRateLimiter _rateLimiter;
    private readonly OpenLibrarySettings _olSettings;

    public JobsController(
        SeriesOrganizerService organizer,
        UnzipService unzip,
        AuthorFolderDisambiguatorService disambiguator,
        SameNameAuthorService sameNames,
        PhysicalAuthorStarService physicalStars,
        OpenLibraryMetadataCacheService metadataCache,
        UnknownFolderFlattenerService flattenUnknown,
        UnknownDuplicateRemovalService dedupeUnknown,
        AuthorDuplicateRemovalService dedupeAuthorFiles,
        ManualBookPromotionService promoteManualBooks,
        UnknownAuthorAdoptionService adoptUnknownAuthors,
        StarredAuthorRefreshService refreshStarred,
        ForeignArchiveService archiveForeign,
        LinkedAuthorMergeService mergeLinkedAuthors,
        BookIntegrityService checkIntegrity,
        StaleFileCleanupService staleFiles,
        ContentScanService contentScan,
        UntrackedAuthorAssignmentService assignAuthors,
        TheLibrary.Server.Services.Search.FullTextSearchService fullText,
        AuthorPruneService pruneAuthors,
        BackgroundTaskCoordinator coordinator,
        IHostApplicationLifetime lifetime,
        OpenLibraryRateLimiter rateLimiter,
        OpenLibrarySettings olSettings)
    {
        _organizer = organizer;
        _unzip = unzip;
        _disambiguator = disambiguator;
        _sameNames = sameNames;
        _physicalStars = physicalStars;
        _metadataCache = metadataCache;
        _flattenUnknown = flattenUnknown;
        _dedupeUnknown = dedupeUnknown;
        _dedupeAuthorFiles = dedupeAuthorFiles;
        _promoteManualBooks = promoteManualBooks;
        _adoptUnknownAuthors = adoptUnknownAuthors;
        _refreshStarred = refreshStarred;
        _archiveForeign = archiveForeign;
        _mergeLinkedAuthors = mergeLinkedAuthors;
        _checkIntegrity = checkIntegrity;
        _staleFiles = staleFiles;
        _contentScan = contentScan;
        _assignAuthors = assignAuthors;
        _fullText = fullText;
        _pruneAuthors = pruneAuthors;
        _coordinator = coordinator;
        _lifetime = lifetime;
        _rateLimiter = rateLimiter;
        _olSettings = olSettings;
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        // Effective rate: demoted → 1/s (anonymous), identified → 3/s, else 1/s anonymous.
        var ratePerSecond = (!_rateLimiter.IsDemoted && _olSettings.IsIdentified) ? 3 : 1;
        var rateTier = _rateLimiter.IsDemoted ? "demoted (rate-limited by OL)"
                     : _olSettings.IsIdentified ? "identified (contact email set)"
                     : "anonymous (no contact email)";

        return Ok(new
        {
            activeJob = _coordinator.CurrentHolder,
            openLibraryRatePerSecond = ratePerSecond,
            openLibraryRateTier = rateTier,
            organizer = new { isRunning = _organizer.IsRunning, message = _organizer.CurrentMessage },
            unzip = new { isRunning = _unzip.IsRunning, message = _unzip.CurrentMessage },
            disambiguator = new { isRunning = _disambiguator.IsRunning, message = _disambiguator.CurrentMessage },
            sameNames = new { isRunning = _sameNames.IsRunning, message = _sameNames.CurrentMessage },
            physicalStars = new { isRunning = _physicalStars.IsRunning, message = _physicalStars.CurrentMessage },
            metadataCache = new { isRunning = _metadataCache.IsRunning, message = _metadataCache.CurrentMessage },
            flattenUnknown = new { isRunning = _flattenUnknown.IsRunning, message = _flattenUnknown.CurrentMessage },
            dedupeUnknown = new { isRunning = _dedupeUnknown.IsRunning, message = _dedupeUnknown.CurrentMessage },
            dedupeAuthorFiles = new { isRunning = _dedupeAuthorFiles.IsRunning, message = _dedupeAuthorFiles.CurrentMessage },
            promoteManualBooks = new { isRunning = _promoteManualBooks.IsRunning, message = _promoteManualBooks.CurrentMessage },
            adoptUnknownAuthors = new { isRunning = _adoptUnknownAuthors.IsRunning, message = _adoptUnknownAuthors.CurrentMessage },
            refreshStarred = new { isRunning = _refreshStarred.IsRunning, message = _refreshStarred.CurrentMessage },
            archiveForeign = new { isRunning = _archiveForeign.IsRunning, message = _archiveForeign.CurrentMessage },
            mergeLinkedAuthors = new { isRunning = _mergeLinkedAuthors.IsRunning, message = _mergeLinkedAuthors.CurrentMessage },
            checkIntegrity = new { isRunning = _checkIntegrity.IsRunning, message = _checkIntegrity.CurrentMessage },
            staleFiles = new { isRunning = _staleFiles.IsRunning, message = _staleFiles.CurrentMessage },
            contentScan = new { isRunning = _contentScan.IsRunning, message = _contentScan.CurrentMessage },
            assignAuthors = new { isRunning = _assignAuthors.IsRunning, message = _assignAuthors.CurrentMessage },
            fullTextIndex = new { isRunning = _fullText.IsRunning, message = _fullText.CurrentMessage },
            pruneAuthors = new { isRunning = _pruneAuthors.IsRunning, message = _pruneAuthors.CurrentMessage },
        });
    }

    [HttpPost("organizer/start")]
    public IActionResult StartOrganizer()
    {
        if (!_organizer.TryStart(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted();
    }

    [HttpPost("unzip/start")]
    public IActionResult StartUnzip()
    {
        if (!_unzip.TryStart(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted();
    }

    [HttpPost("disambiguator/start")]
    public IActionResult StartDisambiguator()
    {
        if (!_disambiguator.TryStart(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted();
    }

    [HttpPost("same-names/start")]
    public IActionResult StartSameNames()
    {
        if (!_sameNames.TryStart(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted();
    }

    [HttpPost("physical-stars/start")]
    public IActionResult StartPhysicalStars()
    {
        if (!_physicalStars.TryStart(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted();
    }

    [HttpPost("metadata-cache/start")]
    public IActionResult StartMetadataCache()
    {
        if (!_metadataCache.TryStart(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted();
    }

    [HttpPost("flatten-unknown/start")]
    public IActionResult StartFlattenUnknown()
    {
        if (!_flattenUnknown.TryStart(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted();
    }

    [HttpPost("dedupe-unknown/start")]
    public IActionResult StartDedupeUnknown()
    {
        if (!_dedupeUnknown.TryStart(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted();
    }

    [HttpPost("dedupe-author-files/start")]
    public IActionResult StartDedupeAuthorFiles()
    {
        if (!_dedupeAuthorFiles.TryStart(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted();
    }

    [HttpPost("promote-manual-books/start")]
    public IActionResult StartPromoteManualBooks()
    {
        if (!_promoteManualBooks.TryStart(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted();
    }

    [HttpPost("adopt-unknown-authors/start")]
    public IActionResult StartAdoptUnknownAuthors()
    {
        if (!_adoptUnknownAuthors.TryStart(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted();
    }

    [HttpPost("refresh-starred/start")]
    public IActionResult StartRefreshStarred()
    {
        if (!_refreshStarred.TryStart(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted();
    }

    [HttpPost("archive-foreign/start")]
    public IActionResult StartArchiveForeign()
    {
        if (!_archiveForeign.TryStart(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted();
    }

    [HttpPost("merge-linked-authors/start")]
    public IActionResult StartMergeLinkedAuthors()
    {
        if (!_mergeLinkedAuthors.TryStart(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted();
    }

    [HttpPost("check-integrity/start")]
    public IActionResult StartCheckIntegrity()
    {
        if (!_checkIntegrity.TryStart(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted();
    }

    [HttpPost("prune-stale-files/start")]
    public IActionResult StartPruneStaleFiles()
    {
        if (!_staleFiles.TryStart(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted();
    }

    [HttpPost("content-scan/start")]
    public IActionResult StartContentScan()
    {
        if (!_contentScan.TryStart(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted();
    }

    [HttpPost("assign-authors/start")]
    public IActionResult StartAssignAuthors()
    {
        if (!_assignAuthors.TryStart(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted();
    }

    [HttpPost("index-fulltext/start")]
    public IActionResult StartIndexFullText()
    {
        if (!_fullText.TryStart(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted();
    }

    [HttpPost("prune-authors/start")]
    public IActionResult StartPruneAuthors()
    {
        if (!_pruneAuthors.TryStart(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted();
    }
}
