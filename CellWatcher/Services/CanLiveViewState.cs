using CellWatcher.Models;

namespace CellWatcher.Services;

// In-memory tail of recently decoded CAN frames, for the Canbus tab's live view — deliberately
// never touches the database, so switching between Raw/Decoded and Identified/Unidentified in the
// UI is instant and the live view can't be slowed down by DB load. can_frame remains the
// authoritative persisted record; this is purely a fast, ephemeral read cache.
public sealed class CanLiveViewState
{
    private const int Capacity = 500;

    private readonly object _lock = new();
    private readonly LinkedList<LiveCanFrameEntry> _entries = new();

    public void Add(LiveCanFrameEntry entry)
    {
        lock (_lock)
        {
            _entries.AddLast(entry);
            if (_entries.Count > Capacity)
                _entries.RemoveFirst();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    // Newest first, optionally filtered by identified/unidentified and/or a specific set of CAN
    // IDs (the Canbus tab's per-frame-type filter dropdown), capped at limit. Filtering here
    // (rather than fetching unfiltered and trimming client-side) is what keeps "last 100" meaning
    // "last 100 matching the filter", not "last 100 total, then whatever's left after filtering".
    //
    // canIdFilter only ever constrains IDENTIFIED entries — an unidentified frame's CAN ID is by
    // definition not in the known-mapping list the filter dropdown is built from, so applying it
    // unconditionally would silently hide every unidentified frame even when "Unidentified" is
    // checked. The dropdown is specifically "which identified frame types to show", not a
    // generic CAN ID filter.
    public List<LiveCanFrameEntry> GetRecent(int limit, bool? identifiedFilter, HashSet<uint>? canIdFilter)
    {
        lock (_lock)
        {
            IEnumerable<LiveCanFrameEntry> source = _entries.Reverse();
            if (identifiedFilter is { } filter)
                source = source.Where(e => e.IsIdentified == filter);
            if (canIdFilter is not null)
                source = source.Where(e => !e.IsIdentified || canIdFilter.Contains(e.CanId));
            return source.Take(limit).ToList();
        }
    }

    // Coverage stat for the whole ring buffer (not just whatever's currently displayed/filtered)
    // — "what fraction of everything we've actually seen recently is identified", for the
    // Identified/Unidentified % display.
    public (int Total, int Identified) GetStats()
    {
        lock (_lock)
        {
            var identified = _entries.Count(e => e.IsIdentified);
            return (_entries.Count, identified);
        }
    }
}
