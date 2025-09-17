using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Cvars.Validators;
using CounterStrikeSharp.API.Modules.Timers;
using MapChooserSharp.API.Events;
using MapChooserSharp.API.Events.MapCycle;
using MapChooserSharp.API.Events.MapVote;
using MapChooserSharp.API.Events.MapCycle;
using MapChooserSharp.API.MapConfig;
using MapChooserSharp.API.MapVoteController;
using MapChooserSharp.Interfaces;
using MapChooserSharp.Modules.MapConfig.Interfaces;
using MapChooserSharp.Modules.MapCycle.Interfaces;
using MapChooserSharp.Modules.MapCycle.Services;
using MapChooserSharp.Modules.MapVote.Interfaces;
using MapChooserSharp.Modules.McsDatabase.Interfaces;
using MapChooserSharp.Modules.PluginConfig.Interfaces;
using MapChooserSharp.Modules.RockTheVote;
using MapChooserSharp.Modules.RockTheVote.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TNCSSPluginFoundation.Models.Plugin;
using TNCSSPluginFoundation.Utils.Other;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace MapChooserSharp.Modules.MapCycle;

internal enum MapChangeBehaviourType
{
    Immediately = 0,
    NextRoundEnd = 1,
    WhenTimeRunsOut = 2
}

internal sealed class McsMapCycleController(IServiceProvider serviceProvider, bool hotReload) : PluginModuleBase(serviceProvider), IMcsInternalMapCycleControllerApi
{
    public override string PluginModuleName => "McsMapCycleController";
    public override string ModuleChatPrefix => "unused";
    protected override bool UseTranslationKeyInModuleChatPrefix => false;

    private IMcsInternalEventManager _mcsEventManager = null!;
    private IMcsInternalMapConfigProviderApi _mcsInternalMapConfigProviderApi = null!;
    private IMcsPluginConfigProvider _mcsPluginConfigProvider = null!;
    private IMcsInternalMapVoteControllerApi _mcsMapVoteController = null!;
    private IMcsInternalRtvControllerApi _mcsRtvController = null!;
    private McsMapConfigExecutionService _mapConfigExecutionService = null!;
    private IMcsDatabaseProvider _mcsDatabaseProvider = null!;
    private ITimeLeftUtil _timeLeftUtil = null!;

    
    private IMapConfig? _nextMap = null;
    // Preserve intended next map when changing to workshop maps,
    // used to notify players if server loads <empty> during download
    private IMapConfig? _lastIntendedNextMap = null;

    public IMapConfig? NextMap
    {
        get => _nextMap;
        private set => _nextMap = value;
    }
    
    public bool IsNextMapConfirmed => _nextMap != null;

    public bool ChangeMapOnNextRoundEnd { get; set; } = false;

    private bool _isCurrentVoteRtv = false;
    private bool _isCurrentVoteTimeBased = false;

    private IMapConfig? _currentMap = null;

    public IMapConfig? CurrentMap
    {
        get
        {
            return _currentMap ??= _mcsInternalMapConfigProviderApi.GetMapConfig(Server.MapName);
        }
        private set => _currentMap = value;
    }

    public int ExtendCount { get; private set; } = 0;
    private int ExtendLimit { get; set; } = 0;
    
    public int ExtendsLeft => ExtendLimit - ExtendCount;
    
    public bool SetNextMap(IMapConfig mapConfig)
    {
        NextMap = mapConfig;

        FireNextMapChangedEvent(NextMap);
        return true;
    }

    public bool SetNextMap(string mapName)
    {
        IMapConfig? mapConfig = _mcsInternalMapConfigProviderApi.GetMapConfig(mapName);

        if (mapConfig == null)
            return false;

        NextMap = mapConfig;

        FireNextMapChangedEvent(NextMap);
        return true;
    }

    public bool RemoveNextMap()
    {
        if (NextMap == null)
            return false;
        
        FireNextMapRemovedEvent(NextMap);
        
        _mapChangeTimer?.Kill();
        ChangeMapOnNextRoundEnd = false;
        NextMap = null;
        RecreateVoteTimer();
        return true;
    }

    
    private int DefaultMapExtends => _mcsPluginConfigProvider.PluginConfig.MapCycleConfig.FallbackDefaultMaxExtends;


    private bool _isMapStarted = false;

    private Timer? _voteStartTimer = null;

    private Timer? _mapChangeTimer = null;

    private Timer? _downloading = null;

    private const float VoteStartCheckInterval = 1.0F;
    
    private const float DefaultRoundRestartDelay = 7.0F;

    private const float DefaultMapChangeDelay = 10.0F;
    
    
    // Those variables are used for avoid unexpected cooldown reduction when server startup
    private bool IsFirstMapEnded { get; set; }
    private bool IsSecondMapIsPassed { get; set; }

    public readonly FakeConVar<int> VoteStartTimingTime = new("mcs_vote_start_timing_time", "When should vote started if map is based on mp_timelimit or mp_roundtime? (seconds)", 180,
        ConVarFlags.FCVAR_NONE, new RangeValidator<int>(0, 600));

    public readonly FakeConVar<int> VoteStartTimingRound = new("mcs_vote_start_timing_round", "When should vote started if map is based on mp_maxrounds? (rounds)", 2,
        ConVarFlags.FCVAR_NONE, new RangeValidator<int>(2, 15));

    // Map change timing settings
    public readonly FakeConVar<int> TimeBasedVoteMapChangeBehaviour = new("mcs_time_based_vote_map_change_behaviour",
        "Map change timing for time-based votes. 0: Immediately, 1: Next round end, 2: When time runs out", 2,
        ConVarFlags.FCVAR_NONE, new RangeValidator<int>(0, 2));

    public readonly FakeConVar<float> TimeBasedVoteMapChangeDelay = new("mcs_time_based_vote_map_change_delay",
        "Delay in seconds after conditions are met for time-based votes", 3.0F,
        ConVarFlags.FCVAR_NONE, new RangeValidator<float>(0.0F, 60.0F));

    public readonly FakeConVar<int> RtvMapChangeBehaviour = new("mcs_rtv_map_change_behaviour",
        "Map change timing for RTV votes. 0: Immediately, 1: Next round end, 2: When time runs out", 2,
        ConVarFlags.FCVAR_NONE, new RangeValidator<int>(0, 2));

    public readonly FakeConVar<float> RtvMapChangeDelay = new("mcs_rtv_map_change_delay",
        "Delay in seconds after conditions are met for RTV votes", 3.0F,
        ConVarFlags.FCVAR_NONE, new RangeValidator<float>(0.0F, 60.0F));

    public readonly FakeConVar<float> IntermissionMapChangeDelay = new("mcs_intermission_map_change_delay",
        "Delay in seconds for map change during intermission (Cs2EndMatchScreen)", 10.0F,
        ConVarFlags.FCVAR_NONE, new RangeValidator<float>(0.0F, 120.0F));
    
    protected override void OnInitialize()
    {
        TrackConVar(VoteStartTimingTime);
        TrackConVar(VoteStartTimingRound);
        TrackConVar(TimeBasedVoteMapChangeBehaviour);
        TrackConVar(TimeBasedVoteMapChangeDelay);
        TrackConVar(RtvMapChangeBehaviour);
        TrackConVar(RtvMapChangeDelay);
        TrackConVar(IntermissionMapChangeDelay);
    }
    
    public override void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IMcsInternalMapCycleControllerApi>(this);
    }

    protected override void OnAllPluginsLoaded()
    {
        _mcsMapVoteController = ServiceProvider.GetRequiredService<IMcsInternalMapVoteControllerApi>();
        _mcsRtvController = ServiceProvider.GetRequiredService<IMcsInternalRtvControllerApi>();
        _mcsInternalMapConfigProviderApi = ServiceProvider.GetRequiredService<IMcsInternalMapConfigProviderApi>();
        _mcsPluginConfigProvider = ServiceProvider.GetRequiredService<IMcsPluginConfigProvider>();
        _mcsEventManager = ServiceProvider.GetRequiredService<IMcsInternalEventManager>();
        _timeLeftUtil = ServiceProvider.GetRequiredService<ITimeLeftUtil>();
        _mcsDatabaseProvider = ServiceProvider.GetRequiredService<IMcsDatabaseProvider>();
        _mapConfigExecutionService = ServiceProvider.GetRequiredService<McsMapConfigExecutionService>();
        
        _mcsEventManager.RegisterEventHandler<McsNextMapConfirmedEvent>(OnNextMapConfirmed);
        _mcsEventManager.RegisterEventHandler<McsMapExtendEvent>(OnMapExtended);
        _mcsEventManager.RegisterEventHandler<McsMapNotChangedEvent>(OnMapNotChanged);
        
        Plugin.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        Plugin.RegisterListener<Listeners.OnClientPutInServer>(OnClientPutInServer);
        Plugin.RegisterListener<Listeners.OnMapEnd>(() =>
        {
            _isMapStarted = false;
            ChangeMapOnNextRoundEnd = false;
            IsFirstMapEnded = true;
        });
        Plugin.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        Plugin.RegisterEventHandler<EventCsIntermission>(OnIntermission);
        
        // This is for late timer start
        // Since we cannot obtain McsMapExtendType before map is fully loaded
        // So we'll wait for first round started
        Plugin.RegisterEventHandler<EventRoundPoststart>((@event, info) =>
        {
            if (_isMapStarted)
                return HookResult.Continue;
            
            _timeLeftUtil.ReDetermineExtendType();
            _isMapStarted = true;
            RecreateVoteTimer();
            return HookResult.Continue;
        });

        if (hotReload)
        {
            OnMapStart(Server.MapName);
            _timeLeftUtil.ReDetermineExtendType();
            _isMapStarted = true;
            RecreateVoteTimer();
            IsFirstMapEnded = true;
            IsSecondMapIsPassed = true;
        }
    }

    protected override void OnUnloadModule()
    {
        _mcsEventManager.UnregisterEventHandler<McsNextMapConfirmedEvent>(OnNextMapConfirmed);
        _mcsEventManager.UnregisterEventHandler<McsMapExtendEvent>(OnMapExtended);
        _mcsEventManager.UnregisterEventHandler<McsMapNotChangedEvent>(OnMapNotChanged);
        
        Plugin.RemoveListener<Listeners.OnMapStart>(OnMapStart);
        Plugin.DeregisterEventHandler<EventRoundEnd>(OnRoundEnd);
        Plugin.DeregisterEventHandler<EventCsIntermission>(OnIntermission);
    }


    public void ChangeToNextMap(float seconds)
    {
        if (seconds < 0.0F)
            seconds = DefaultMapChangeDelay;

        _mapChangeTimer = Plugin.AddTimer(seconds, ChangeToNextMapInternal, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void ChangeToNextMapInternal()
    {
        //var defaultMapName = _mcsPluginConfigProvider.PluginConfig.MapCycleConfig.DefaultMap ?? "de_dust2";
        //if (NextMap == null)
        //{
        //    if (!string.IsNullOrEmpty(defaultMapName))
        //    {
        //        //SetNextMap(defaultMapName);
        //        if (NextMap == null)
        //        {
        //            Logger.LogError($"Failed to change map: default map '{defaultMapName}' could not be found");
        //            return;
        //        }
        //        Logger.LogInformation($"Using default map '{defaultMapName}' since next map was null");
        //    }
        //    else
        //    {
        //        Logger.LogError("Failed to change map: next map is null and no default map configured");
        //        return;
        //    }
        //}
        //else if (NextMap != null && (string.IsNullOrEmpty(NextMap.MapName) || NextMap.WorkshopId < 0))
        //{
        //    SetNextMap(defaultMapName);
        //    Logger.LogError("Failed to change map: Invalid next map configuration: " +
        //                    $"MapName='{NextMap.MapName}', WorkshopId='{NextMap.WorkshopId}'");
        //    Logger.LogInformation($"Using default map '{defaultMapName}' since next map was invalid");
        //}

        if (NextMap == null)
        {
            Logger.LogError("Failed to change map: next map is null");
            return;
        }

        // Fire IntermissionEndEvent before changing map
        var intermissionEndEvent = new McsIntermissionEndEvent(GetTextWithPluginPrefix(null, ""),
            CurrentMap ?? _mcsInternalMapConfigProviderApi.GetMapConfig(Server.MapName), NextMap);
        _mcsEventManager.FireEventNoResult(intermissionEndEvent);

        if (_mcsPluginConfigProvider.PluginConfig.MapCycleConfig.ShouldStopSourceTvRecording)
        {
            Logger.LogInformation("Executing tv_stoprecord before map change to prevent server crash.");
            Server.ExecuteCommand("tv_stoprecord");
        }

        DebugLogger.LogDebug("Changing to next map!");
        long workshopId = NextMap.WorkshopId;

        var previousMap = CurrentMap ?? _mcsInternalMapConfigProviderApi.GetMapConfig(Server.MapName);

        // Preserve intended next map to notify players when server temporarily loads <empty> while downloading
        _lastIntendedNextMap = NextMap;

        if (workshopId == 0)
        {
            DebugLogger.LogInformation($"No workshop ID defined! We will try change level with {NextMap.MapName}");
            // Use MapUtil.ChangeMap(string) instead of MapUtil.ChangeToWorkshopMap(string)
            // Because, This IMapConfig is no guarantee official map or not.
            MapUtil.ChangeMap(NextMap.MapName);

            return;
        }

        DebugLogger.LogInformation($"We will try to change map to {NextMap.MapName} with workshop ID: {workshopId}");
        MapUtil.ChangeToWorkshopMap(workshopId);
    }

    private void OnClientPutInServer(int slot)
    {
        // TODO if current map is official maps, then set IsSecondMapIsPassed to true.
        if (IsSecondMapIsPassed || !IsFirstMapEnded)
            return;
        
        IsSecondMapIsPassed = true;
    }

    private void OnMapStart(string mapName)
    {
        // Hard-kill any leftover map-change timer on map start to avoid post-change re-entry
        //_mapChangeTimer?.Kill();
        //_mapChangeTimer = null;
        _downloading?.Kill();
        _downloading = null;

        ExtendCount = 0;

        DecrementAllMapCooldown(CurrentMap);

        // If server temporarily moved to <empty> during workshop download, inform players once
        if (mapName == "<empty>")
        {
            if (_lastIntendedNextMap != null)
            {
                _downloading = Plugin.AddTimer(10.0F, () =>
                {
                    if (_lastIntendedNextMap != null)
                    {
                        PrintLocalizedChatToAll("MapCycle.Broadcast.DownloadingWorkshopMap",
                            _mcsInternalMapConfigProviderApi.GetMapName(_lastIntendedNextMap));
                    }
                }, TimerFlags.REPEAT);
            }
        }
        else
        {
            // Clear the preserved map when a non-empty map actually starts
            _lastIntendedNextMap = null;
        }

        CurrentMap = NextMap;
        NextMap = null;
        

        // Wait for first people joined
        // TODO() Maybe we can use Server.NextWorldUpdate() to execute things?
        Plugin.AddTimer(0.1F, () =>
        {
            ObtainCurrentMap(mapName);
            _mapConfigExecutionService.ExecuteMapConfigs();
        });
    }

    private void ObtainCurrentMap(string mapName)
    {
        // This is extra check for server startup
        if (CurrentMap == null)
        {
            CurrentMap = _mcsInternalMapConfigProviderApi.GetMapConfig(mapName);
            
            // If map name isn't match with config then find with workshop ID
            if (CurrentMap == null)
            {
                if (long.TryParse(ForceFullUpdate.GetWorkshopId(), out long workshopId))
                {
                    // Find map config my workshopId
                    // But if not find with this way, CurrentMap become null.
                    CurrentMap = _mcsInternalMapConfigProviderApi.GetMapConfig(workshopId);
                }
            }
        }

        ExtendLimit = CurrentMap?.MaxExtends ?? DefaultMapExtends;
    }

    private void DecrementAllMapCooldown(IMapConfig? previousMap)
    {
        // To prevent unxpected cooldown reduction
        if (!IsFirstMapEnded || !IsSecondMapIsPassed)
            return;
        
        // Decrement all cooldowns
        _mcsDatabaseProvider.MapInfoRepository.DecrementAllCooldownsAsync().ConfigureAwait(false);
        _mcsDatabaseProvider.GroupInfoRepository.DecrementAllCooldownsAsync().ConfigureAwait(false);
        foreach (var (key, value) in _mcsInternalMapConfigProviderApi.GetMapConfigs())
        {
            foreach (IMapGroupSettings setting in value.GroupSettings)
            {
                if (setting.GroupCooldown.CurrentCooldown > 0)
                    setting.GroupCooldown.CurrentCooldown--;
            }

            if (value.MapCooldown.CurrentCooldown > 0)
                value.MapCooldown.CurrentCooldown--;
        }

        // Set previous map cooldown if defined in config
        if (previousMap != null)
        {
            _mcsDatabaseProvider.MapInfoRepository.UpsertMapCooldownAsync(previousMap.MapName, previousMap.MapCooldown.MapConfigCooldown).ConfigureAwait(false);
            previousMap.MapCooldown.CurrentCooldown = previousMap.MapCooldown.MapConfigCooldown;
            
            foreach (IMapGroupSettings setting in previousMap.GroupSettings)
            {
                _mcsDatabaseProvider.GroupInfoRepository.UpsertGroupCooldownAsync(setting.GroupName, setting.GroupCooldown.MapConfigCooldown).ConfigureAwait(false);
                setting.GroupCooldown.CurrentCooldown = setting.GroupCooldown.MapConfigCooldown;
            }
        }
    }

        
    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (!_isMapStarted)
            return HookResult.Continue;

        if (NextMap == null)
            return HookResult.Continue;

        // Check if we should change map on round end based on current vote type
        bool shouldChangeMap = false;
        float delay = 1.0F;

        if (_isCurrentVoteRtv)
        {
            // RTV vote handling
            var rtvBehaviour = (MapChangeBehaviourType)RtvMapChangeBehaviour.Value;
            if (rtvBehaviour == MapChangeBehaviourType.NextRoundEnd)
            {
                shouldChangeMap = true;
                delay = RtvMapChangeDelay.Value;
            }
        }
        else if (_isCurrentVoteTimeBased)
        {
            // Time-based vote handling
            var timeBasedBehaviour = (MapChangeBehaviourType)TimeBasedVoteMapChangeBehaviour.Value;
            if (timeBasedBehaviour == MapChangeBehaviourType.NextRoundEnd)
            {
                shouldChangeMap = true;
                delay = TimeBasedVoteMapChangeDelay.Value;
            }
        }
        //else if (ChangeMapOnNextRoundEnd)
        //{
        //    // Legacy handling
        //    shouldChangeMap = true;
        //    delay = 1.0F;
        //}

        if (shouldChangeMap)
        {
            ChangeToNextMap(delay);
            return HookResult.Continue;
        }

        return HookResult.Continue;
    }

    private HookResult OnIntermission(EventCsIntermission @event, GameEventInfo info)
    {
        if (!_isMapStarted)
            return HookResult.Continue;

        if (NextMap == null)
            return HookResult.Continue;

        // Fire IntermissionStartEvent
        var intermissionStartEvent = new McsIntermissionStartEvent(GetTextWithPluginPrefix(null, ""), NextMap);
        var eventResult = _mcsEventManager.FireEvent(intermissionStartEvent);

        // If the event was cancelled, stop the map change
        if (eventResult == McsEventResult.Stop)
        {
            return HookResult.Continue;
        }

        // Check if this is during Cs2EndMatchScreen
        bool isDuringEndMatchScreen = false;
        if (_mcsPluginConfigProvider.PluginConfig.GeneralConfig.MapTransitionMethod == MapTransitionMethod.Cs2EndMatchScreen)
        {
            // If we're using Cs2EndMatchScreen, use intermission delay
            isDuringEndMatchScreen = true;
        }

        // Determine map change delay based on current vote type and situation
        float delay = DefaultMapChangeDelay;

        if (isDuringEndMatchScreen)
        {
            // Use intermission delay for Cs2EndMatchScreen
            delay = IntermissionMapChangeDelay.Value;
        }
        else if (_isCurrentVoteRtv)
        {
            // RTV vote handling
            var rtvBehaviour = (MapChangeBehaviourType)RtvMapChangeBehaviour.Value;

            if (rtvBehaviour == MapChangeBehaviourType.Immediately)
            {
                delay = RtvMapChangeDelay.Value;
            }
            else if (rtvBehaviour == MapChangeBehaviourType.NextRoundEnd)
            {
                // Let OnRoundEnd handle this
                return HookResult.Continue;
            }
            else if (rtvBehaviour == MapChangeBehaviourType.WhenTimeRunsOut)
            {
                // Check if time has actually run out
                McsMapExtendType extendType = _timeLeftUtil.ExtendType;
                bool timeHasRunOut = false;

                if (extendType == McsMapExtendType.TimeLimit && _timeLeftUtil.TimeLimit <= 0)
                    timeHasRunOut = true;
                else if (extendType == McsMapExtendType.Rounds && _timeLeftUtil.RoundsLeft <= 0)
                    timeHasRunOut = true;
                else if (extendType == McsMapExtendType.RoundTime && _timeLeftUtil.RoundTimeLeft <= 0)
                    timeHasRunOut = true;

                if (!timeHasRunOut)
                    return HookResult.Continue;

                delay = RtvMapChangeDelay.Value;
            }
        }
        else if (_isCurrentVoteTimeBased)
        {
            // Time-based vote handling
            var timeBasedBehaviour = (MapChangeBehaviourType)TimeBasedVoteMapChangeBehaviour.Value;

            if (timeBasedBehaviour == MapChangeBehaviourType.Immediately)
            {
                delay = TimeBasedVoteMapChangeDelay.Value;
            }
            else if (timeBasedBehaviour == MapChangeBehaviourType.NextRoundEnd)
            {
                // Let OnRoundEnd handle this
                return HookResult.Continue;
            }
            else if (timeBasedBehaviour == MapChangeBehaviourType.WhenTimeRunsOut)
            {
                // Check if time has actually run out
                McsMapExtendType extendType = _timeLeftUtil.ExtendType;
                bool timeHasRunOut = false;

                if (extendType == McsMapExtendType.TimeLimit && _timeLeftUtil.TimeLimit <= 0)
                    timeHasRunOut = true;
                else if (extendType == McsMapExtendType.Rounds && _timeLeftUtil.RoundsLeft <= 0)
                    timeHasRunOut = true;
                else if (extendType == McsMapExtendType.RoundTime && _timeLeftUtil.RoundTimeLeft <= 0)
                    timeHasRunOut = true;

                if (!timeHasRunOut)
                    return HookResult.Continue;

                delay = TimeBasedVoteMapChangeDelay.Value;
            }
        }
        else
        {
            // Legacy handling for cases where vote type is not set
            //if (ChangeMapOnNextRoundEnd)
            //    return HookResult.Continue;

            McsMapExtendType extendType = _timeLeftUtil.ExtendType;

            if (extendType == McsMapExtendType.TimeLimit && _timeLeftUtil.TimeLimit > 0)
                return HookResult.Continue;

            if (extendType == McsMapExtendType.Rounds && _timeLeftUtil.RoundsLeft > 0)
                return HookResult.Continue;

            if (extendType == McsMapExtendType.RoundTime && _timeLeftUtil.RoundTimeLeft > 0)
                return HookResult.Continue;

            // Use competitive end of match extra time if available
            ConVar? mp_competitive_endofmatch_extra_time = ConVar.Find("mp_competitive_endofmatch_extra_time");
            delay = mp_competitive_endofmatch_extra_time?.GetPrimitiveValue<float>() ?? DefaultRoundRestartDelay;
        }

        ChangeToNextMap(delay - 1);
        return HookResult.Continue;
    }


    private void OnNextMapConfirmed(McsNextMapConfirmedEvent @event)
    {
        NextMap = @event.MapConfig;
        _isCurrentVoteRtv = false;
        _isCurrentVoteTimeBased = false;
    }

    private void OnMapExtended(McsMapExtendEvent @event)
    {
        ExtendCount++;
        
        switch (@event.MapExtendType)
        {
            case McsMapExtendType.TimeLimit:
                _timeLeftUtil.ExtendTimeLimit(@event.ExtendTime);
                break;
            case McsMapExtendType.Rounds:
                _timeLeftUtil.ExtendRounds(@event.ExtendTime);
                break;
            case McsMapExtendType.RoundTime:
                _timeLeftUtil.ExtendRoundTime(@event.ExtendTime);
                break;
        }

        RecreateVoteTimer();
    }

    // This method is redundant, but left for just in case.
    private void OnMapNotChanged(McsMapNotChangedEvent @event)
    {
        RecreateVoteTimer();
    }

    private void RecreateVoteTimer()
    {
        _voteStartTimer?.Kill();

        switch (_timeLeftUtil.ExtendType)
        {
            case McsMapExtendType.TimeLimit:
                CreateVoteStartTimer(() => _timeLeftUtil.TimeLimit  > VoteStartTimingTime.Value) ;
                break;
            
            case McsMapExtendType.RoundTime:
                CreateVoteStartTimer(() => _timeLeftUtil.RoundTimeLeft > VoteStartTimingTime.Value);
                break;
            
            case McsMapExtendType.Rounds:
                CreateVoteStartTimer(() => _timeLeftUtil.RoundsLeft > VoteStartTimingRound.Value);
                break;
        }
    }
    
    private void CreateVoteStartTimer(Func<bool> shouldContinueCheck)
    {
        _voteStartTimer = Plugin.AddTimer(VoteStartCheckInterval, () =>
        {
            if (!_isMapStarted)
                return;
            
            if (shouldContinueCheck())
                return;
        
            if (_mcsMapVoteController.CurrentVoteState == McsMapVoteState.NextMapConfirmed)
            {
                _voteStartTimer?.Kill();
                _voteStartTimer = null;
                return;
            }

            if (_mcsMapVoteController.CurrentVoteState == McsMapVoteState.NoActiveVote)
            {
                InitiateVote();
            }
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    internal void InitiateRtvVote()
    {
        if (_mcsMapVoteController.CurrentVoteState == McsMapVoteState.NextMapConfirmed)
            return;

        _voteStartTimer?.Kill();
        _voteStartTimer = null;

        // Set RTV vote flag
        _isCurrentVoteRtv = true;
        _isCurrentVoteTimeBased = false;

        _mcsMapVoteController.InitiateVote(true); // RTV vote
    }

    private void InitiateVote()
    {
        _voteStartTimer?.Kill();
        _voteStartTimer = null;

        // Set time-based vote flag
        _isCurrentVoteRtv = false;
        _isCurrentVoteTimeBased = true;

        _mcsMapVoteController.InitiateVote(false); // Time-based vote
    }
    
    private void FireNextMapChangedEvent(IMapConfig newConfig)
    {
        var confirmedEvent = new McsNextMapChangedEvent(GetTextWithPluginPrefix(null, ""), newConfig);
        _mcsEventManager.FireEventNoResult(confirmedEvent);
    }
    
    private void FireNextMapRemovedEvent(IMapConfig newConfig)
    {
        var nextMapRemovedEvent = new McsNextMapRemovedEvent(GetTextWithPluginPrefix(null, ""), newConfig);
        _mcsEventManager.FireEventNoResult(nextMapRemovedEvent);
    }
}