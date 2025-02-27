using System.Linq;
using Content.Server.Administration.Managers;
using Content.Server.Afk;
using Content.Server.Afk.Events;
using Content.Server.GameTicking;
using Content.Server.Mind;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Players;
using Content.Shared.Players.PlayTimeTracking;
using Content.Shared.Roles;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server.Players.PlayTimeTracking;

/// <summary>
/// Connects <see cref="PlayTimeTrackingManager"/> to the simulation state. Reports trackers and such.
/// </summary>
public sealed class PlayTimeTrackingSystem : EntitySystem
{
    [Dependency] private readonly IAfkManager _afk = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IAdminManager _adminManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly MindSystem _minds = default!;
    [Dependency] private readonly PlayTimeTrackingManager _tracking = default!;

    private const string AGhostPrototypeID = "AdminObserver"; //SS220-aghost-playtime

    public override void Initialize()
    {
        base.Initialize();

        _tracking.CalcTrackers += CalcTrackers;
        _adminManager.OnPermsChanged += AdminManager_OnPermsChanged;

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundEnd);
        SubscribeLocalEvent<PlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<PlayerDetachedEvent>(OnPlayerDetached);
        SubscribeLocalEvent<RoleAddedEvent>(OnRoleAdd);
        SubscribeLocalEvent<RoleRemovedEvent>(OnRoleRemove);
        SubscribeLocalEvent<AFKEvent>(OnAFK);
        SubscribeLocalEvent<UnAFKEvent>(OnUnAFK);
        SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<PlayerJoinedLobbyEvent>(OnPlayerJoinedLobby);
    }

    private void AdminManager_OnPermsChanged(Administration.AdminPermsChangedEventArgs obj)
    {
        if (_minds.TryGetSession(obj.Player.GetMind(), out var session))
            _tracking.QueueRefreshTrackers(session);
    }

    public override void Shutdown()
    {
        base.Shutdown();

        _tracking.CalcTrackers -= CalcTrackers;
    }

    private bool IsBypassingChecks(ICommonSession player)
    {
        return _adminManager.IsAdmin(player, true);
    }

    private void CalcTrackers(ICommonSession player, HashSet<string> trackers)
    {
        //SS220-aghost-playtime begin
        // Checking for attached entity to prevent tracking for players in lobby
        if (_afk.IsAfk(player) || player.AttachedEntity == null)
            return;

        if (_adminManager.IsAdmin(player, includeDeAdmin: false))
        {
            trackers.Add(PlayTimeTrackingShared.TrackerAdmin);
            if (player.AttachedEntity is { Valid: true } attachedEntity &&
                Comp<MetaDataComponent>(attachedEntity).EntityPrototype?.ID == AGhostPrototypeID)
                trackers.Add(PlayTimeTrackingShared.TrackerAGhost);
        }

        if (!IsPlayerAlive(player))
        {
            trackers.Add(PlayTimeTrackingShared.TrackerObserver);
            return;
        }
        //SS220-aghost-playtime end

        trackers.UnionWith(GetTimedRoles(player));
        trackers.Add(PlayTimeTrackingShared.TrackerOverall);
    }

    private bool IsPlayerAlive(ICommonSession session)
    {
        var attached = session.AttachedEntity;
        if (attached == null)
            return false;

        if (!TryComp<MobStateComponent>(attached, out var state))
            return false;

        return state.CurrentState is MobState.Alive or MobState.Critical;
    }

    public IEnumerable<string> GetTimedRoles(EntityUid mindId)
    {
        var ev = new MindGetAllRolesEvent(new List<RoleInfo>());
        RaiseLocalEvent(mindId, ref ev);

        foreach (var role in ev.Roles)
        {
            if (string.IsNullOrWhiteSpace(role.PlayTimeTrackerId))
                continue;

            yield return _prototypes.Index<PlayTimeTrackerPrototype>(role.PlayTimeTrackerId).ID;
        }
    }

    private IEnumerable<string> GetTimedRoles(ICommonSession session)
    {
        var contentData = _playerManager.GetPlayerData(session.UserId).ContentData();

        if (contentData?.Mind == null)
            return Enumerable.Empty<string>();

        return GetTimedRoles(contentData.Mind.Value);
    }

    private void OnRoleRemove(RoleRemovedEvent ev)
    {
        if (_minds.TryGetSession(ev.Mind, out var session))
            _tracking.QueueRefreshTrackers(session);
    }

    private void OnRoleAdd(RoleAddedEvent ev)
    {
        if (_minds.TryGetSession(ev.Mind, out var session))
            _tracking.QueueRefreshTrackers(session);
    }

    private void OnRoundEnd(RoundRestartCleanupEvent ev)
    {
        _tracking.Save();
    }

    private void OnUnAFK(ref UnAFKEvent ev)
    {
        _tracking.QueueRefreshTrackers(ev.Session);
    }

    private void OnAFK(ref AFKEvent ev)
    {
        _tracking.QueueRefreshTrackers(ev.Session);
    }

    private void OnPlayerAttached(PlayerAttachedEvent ev)
    {
        _tracking.QueueRefreshTrackers(ev.Player);
    }

    private void OnPlayerDetached(PlayerDetachedEvent ev)
    {
        // This doesn't fire if the player doesn't leave their body. I guess it's fine?
        _tracking.QueueRefreshTrackers(ev.Player);
    }

    private void OnMobStateChanged(MobStateChangedEvent ev)
    {
        if (!TryComp(ev.Target, out ActorComponent? actor))
            return;

        _tracking.QueueRefreshTrackers(actor.PlayerSession);
    }

    private void OnPlayerJoinedLobby(PlayerJoinedLobbyEvent ev)
    {
        _tracking.QueueRefreshTrackers(ev.PlayerSession);
        // Send timers to client when they join lobby, so the UIs are up-to-date.
        _tracking.QueueSendTimers(ev.PlayerSession);
    }

    public bool IsAllowed(ICommonSession player, string role)
    {
        if (IsBypassingChecks(player))
            return true;

        if (!_prototypes.TryIndex<JobPrototype>(role, out var job) ||
            job.Requirements == null ||
            !_cfg.GetCVar(CCVars.GameRoleTimers))
            return true;

        if (!_tracking.TryGetTrackerTimes(player, out var playTimes))
        {
            Log.Error($"Unable to check playtimes {Environment.StackTrace}");
            playTimes = new Dictionary<string, TimeSpan>();
        }

        return JobRequirements.TryRequirementsMet(job, playTimes, out _, EntityManager, _prototypes);
    }

    public HashSet<string> GetDisallowedJobs(ICommonSession player)
    {
        var roles = new HashSet<string>();
        if (IsBypassingChecks(player))
            return roles;

        if (!_cfg.GetCVar(CCVars.GameRoleTimers))
            return roles;

        if (!_tracking.TryGetTrackerTimes(player, out var playTimes))
        {
            Log.Error($"Unable to check playtimes {Environment.StackTrace}");
            playTimes = new Dictionary<string, TimeSpan>();
        }

        foreach (var job in _prototypes.EnumeratePrototypes<JobPrototype>())
        {
            if (job.Requirements != null)
            {
                foreach (var requirement in job.Requirements)
                {
                    if (JobRequirements.TryRequirementMet(requirement, playTimes, out _, EntityManager, _prototypes))
                        continue;

                    goto NoRole;
                }
            }

            roles.Add(job.ID);
            NoRole:;
        }

        return roles;
    }

    public void RemoveDisallowedJobs(NetUserId userId, ref List<string> jobs)
    {
        if (!_cfg.GetCVar(CCVars.GameRoleTimers))
            return;

        var player = _playerManager.GetSessionById(userId);
        if (IsBypassingChecks(player))
            return;

        if (!_tracking.TryGetTrackerTimes(player, out var playTimes))
        {
            // Sorry mate but your playtimes haven't loaded.
            Log.Error($"Playtimes weren't ready yet for {player} on roundstart!");
            playTimes ??= new Dictionary<string, TimeSpan>();
        }

        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];

            if (!_prototypes.TryIndex<JobPrototype>(job, out var jobber) ||
                jobber.Requirements == null ||
                jobber.Requirements.Count == 0)
                continue;

            foreach (var requirement in jobber.Requirements)
            {
                if (JobRequirements.TryRequirementMet(requirement, playTimes, out _, EntityManager, _prototypes))
                    continue;

                jobs.RemoveSwap(i);
                i--;
                break;
            }
        }
    }

    public void PlayerRolesChanged(ICommonSession player)
    {
        _tracking.QueueRefreshTrackers(player);
    }
}
