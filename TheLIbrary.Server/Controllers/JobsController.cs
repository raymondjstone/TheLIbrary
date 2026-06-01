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
    private readonly UnknownAuthorAdoptionService _adoptUnknownAuthors;
    private readonly StarredAuthorRefreshService _refreshStarred;
    private readonly ForeignArchiveService _archiveForeign;
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
        UnknownAuthorAdoptionService adoptUnknownAuthors,
        StarredAuthorRefreshService refreshStarred,
        ForeignArchiveService archiveForeign,
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
        _adoptUnknownAuthors = adoptUnknownAuthors;
        _refreshStarred = refreshStarred;
        _archiveForeign = archiveForeign;
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
            adoptUnknownAuthors = new { isRunning = _adoptUnknownAuthors.IsRunning, message = _adoptUnknownAuthors.CurrentMessage },
            refreshStarred = new { isRunning = _refreshStarred.IsRunning, message = _refreshStarred.CurrentMessage },
            archiveForeign = new { isRunning = _archiveForeign.IsRunning, message = _archiveForeign.CurrentMessage },
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
}
