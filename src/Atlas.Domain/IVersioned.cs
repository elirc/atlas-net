namespace Atlas.Domain;

/// <summary>
/// Entities whose updates can race (lifecycle transitions, approvals) carry an
/// integer version used as an optimistic-concurrency token: the DbContext bumps
/// it on every update and a stale save fails instead of silently overwriting.
/// </summary>
public interface IVersioned
{
    int Version { get; set; }
}
