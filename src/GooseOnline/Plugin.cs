using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using HarmonyLib;
using Steamworks;
using Steamworks.Data;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace GooseOnline;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class GooseOnlinePlugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.gooseonline.steam-p2p";
    public const string PluginName = "Goose Online";
    public const string PluginVersion = "0.4.0";

    private void Awake()
    {
        Logger.LogInfo($"{PluginName} {PluginVersion} loading");
        var go = new GameObject("GooseOnlineRuntime");
        DontDestroyOnLoad(go);
        var runtime = go.AddComponent<OnlineRuntime>();
        runtime.PluginLogger = Logger;
        new Harmony(PluginGuid).PatchAll();
    }
}

internal enum OnlineRole { None, Host, Client }

internal sealed class OnlineRuntime : MonoBehaviour
{
    internal static OnlineRuntime Instance { get; private set; }
    internal BepInEx.Logging.ManualLogSource PluginLogger;
    internal OnlineRole Role { get; private set; }
    internal bool IsOnline => Role != OnlineRole.None;
    internal bool IsConnected { get; private set; }

    private const uint AppId = 837470;
    private const int Channel = 37;
    private const float SnapshotInterval = 0.10f;
    private const float ProgressInterval = 0.50f;
    private const float ObjectCacheInterval = 1.00f;
    private const float PoseInterval = 1f / 30f;
    private const string VersionKey = "gooseonline_version";
    private const string ProtocolKey = "gooseonline_protocol";

    private Lobby? lobby;
    private SteamId peer;
    private float nextSnapshot;
    private float nextProgressSnapshot;
    private float nextObjectCacheRefresh;
    private float nextPose;
    private float nextHello;
    private float handshakeStartedAt;
    private int tick;
    private bool initializedSteam;
    private bool steamEventsInstalled;
    private string status = "单机模式";
    private string lastError;
    private string connectedPlayerName = "未连接";
    private string connectedPlayerId = "-";
    private Protocol.Pose remotePose;
    private bool controllersNormalized;
    private bool uiVisible = true;
    private int worldSnapshotsSent;
    private int worldSnapshotsReceived;
    private int progressSnapshotsSent;
    private int progressSnapshotsReceived;
    private float nextWorldSnapshotWarning;
    private readonly Dictionary<string, Prop> propsByPath = new Dictionary<string, Prop>(StringComparer.Ordinal);
    private readonly Dictionary<string, float> remotePropTimes = new Dictionary<string, float>(StringComparer.Ordinal);
    private readonly Dictionary<string, Brain> brainsByPath = new Dictionary<string, Brain>(StringComparer.Ordinal);
    private readonly Dictionary<string, SwitchSystem> switchesByPath = new Dictionary<string, SwitchSystem>(StringComparer.Ordinal);

    internal void Log(string message) => PluginLogger?.LogInfo(message);
    internal void Warn(string message) { lastError = message; PluginLogger?.LogWarning(message); }
    internal void Error(string message) { lastError = message; PluginLogger?.LogError(message); }

    private void Awake()
    {
        Instance = this;
        Role = OnlineRole.None;
        TryInitializeSteam();
    }

    private void OnDestroy()
    {
        LeaveSession();
        if (initializedSteam) { try { SteamClient.Shutdown(); } catch { } }
        if (Instance == this) Instance = null;
    }

    private void TryInitializeSteam()
    {
        try
        {
            if (!SteamClient.IsValid)
            {
                SteamClient.Init(AppId, true);
                initializedSteam = SteamClient.IsValid;
            }
            if (!SteamClient.IsValid)
            {
                Warn("Steam API 不可用，请从 Steam 启动游戏。");
                return;
            }
            InstallSteamEvents();
            SteamNetworking.AllowP2PPacketRelay(true);

            Log($"Steam ready: {SteamClient.Name} ({SteamClient.SteamId})");
        }
        catch (Exception ex) { Error("Steam 初始化失败: " + ex.Message); }
    }

    private void InstallSteamEvents()
    {
        if (steamEventsInstalled) return;
        SteamMatchmaking.OnLobbyEntered += OnLobbyEntered;
        SteamMatchmaking.OnLobbyInvite += OnLobbyInvite;
        SteamMatchmaking.OnLobbyMemberJoined += OnLobbyMemberJoined;
        SteamMatchmaking.OnLobbyMemberLeave += OnLobbyMemberLeave;
        SteamFriends.OnGameLobbyJoinRequested += OnGameLobbyJoinRequested;
        steamEventsInstalled = true;
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F8)) uiVisible = !uiVisible;
        if (SteamClient.IsValid)
        {
            try { SteamClient.RunCallbacks(); } catch (Exception ex) { Warn("Steam callbacks: " + ex.Message); }
        }
        if (!IsOnline) return;
        RefreshPeerFromLobby();
        if (!peer.IsValid) return;
        SteamNetworking.AcceptP2PSessionWithUser(peer);
        ReadPackets();
        if (!IsConnected)
        {
            if (Role == OnlineRole.Client && Time.unscaledTime >= nextHello)
            {
                nextHello = Time.unscaledTime + 1f;
                SendHello();
            }
            if (handshakeStartedAt > 0f && Time.unscaledTime - handshakeStartedAt > 15f)
                status = "握手超时，请确认双方使用同一个安装包并重启游戏";
            return;
        }
        EnsureTwoPlayerMode();
        if (Time.unscaledTime >= nextObjectCacheRefresh)
        {
            nextObjectCacheRefresh = Time.unscaledTime + ObjectCacheInterval;
            RebuildPropCache();
            RebuildBrainCache();
            RebuildSwitchCache();
        }
        if (Role == OnlineRole.Client)
        {
            if (Time.unscaledTime >= nextPose)
            {
                nextPose = Time.unscaledTime + PoseInterval;
                SendLocalPose();
            }
        }
        else if (Role == OnlineRole.Host && Time.unscaledTime >= nextSnapshot)
        {
            nextSnapshot = Time.unscaledTime + SnapshotInterval;
            SendWorldSnapshot();
        }
        if (Role == OnlineRole.Host && Time.unscaledTime >= nextProgressSnapshot)
        {
            nextProgressSnapshot = Time.unscaledTime + ProgressInterval;
            SendProgressSnapshot();
        }
    }

    private void LateUpdate()
    {
        if (!IsConnected || remotePose == null) return;
        ApplyGoosePose(1, remotePose);
    }

    private void OnGUI()
    {
        if (!uiVisible) return;
        GUILayout.BeginArea(new Rect(16, 16, 360, 300), GUI.skin.box);
        GUILayout.Label("Goose Online " + GooseOnlinePlugin.PluginVersion);
        GUILayout.Label(status);
        if (!SteamClient.IsValid)
        {
            GUILayout.Label("Steam 未连接");
            GUILayout.EndArea();
            return;
        }
        if (!IsOnline)
        {
            GUILayout.Label("通过 Steam 好友邀请进行联机");
            if (GUILayout.Button("创建 Steam 房间")) CreateLobby();
            GUILayout.Label("创建后点击 Steam 好友邀请面板邀请朋友");
        }
        else
        {
            GUILayout.Label(Role == OnlineRole.Host ? "角色：房主" : "角色：客户端");
            GUILayout.Label("本机鹅：P1　远端鹅：P2");
            GUILayout.Label(IsConnected ? "连接：已建立" : "连接：等待中");
            GUILayout.Label("Steam 玩家：" + connectedPlayerName);
            GUILayout.Label("SteamID：" + connectedPlayerId);
            GUILayout.Label(Role == OnlineRole.Host ? "世界同步发送：" + worldSnapshotsSent : "世界同步接收：" + worldSnapshotsReceived);
            GUILayout.Label(Role == OnlineRole.Host ? "进度同步发送：" + progressSnapshotsSent : "进度同步接收：" + progressSnapshotsReceived);
            if (lobby.HasValue && GUILayout.Button("打开 Steam 邀请面板")) SteamFriends.OpenGameInviteOverlay(lobby.Value.Id);
            if (GUILayout.Button("离开联机")) LeaveSession();
        }
        if (!string.IsNullOrEmpty(lastError)) GUILayout.Label("错误：" + lastError);
        GUILayout.EndArea();
    }

    private async void CreateLobby()
    {
        if (!SteamClient.IsValid) return;
        try
        {
            var created = await SteamMatchmaking.CreateLobbyAsync(2);
            if (!created.HasValue) { Warn("创建 Steam 房间失败"); return; }
            lobby = created.Value;
            lobby.Value.SetFriendsOnly();
            lobby.Value.SetJoinable(true);
            lobby.Value.SetData(VersionKey, GooseOnlinePlugin.PluginVersion);
            lobby.Value.SetData(ProtocolKey, Protocol.Version.ToString());
            Role = OnlineRole.Host;
            IsConnected = false;
            worldSnapshotsSent = 0;
            worldSnapshotsReceived = 0;
            progressSnapshotsSent = 0;
            progressSnapshotsReceived = 0;
            nextProgressSnapshot = 0f;
            status = "房间已创建，等待朋友加入";
            EnsureTwoPlayerMode();
            SteamFriends.OpenGameInviteOverlay(lobby.Value.Id);
            Log("Lobby created: " + lobby.Value.Id);
        }
        catch (Exception ex) { Error("创建房间失败: " + ex.Message); }
    }

    private async void JoinLobby(Lobby requested)
    {
        try
        {
            var joined = await SteamMatchmaking.JoinLobbyAsync(requested.Id);
            if (!joined.HasValue) { Warn("加入 Steam 房间失败"); return; }
            lobby = joined.Value;
            var version = lobby.Value.GetData(VersionKey);
            var protocol = lobby.Value.GetData(ProtocolKey);
            if (!string.IsNullOrEmpty(version) && version != GooseOnlinePlugin.PluginVersion) { Warn("双方插件版本不一致"); LeaveSession(); return; }
            if (!string.IsNullOrEmpty(protocol) && protocol != Protocol.Version.ToString()) { Warn("双方协议版本不一致"); LeaveSession(); return; }
            Role = OnlineRole.Client;
            IsConnected = false;
            worldSnapshotsSent = 0;
            worldSnapshotsReceived = 0;
            progressSnapshotsSent = 0;
            progressSnapshotsReceived = 0;
            nextProgressSnapshot = 0f;
            handshakeStartedAt = Time.unscaledTime;
            peer = lobby.Value.Owner.Id;
            SteamNetworking.AcceptP2PSessionWithUser(peer);
            status = "已加入房间，正在握手";
            EnsureTwoPlayerMode();
            SendHello();
        }
        catch (Exception ex) { Error("加入房间失败: " + ex.Message); }
    }

    private void OnLobbyEntered(Lobby entered)
    {
        lobby = entered;
        if (Role == OnlineRole.None)
        {
            Role = entered.Owner.Id == SteamClient.SteamId ? OnlineRole.Host : OnlineRole.Client;
            peer = entered.Owner.Id;
        }
        if (Role == OnlineRole.Client && peer.IsValid)
        {
            handshakeStartedAt = Time.unscaledTime;
            RefreshPeerInfo();
            SteamNetworking.AcceptP2PSessionWithUser(peer);
            SendHello();
        }
        status = Role == OnlineRole.Host ? "房间已创建，等待客户端" : "已进入房间，正在握手";
    }

    private void OnLobbyInvite(Friend friend, Lobby invited) { JoinLobby(invited); }
    private void OnGameLobbyJoinRequested(Lobby requested, SteamId friend) { JoinLobby(requested); }
    private void OnLobbyMemberJoined(Lobby current, Friend member)
    {
        if (Role == OnlineRole.Host && member.Id != SteamClient.SteamId) { peer = member.Id; status = "客户端已加入，等待握手"; SteamNetworking.AcceptP2PSessionWithUser(peer); }
    }

    private void RefreshPeerFromLobby()
    {
        if (!lobby.HasValue || !SteamClient.IsValid) return;
        try
        {
            if (Role == OnlineRole.Client)
            {
                peer = lobby.Value.Owner.Id;
                return;
            }
            foreach (var member in lobby.Value.Members)
            {
                if (member.Id != SteamClient.SteamId)
                {
                    if (peer != member.Id) status = "客户端已加入，正在握手";
                    peer = member.Id;
                    RefreshPeerInfo();
                    return;
                }
            }
        }
        catch (Exception ex) { Warn("刷新 Lobby 成员失败: " + ex.Message); }
    }
    private void OnLobbyMemberLeave(Lobby current, Friend member) { if (member.Id == peer) { IsConnected = false; status = "对方已离开"; } }

    private void SendHello()
    {
        Send(Protocol.Encode(MessageType.Hello, tick, writer => { writer.Write(GooseOnlinePlugin.PluginVersion); writer.Write(Protocol.Version); } ), P2PSend.Reliable);
    }

    private void RefreshPeerInfo()
    {
        if (!peer.IsValid) return;
        connectedPlayerId = peer.ToString();
        try
        {
            var friend = new Friend(peer);
            connectedPlayerName = string.IsNullOrWhiteSpace(friend.Name) ? "未知玩家" : friend.Name;
        }
        catch { connectedPlayerName = "未知玩家"; }
    }

    private void SendLocalPose()
    {
        var goose = GetGoose(0);
        if (goose == null) return;
        Send(Protocol.EncodePose(tick++, ToPose(goose)), P2PSend.UnreliableNoDelay);
    }

    private void SendWorldSnapshot()
    {
        var localGoose = GetGoose(0);
        var remoteGoose = GetGoose(1);
        if (localGoose == null || remoteGoose == null) return;
        var state = new Protocol.WorldState { Tick = tick++, GooseA = ToPose(localGoose), GooseB = ToPose(remoteGoose) };
        RebuildPropCache();
        foreach (var prop in propsByPath.Values)
        {
            if (prop == null || prop.rb == null) continue;
            state.Props.Add(new Protocol.PropState { Path = GetPropKey(prop), Position = prop.transform.position, Rotation = prop.transform.rotation, Velocity = prop.rb.velocity });
            if (state.Props.Count >= 256) break;
        }
        RebuildBrainCache();
        foreach (var brain in brainsByPath.Values)
        {
            if (brain == null || brain.root == null) continue;
            state.Brains.Add(new Protocol.PropState { Path = GetStablePath(brain.root), Position = brain.root.position, Rotation = brain.root.rotation, Velocity = Vector3.zero });
            if (state.Brains.Count >= 64) break;
        }
        Send(Protocol.EncodeWorld(state.Tick, state), P2PSend.UnreliableNoDelay);
        worldSnapshotsSent++;
    }

    private void SendProgressSnapshot()
    {
        if (Role != OnlineRole.Host || !IsConnected) return;
        var state = new Protocol.ProgressState { Area = (int)AreaTracker.mostRecentArea };
        foreach (var pair in switchesByPath)
            if (pair.Value != null) state.Switches.Add(new Protocol.SwitchState { Path = pair.Key, State = pair.Value.currentState });
        try
        {
            if (SaveGameData.instance?.entries != null)
                foreach (var entry in SaveGameData.instance.entries)
                    if (entry != null && !string.IsNullOrEmpty(entry.key)) state.Saves.Add(new Protocol.SaveState { Key = entry.key, BoolValue = entry.boolValue, IntValue = entry.intValue });
            if (GoalManager.instance?.goals != null)
                foreach (var goal in GoalManager.instance.goals)
                    if (goal != null && goal.goalInfo.goalName != null) state.Goals.Add(new Protocol.GoalState { Name = goal.goalInfo.goalName, Awarded = goal.awarded, Hidden = goal.hidden });
        }
        catch (Exception ex) { Warn("收集任务进度失败: " + ex.Message); }
        Send(Protocol.EncodeProgress(tick++, state), P2PSend.ReliableWithBuffering);
        progressSnapshotsSent++;
    }

    private void ApplyProgress(Protocol.ProgressState state)
    {
        if (state == null) return;
        RebuildSwitchCache();
        foreach (var switchState in state.Switches)
        {
            if (!switchesByPath.TryGetValue(switchState.Path, out var system) || system == null || system.currentState == switchState.State) continue;
            try { system.SetState(switchState.State, null); } catch (Exception ex) { Warn("同步门/开关失败: " + ex.Message); }
        }
        try
        {
            if (SaveGameData.instance?.entries != null)
                foreach (var save in state.Saves)
                {
                    var entry = SaveGameData.instance.GetSaveEntry(save.Key);
                    entry.boolValue = save.BoolValue;
                    entry.intValue = save.IntValue;
                }
            if ((int)AreaTracker.mostRecentArea != state.Area)
            {
                AreaTracker.mostRecentArea = (GoalListArea)state.Area;
                switch ((GoalListArea)state.Area)
                {
                    case GoalListArea.Garden: SwitchEventManager.TriggerEvent("enterAreaGarden"); break;
                    case GoalListArea.Shops: SwitchEventManager.TriggerEvent("enterAreaHighstreet"); break;
                    case GoalListArea.Backyards: SwitchEventManager.TriggerEvent("enterAreaBackyards"); break;
                    case GoalListArea.Pub: SwitchEventManager.TriggerEvent("enterAreaPub"); break;
                    case GoalListArea.Finale: SwitchEventManager.TriggerEvent("enterAreaFinale"); break;
                }
            }
            if (GoalManager.instance?.goals != null)
                foreach (var goalState in state.Goals)
                {
                    var goal = GoalManager.GetGoalByName(goalState.Name);
                    if (goal == null) continue;
                    if (goalState.Awarded && !goal.awarded) goal.SetToAwarded();
                    if (!goalState.Hidden && goal.hidden) goal.Unhide();
                }
        }
        catch (Exception ex) { Warn("应用关卡进度失败: " + ex.Message); }
    }

    private void Send(byte[] data, P2PSend mode)
    {
        if (!peer.IsValid || data == null) return;
        try { SteamNetworking.SendP2PPacket(peer, data, data.Length, Channel, mode); } catch (Exception ex) { Warn("发送网络包失败: " + ex.Message); }
    }

    private void ReadPackets()
    {
        if (!peer.IsValid) return;
        try
        {
            while (SteamNetworking.IsP2PPacketAvailable(Channel))
            {
                var packet = SteamNetworking.ReadP2PPacket(Channel);
                if (!packet.HasValue) break;
                if (packet.Value.SteamId != peer) continue;
                if (!Protocol.TryDecode(packet.Value.Data, out var decoded)) continue;
                HandlePacket(decoded);
            }
        }
        catch (Exception ex) { Warn("读取网络包失败: " + ex.Message); }
    }

    private void HandlePacket(Protocol.Packet packet)
    {
        switch (packet.Type)
        {
            case MessageType.Hello:
                if (Role != OnlineRole.Host) return;
                RefreshPeerInfo();
                IsConnected = true; status = "客户端已连接";
                Send(Protocol.Encode(MessageType.Welcome, tick++), P2PSend.Reliable);
                SendProgressSnapshot();
                break;
            case MessageType.Welcome:
                if (Role == OnlineRole.Client) { RefreshPeerInfo(); IsConnected = true; status = "已连接到房主"; }
                break;
            case MessageType.ClientPose:
                if (Role == OnlineRole.Host && Protocol.TryReadPose(packet, out var pose)) QueueRemotePose(pose);
                break;
            case MessageType.WorldSnapshot:
                if (Role == OnlineRole.Client)
                {
                    if (Protocol.TryReadWorld(packet, out var state))
                    {
                        worldSnapshotsReceived++;
                        ApplyWorld(state);
                    }
                    else if (Time.unscaledTime >= nextWorldSnapshotWarning)
                    {
                        nextWorldSnapshotWarning = Time.unscaledTime + 2f;
                        Warn("主机世界快照解析失败");
                    }
                }
                break;
            case MessageType.GoalAwarded:
                if (Role == OnlineRole.Client && Protocol.TryReadGoal(packet, out var goalName)) ApplyGoal(goalName);
                break;
            case MessageType.ProgressSnapshot:
                if (Role == OnlineRole.Client && Protocol.TryReadProgress(packet, out var progress))
                {
                    progressSnapshotsReceived++;
                    ApplyProgress(progress);
                }
                break;
            case MessageType.Leave:
                IsConnected = false; status = "对方已离开"; break;
        }
    }

    private void ApplyWorld(Protocol.WorldState state)
    {
        QueueRemotePose(state.GooseA);
        RebuildPropCache();
        foreach (var propState in state.Props)
        {
            if (!propsByPath.TryGetValue(propState.Path, out var prop) || prop == null || prop.rb == null) continue;
            prop.transform.SetPositionAndRotation(propState.Position, propState.Rotation);
            prop.rb.velocity = propState.Velocity;
            remotePropTimes[propState.Path] = Time.unscaledTime;
        }
        RebuildBrainCache();
        foreach (var brainState in state.Brains)
        {
            if (brainsByPath.TryGetValue(brainState.Path, out var brain) && brain != null && brain.root != null)
                brain.root.SetPositionAndRotation(brainState.Position, brainState.Rotation);
        }
    }

    private void ApplyGoosePose(int index, Protocol.Pose pose)
    {
        if (pose == null) return;
        var goose = GetGoose(index);
        if (goose == null) return;
        EnsureGooseActive(goose);
        goose.transform.SetPositionAndRotation(pose.Position, pose.Rotation);
        if (goose.rb != null) goose.rb.velocity = pose.Velocity;
        ApplyGooseActions(goose, pose);
    }

    private void QueueRemotePose(Protocol.Pose pose)
    {
        if (pose == null) return;
        remotePose = pose;
    }

    private void ApplyGooseActions(Goose goose, Protocol.Pose pose)
    {
        if (goose == null || pose == null) return;
        try
        {
            if (goose.gooseNeck != null)
            {
                goose.gooseNeck.headBobAmount = Mathf.Clamp01(pose.NeckAmount);
                goose.gooseNeck.neckIsToggledOn = pose.NeckToggle;
            }
            if (goose.gooseEngine != null)
            {
                goose.gooseEngine.SetFloat(GooseAnimationParameters.sneakTrigger, Mathf.Clamp01(pose.NeckAmount));
                goose.gooseEngine.SetBool(GooseAnimationParameters.isDragging, pose.IsDragging);
            }
            ApplyHeldProp(goose, pose);
        }
        catch (Exception ex) { Warn("应用远端鹅动作失败: " + ex.Message); }
    }

    private void ApplyHeldProp(Goose goose, Protocol.Pose pose)
    {
        if (goose.holder == null || goose.dragger == null) return;
        var desiredPath = pose.HeldPropPath ?? string.Empty;
        var currentProp = goose.dragger.active ? goose.dragger.holdable : goose.holder.holding;
        var currentPath = currentProp == null ? string.Empty : GetPropKey(currentProp);
        var currentDragging = goose.dragger.active;
        if (string.Equals(currentPath, desiredPath, StringComparison.Ordinal) && currentDragging == pose.IsDragging) return;

        if (currentProp != null)
        {
            try { goose.Drop(); } catch (Exception ex) { Warn("释放远端物品失败: " + ex.Message); }
        }
        if (string.IsNullOrEmpty(desiredPath)) return;

        RebuildPropCache();
        if (!propsByPath.TryGetValue(desiredPath, out var prop) || prop == null)
        {
            Warn("找不到远端携带物品: " + desiredPath);
            return;
        }
        var handle = FindHandleForProp(goose, prop);
        if (handle == null || handle.prop == null)
        {
            Warn("找不到远端携带物品的 BeakHandle: " + desiredPath);
            return;
        }
        try
        {
            if (pose.IsDragging) goose.dragger.PickUp(handle);
            else goose.holder.PickUp(handle);
        }
        catch (Exception ex) { Warn("同步远端携带物品失败: " + ex.Message); }
    }

    private void EnsureTwoPlayerMode()
    {
        try
        {
            if (!IsOnline) return;
            var manager = BaseMultiplayerManager.instance;
            if (manager != null && manager.preferredPlayerCount != 2) manager.preferredPlayerCount = 2;
            var game = GameManager.instance;
            if (game != null && game.readyForGeese)
            {
                if (game.activeGooseCount < 2)
                {
                    try { game.ActivateAllGeese(); } catch (Exception ex) { Warn("原版双人鹅激活失败: " + ex.Message); }
                }
                EnsureGooseActive(GetGoose(0));
                EnsureGooseActive(GetGoose(1));
                if (!controllersNormalized)
                {
                    BaseMultiplayerManager.ReturnAllFloatingControllersToPlayer1();
                    controllersNormalized = true;
                }
            }
            game?.PlayersChanged();
        }
        catch (Exception ex) { Warn("双人模式初始化失败: " + ex.Message); }
    }

    private List<Goose> GetGeese()
    {
        try
        {
            var list = GameManager.instance?.allGeese;
            if (list != null && list.Count > 0) return list.Where(x => x != null).OrderBy(x => x.playerNumber).ToList();
        }
        catch { }
        return UObject.FindObjectsOfType<Goose>().Where(x => x != null).OrderBy(x => x.playerNumber).ToList();
    }

    private Goose GetGoose(int playerNumber)
    {
        return GetGeese().FirstOrDefault(goose => goose.playerNumber == playerNumber);
    }

    private void EnsureGooseActive(Goose goose)
    {
        if (goose == null || goose.isActiveAndEnabled) return;
        try
        {
            goose.Initialize();
            goose.gameObject.SetActive(true);
            goose.enabled = true;
            GooseSpawn.PlaceGooseAt(goose, AreaTracker.mostRecentArea);
            InGameUI.SetGooseActive();
            Log("Activated online goose P" + (goose.playerNumber + 1));
        }
        catch (Exception ex) { Warn("激活在线鹅失败 P" + (goose.playerNumber + 1) + ": " + ex.Message); }
    }

    private Protocol.Pose ToPose(Goose goose)
    {
        var held = goose.dragger != null && goose.dragger.active ? goose.dragger.holdable : goose.holder?.holding;
        return new Protocol.Pose
        {
            Position = goose.transform.position,
            Rotation = goose.transform.rotation,
            Velocity = goose.rb != null ? goose.rb.velocity : Vector3.zero,
            NeckAmount = goose.gooseNeck != null ? goose.gooseNeck.headBobAmount : 0f,
            NeckToggle = goose.gooseNeck != null && goose.gooseNeck.neckIsToggledOn,
            IsDragging = goose.dragger != null && goose.dragger.active,
            HeldPropPath = held == null ? string.Empty : GetPropKey(held)
        };
    }

    private void RebuildPropCache()
    {
        propsByPath.Clear();
        foreach (var prop in UObject.FindObjectsOfType<Prop>())
        {
            if (prop != null) propsByPath[GetPropKey(prop)] = prop;
        }
    }

    private static string GetPropKey(Prop prop)
    {
        if (prop == null) return string.Empty;
        if (prop.startHome != null) return "prop:" + GetStablePath(prop.startHome.transform);
        return "prop-name:" + prop.name;
    }

    private static BeakHandle FindHandleForProp(Goose goose, Prop prop)
    {
        if (prop == null || prop.beakHandles == null || prop.beakHandles.Length == 0) return null;
        var origin = goose != null && goose.holdParent != null ? goose.holdParent.position : prop.transform.position;
        return prop.beakHandles
            .Where(handle => handle != null && handle.prop == prop && handle.isActiveAndEnabled)
            .OrderBy(handle => (handle.transform.position - origin).sqrMagnitude)
            .FirstOrDefault();
    }

    private void RebuildBrainCache()
    {
        brainsByPath.Clear();
        foreach (var brain in UObject.FindObjectsOfType<Brain>())
            if (brain != null && brain.root != null) brainsByPath[GetStablePath(brain.root)] = brain;
    }

    private void RebuildSwitchCache()
    {
        switchesByPath.Clear();
        foreach (var system in UObject.FindObjectsOfType<SwitchSystem>())
            if (system != null) switchesByPath[GetStablePath(system.transform)] = system;
    }

    private static string GetStablePath(Transform transform)
    {
        var parts = new Stack<string>();
        var current = transform;
        while (current != null)
        {
            parts.Push(current.name + "[" + current.GetSiblingIndex() + "]");
            current = current.parent;
        }
        return string.Join("/", parts.ToArray());
    }

    internal void LeaveSession()
    {
        if (!IsOnline) return;
        try { Send(Protocol.Encode(MessageType.Leave, tick++), P2PSend.Reliable); } catch { }
        try { if (peer.IsValid) SteamNetworking.CloseP2PSessionWithUser(peer); } catch { }
        try { lobby?.Leave(); } catch { }
        lobby = null; peer = default(SteamId); Role = OnlineRole.None; IsConnected = false; status = "单机模式"; connectedPlayerName = "未连接"; connectedPlayerId = "-"; remotePose = null; controllersNormalized = false; worldSnapshotsSent = 0; worldSnapshotsReceived = 0;
    }

    internal void BroadcastGoal(string goalName)
    {
        if (Role == OnlineRole.Host && IsConnected) Send(Protocol.EncodeGoal(tick++, goalName), P2PSend.Reliable);
    }

    internal void ApplyGoal(string goalName)
    {
        if (string.IsNullOrEmpty(goalName)) return;
        try { GoalManager.GetGoalByName(goalName)?.SetToAwarded(); } catch (Exception ex) { Warn("任务同步失败: " + ex.Message); }
    }
}

[HarmonyPatch(typeof(Goal), nameof(Goal.AwardGoalDuringGame))]
internal static class GoalAwardPatch
{
    private static void Postfix(Goal __instance)
    {
        var runtime = OnlineRuntime.Instance;
        if (runtime == null || !runtime.IsOnline || runtime.Role != OnlineRole.Host) return;
        runtime.BroadcastGoal(__instance.goalInfo.goalName);
    }
}

[HarmonyPatch(typeof(PlayerNumberMenu), nameof(PlayerNumberMenu.ConfirmOnePlayer))]
internal static class ForceOnlineCoopPatch
{
    private static bool Prefix(PlayerNumberMenu __instance)
    {
        var runtime = OnlineRuntime.Instance;
        if (runtime == null || !runtime.IsOnline) return true;
        try { __instance.ConfirmTwoPlayer(); return false; } catch { return true; }
    }
}

[HarmonyPatch(typeof(BaseMultiplayerManager), nameof(BaseMultiplayerManager.Inititialize))]
internal static class OnlineMultiplayerInitPatch
{
    private static void Postfix()
    {
        var runtime = OnlineRuntime.Instance;
        if (runtime == null || !runtime.IsOnline) return;
        try { BaseMultiplayerManager.instance.preferredPlayerCount = 2; } catch { }
    }
}







