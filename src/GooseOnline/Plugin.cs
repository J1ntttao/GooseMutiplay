using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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
    public const string PluginVersion = "0.4.19";

    private void Awake()
    {
        Logger.LogInfo($"{PluginName} {PluginVersion} loading");
        var go = new GameObject("GooseOnlineRuntime");
        DontDestroyOnLoad(go);
        var runtime = go.AddComponent<OnlineRuntime>();
        runtime.PluginLogger = Logger;
        runtime.StartUpdateCheck();
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
    private const int MaxUnreliablePacketBytes = 1200;
    private const float SnapshotInterval = 0.25f;
    private const float FullWorldResyncInterval = 10.00f;
    private const float ObjectCacheInterval = 1.00f;
    private const float PoseInterval = 1f / 30f;
    private const string VersionKey = "gooseonline_version";
    private const string ProtocolKey = "gooseonline_protocol";

    private Lobby? lobby;
    private SteamId peer;
    private float nextSnapshot;
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
    private bool remoteHeldStateKnown;
    private string remoteAppliedHeldPropPath = string.Empty;
    private bool remoteAppliedDragging;
    private string lastLocalHeldPropPath = string.Empty;
    private bool localGameReadyAnnounced;
    private bool remoteGameReady;
    private bool initialStateSent;
    private bool initialWorldApplied;
    private bool initialProgressApplied;
    private bool initialProgressAcknowledged;
    private bool uiVisible = true;
    private string updateStatus = "正在检查更新...";
    private int updateProcessId;
    private int worldSnapshotsSent;
    private int worldSnapshotsReceived;
    private int progressSnapshotsSent;
    private int progressSnapshotsReceived;
    private float nextWorldSnapshotWarning;
    private float nextProgressSnapshotWarning;
    private float nextSendWarning;
    private float nextInitialProgressRetry;
    private float nextMissingPropWarning;
    private float nextMissingSwitchWarning;
    private float nextFullWorldResync;
    private bool switchSnapshotPending;
    private readonly HashSet<Brain> remoteBrainAgents = new HashSet<Brain>();
    private readonly Dictionary<string, int> authoritativeSwitchStates = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly Dictionary<string, Protocol.PropState> lastSentProps = new Dictionary<string, Protocol.PropState>(StringComparer.Ordinal);
    private readonly Dictionary<string, Protocol.PropState> lastSentBrains = new Dictionary<string, Protocol.PropState>(StringComparer.Ordinal);
    private readonly Dictionary<string, int> lastSentSwitches = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly Dictionary<Brain, Protocol.PropState> remoteBrainTargets = new Dictionary<Brain, Protocol.PropState>();
    private readonly Dictionary<string, string> authoritativeBrainStates = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, float> localPropReleaseGraceUntil = new Dictionary<string, float>(StringComparer.Ordinal);
    private readonly Dictionary<string, Prop> propsByPath = new Dictionary<string, Prop>(StringComparer.Ordinal);
    private readonly Dictionary<string, Prop> propsByAlias = new Dictionary<string, Prop>(StringComparer.Ordinal);
    private readonly Dictionary<string, float> remotePropTimes = new Dictionary<string, float>(StringComparer.Ordinal);
    private readonly Dictionary<string, Brain> brainsByPath = new Dictionary<string, Brain>(StringComparer.Ordinal);
    private readonly Dictionary<string, Brain> brainsByAlias = new Dictionary<string, Brain>(StringComparer.Ordinal);
    private readonly Dictionary<string, SwitchSystem> switchesByPath = new Dictionary<string, SwitchSystem>(StringComparer.Ordinal);
    private readonly Dictionary<string, SwitchSystem> switchesByAlias = new Dictionary<string, SwitchSystem>(StringComparer.Ordinal);

    internal void Log(string message) => PluginLogger?.LogInfo(message);
    internal void Warn(string message) { lastError = message; PluginLogger?.LogWarning(message); }
    internal void Error(string message) { lastError = message; PluginLogger?.LogError(message); }

    private const string UpdateVersionUrl = "https://raw.githubusercontent.com/J1ntttao/GooseMutiplay/master/src/GooseOnline/Plugin.cs";
    private const string UpdateDllUrl = "https://raw.githubusercontent.com/J1ntttao/GooseMutiplay/master/dist/GooseOnline.dll";

    internal void StartUpdateCheck()
    {
        updateProcessId = Process.GetCurrentProcess().Id;
        ThreadPool.QueueUserWorkItem(_ => CheckForUpdate());
    }

    private void CheckForUpdate()
    {
        try
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            using (var client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = "GooseOnline-Updater";
                var source = DownloadText(client, UpdateVersionUrl);
                var match = Regex.Match(source, @"PluginVersion\s*=\s*""(?<version>[0-9]+\.[0-9]+\.[0-9]+)""");
                if (!match.Success) throw new InvalidDataException("远程版本号格式无效");
                var remoteVersion = new Version(match.Groups["version"].Value);
                var currentVersion = new Version(GooseOnlinePlugin.PluginVersion);
                if (remoteVersion <= currentVersion)
                {
                    updateStatus = "已是最新版本";
                    return;
                }

                var root = BepInEx.Paths.GameRootPath;
                var updateDir = Path.Combine(root, "BepInEx", "update");
                Directory.CreateDirectory(updateDir);
                var staged = Path.Combine(updateDir, "GooseOnline.dll.download");
                var versionFile = Path.Combine(updateDir, "GooseOnline.version");
                if (!File.Exists(versionFile) || !string.Equals(File.ReadAllText(versionFile).Trim(), remoteVersion.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    updateStatus = "正在下载 Goose Online " + remoteVersion + "，请勿关闭游戏";
                    DownloadFile(client, UpdateDllUrl, staged);
                    if (!File.Exists(staged) || new FileInfo(staged).Length < 4096) throw new InvalidDataException("远程 DLL 下载不完整");
                    File.WriteAllText(versionFile, remoteVersion.ToString());
                }

                var script = Path.Combine(updateDir, "GooseOnlineUpdater.ps1");
                File.WriteAllText(script, UpdateScript);
                var target = Path.Combine(root, "BepInEx", "plugins", "GooseOnline.dll");
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " + Quote(script) + " -ProcessId " + updateProcessId + " -Target " + Quote(target) + " -Staged " + Quote(staged) + " -VersionFile " + Quote(versionFile) + " -Script " + Quote(script),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process.Start(psi)) { }
                updateStatus = "更新已下载，请退出游戏；更新完成后手动重启";
                Log("Update staged: " + remoteVersion);
            }
        }
        catch (Exception ex)
        {
            updateStatus = "更新检查失败，继续使用当前版本";
            PluginLogger?.LogWarning("自动更新失败: " + ex.Message);
        }
    }

    private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

    private static string DownloadText(WebClient client, string url)
    {
        try { return client.DownloadString(url); }
        catch { return RunPowerShell(url, null); }
    }

    private static void DownloadFile(WebClient client, string url, string path)
    {
        try { client.DownloadFile(url, path); }
        catch { RunPowerShell(url, path); }
    }

    private static string RunPowerShell(string url, string outputPath)
    {
        var command = "$ProgressPreference='SilentlyContinue'; " +
            (outputPath == null
                ? "(Invoke-WebRequest -UseBasicParsing -Uri '" + EscapePowerShellString(url) + "').Content"
                : "Invoke-WebRequest -UseBasicParsing -Uri '" + EscapePowerShellString(url) + "' -OutFile '" + EscapePowerShellString(outputPath) + "'");
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encoded,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = outputPath == null,
            RedirectStandardError = true
        };
        using (var process = Process.Start(psi))
        {
            var output = outputPath == null ? process.StandardOutput.ReadToEnd() : string.Empty;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException("curl failed: " + error.Trim());
            return output;
        }
    }

    private static string EscapePowerShellString(string value) => (value ?? string.Empty).Replace("'", "''");

    private const string UpdateScript = @"
param([int]$ProcessId, [string]$Target, [string]$Staged, [string]$VersionFile, [string]$Script)
$ErrorActionPreference = 'Stop'
while (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 2 }
for ($attempt = 1; $attempt -le 20; $attempt++) {
    try {
        Copy-Item -LiteralPath $Staged -Destination $Target -Force
        Remove-Item -LiteralPath $Staged -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $VersionFile -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $Script -Force -ErrorAction SilentlyContinue
        exit 0
    } catch {
        if ($attempt -eq 20) { exit 1 }
        Start-Sleep -Milliseconds 500
    }
}";

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
        UpdateInitialSync();
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
        else if (Role == OnlineRole.Host)
        {
            if (Time.unscaledTime >= nextPose)
            {
                nextPose = Time.unscaledTime + PoseInterval;
                SendHostPose();
            }
            if (Time.unscaledTime >= nextSnapshot)
            {
                switchSnapshotPending = false;
                nextSnapshot = Time.unscaledTime + SnapshotInterval;
                SendWorldSnapshot();
            }
            else if (switchSnapshotPending)
            {
                switchSnapshotPending = false;
                nextSnapshot = Time.unscaledTime + SnapshotInterval;
                SendWorldSnapshot();
            }
        }
    }

    private void LateUpdate()
    {
        ApplyRemoteBrainTargets();
        if (!IsConnected || remotePose == null) return;
        ApplyGoosePose(1, remotePose);
    }

    private void ApplyRemoteBrainTargets()
    {
        if (Role != OnlineRole.Client || remoteBrainTargets.Count == 0) return;
        var expired = new List<Brain>();
        var blend = 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime);
        foreach (var pair in remoteBrainTargets)
        {
            var brain = pair.Key;
            var target = pair.Value;
            if (brain == null || target == null)
            {
                expired.Add(brain);
                continue;
            }
            var position = Vector3.Lerp(brain.transform.position, target.Position, blend);
            var rotation = Quaternion.Slerp(brain.transform.rotation, target.Rotation, blend);
            brain.transform.SetPositionAndRotation(position, rotation);
            if (brain.agent != null && brain.agent.isOnNavMesh)
                brain.agent.nextPosition = position;

            if (authoritativeBrainStates.TryGetValue(GetStablePath(brain.transform), out var stateName) &&
                !string.IsNullOrEmpty(stateName))
                ApplyBrainState(brain, stateName);
        }
        foreach (var brain in expired) remoteBrainTargets.Remove(brain);
    }

    private void OnGUI()
    {
        if (!uiVisible) return;
        GUILayout.BeginArea(new Rect(16, 16, 380, 360), GUI.skin.box);
        GUILayout.Label("Goose Online " + GooseOnlinePlugin.PluginVersion);
        if (!string.IsNullOrEmpty(updateStatus)) GUILayout.Label(updateStatus);
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
            GUILayout.Label("链接成功后，双方都要手动进入单人游戏中");
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
            ResetInitialSyncState();
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
            ResetInitialSyncState();
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
        var pose = ToPose(goose);
        TrackLocalHeldProp(pose.HeldPropPath);
        Send(Protocol.EncodePose(tick++, pose), P2PSend.UnreliableNoDelay);
    }

    private void SendHostPose()
    {
        var goose = GetGoose(0);
        if (goose == null) return;
        Send(Protocol.EncodeHostPose(tick++, ToPose(goose)), P2PSend.UnreliableNoDelay);
    }

    private void SendWorldSnapshot()
    {
        var state = BuildWorldState();
        if (state == null)
        {
            if (Time.unscaledTime >= nextWorldSnapshotWarning)
            {
                nextWorldSnapshotWarning = Time.unscaledTime + 2f;
                Warn("世界快照未发送：P1/P2 尚未同时就绪");
            }
            return;
        }
        if (Time.unscaledTime >= nextFullWorldResync ||
            (lastSentSwitches.Count == 0 && lastSentBrains.Count == 0 && lastSentProps.Count == 0))
        {
            if (Send(Protocol.EncodeWorld(state.Tick, state), P2PSend.Reliable))
            {
                RememberSentWorld(state, true);
                nextFullWorldResync = Time.unscaledTime + FullWorldResyncInterval;
                worldSnapshotsSent++;
            }
            return;
        }

        var delta = BuildLiveWorldDelta(state);
        if (Send(Protocol.EncodeLiveWorld(delta.Tick, delta), P2PSend.Reliable))
        {
            RememberSentWorld(delta, false);
            worldSnapshotsSent++;
        }
    }

    private Protocol.WorldState BuildLiveWorldDelta(Protocol.WorldState state)
    {
        var delta = new Protocol.WorldState { Tick = state.Tick, GooseA = state.GooseA, GooseB = state.GooseB };
        foreach (var switchState in state.Switches)
        {
            if (lastSentSwitches.TryGetValue(switchState.Path, out var previous) && previous == switchState.State) continue;
            TryAddLiveSwitch(delta, switchState);
        }
        foreach (var brainState in state.Brains)
        {
            if (lastSentBrains.TryGetValue(brainState.Path, out var previous) && SameWorldObject(previous, brainState)) continue;
            TryAddLiveBrain(delta, brainState);
        }
        foreach (var propState in state.Props.OrderByDescending(prop => !string.IsNullOrEmpty(prop.HeldByBrainPath)))
        {
            if (lastSentProps.TryGetValue(propState.Path, out var previous) && SameWorldObject(previous, propState)) continue;
            TryAddLiveProp(delta, propState);
        }
        return delta;
    }

    private static bool SameWorldObject(Protocol.PropState left, Protocol.PropState right)
    {
        if (left == null || right == null) return false;
        return string.Equals(left.HeldByBrainPath ?? string.Empty, right.HeldByBrainPath ?? string.Empty, StringComparison.Ordinal) &&
            string.Equals(left.BrainState ?? string.Empty, right.BrainState ?? string.Empty, StringComparison.Ordinal) &&
            (left.Position - right.Position).sqrMagnitude < 0.0025f &&
            Quaternion.Angle(left.Rotation, right.Rotation) < 2f &&
            (left.Velocity - right.Velocity).sqrMagnitude < 0.01f;
    }

    private static void TryAddLiveSwitch(Protocol.WorldState delta, Protocol.SwitchState switchState)
    {
        delta.Switches.Add(switchState);
        if (!LiveWorldContainsAll(delta)) delta.Switches.RemoveAt(delta.Switches.Count - 1);
    }

    private static void TryAddLiveBrain(Protocol.WorldState delta, Protocol.PropState brainState)
    {
        delta.Brains.Add(brainState);
        if (!LiveWorldContainsAll(delta)) delta.Brains.RemoveAt(delta.Brains.Count - 1);
    }

    private static void TryAddLiveProp(Protocol.WorldState delta, Protocol.PropState propState)
    {
        delta.Props.Add(propState);
        if (!LiveWorldContainsAll(delta)) delta.Props.RemoveAt(delta.Props.Count - 1);
    }

    private static bool LiveWorldContainsAll(Protocol.WorldState state)
    {
        try
        {
            var bytes = Protocol.EncodeLiveWorld(state.Tick, state);
            if (!Protocol.TryDecode(bytes, out var packet) || !Protocol.TryReadWorld(packet, out var decoded)) return false;
            return decoded.Switches.Count == state.Switches.Count && decoded.Brains.Count == state.Brains.Count && decoded.Props.Count == state.Props.Count;
        }
        catch { return false; }
    }

    private void RememberSentWorld(Protocol.WorldState state, bool replace)
    {
        if (replace)
        {
            lastSentProps.Clear();
            lastSentBrains.Clear();
            lastSentSwitches.Clear();
        }
        foreach (var prop in state.Props) lastSentProps[prop.Path] = CloneWorldObject(prop);
        foreach (var brain in state.Brains) lastSentBrains[brain.Path] = CloneWorldObject(brain);
        foreach (var switchState in state.Switches) lastSentSwitches[switchState.Path] = switchState.State;
    }

    private static Protocol.PropState CloneWorldObject(Protocol.PropState source)
    {
        return new Protocol.PropState
        {
            Path = source.Path,
            HeldByBrainPath = source.HeldByBrainPath,
            BrainState = source.BrainState,
            Position = source.Position,
            Rotation = source.Rotation,
            Velocity = source.Velocity
        };
    }

    private bool SendInitialWorldSnapshot()
    {
        var state = BuildWorldState();
        if (state == null) return false;
        if (!Send(Protocol.EncodeInitialWorld(state.Tick, state), P2PSend.Reliable)) return false;
        RememberSentWorld(state, true);
        nextFullWorldResync = Time.unscaledTime + FullWorldResyncInterval;
        worldSnapshotsSent++;
        return true;
    }

    private Protocol.WorldState BuildWorldState()
    {
        var localGoose = GetGoose(0);
        var remoteGoose = GetGoose(1);
        if (localGoose == null || remoteGoose == null) return null;
        var state = new Protocol.WorldState { Tick = tick++, GooseA = ToPose(localGoose), GooseB = ToPose(remoteGoose) };
        RebuildPropCache();
        foreach (var prop in propsByPath.Values)
        {
            if (prop == null || prop.rb == null) continue;
            var heldByBrainPath = prop.heldBy != null && prop.heldBy.brain != null
                ? GetStablePath(prop.heldBy.brain.transform)
                : string.Empty;
            state.Props.Add(new Protocol.PropState { Path = GetPropKey(prop), HeldByBrainPath = heldByBrainPath, Position = prop.transform.position, Rotation = prop.transform.rotation, Velocity = prop.rb.velocity });
        }
        RebuildBrainCache();
        foreach (var brain in brainsByPath.Values)
        {
            if (brain == null) continue;
            state.Brains.Add(new Protocol.PropState { Path = GetStablePath(brain.transform), BrainState = GetBrainStateName(brain), Position = brain.transform.position, Rotation = brain.transform.rotation, Velocity = Vector3.zero });
        }
        RebuildSwitchCache();
        foreach (var pair in switchesByPath)
            if (pair.Value != null) state.Switches.Add(new Protocol.SwitchState { Path = pair.Key, State = pair.Value.currentState });
        return state;
    }

    private bool SendProgressSnapshot()
    {
        if (Role != OnlineRole.Host || !IsConnected) return false;
        RebuildSwitchCache();
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
        // Flush this packet immediately. World and progress packets share the
        // channel, and buffering the progress packet can leave the client with
        // only the initial world count visible.
        if (!Send(Protocol.EncodeProgress(tick++, state), P2PSend.Reliable)) return false;
        progressSnapshotsSent++;
        return true;
    }

    private void UpdateInitialSync()
    {
        if (!IsConnected || !IsGameplayReady()) return;

        if (Role == OnlineRole.Client && !localGameReadyAnnounced)
        {
            if (!Send(Protocol.Encode(MessageType.GameReady, tick++), P2PSend.Reliable)) return;
            localGameReadyAnnounced = true;
            status = "已进入游戏，等待主机同步进度";
        }

        if (Role == OnlineRole.Host && remoteGameReady && !initialStateSent)
        {
            SendInitialState();
        }
        else if (Role == OnlineRole.Host && remoteGameReady && initialStateSent && !initialProgressAcknowledged && Time.unscaledTime >= nextInitialProgressRetry)
        {
            nextInitialProgressRetry = Time.unscaledTime + 1f;
            SendProgressSnapshot();
        }
    }

    private bool IsGameplayReady()
    {
        var game = GameManager.instance;
        return game != null && game.readyForGeese && game.activeGooseCount >= 2 && GetGoose(0) != null && GetGoose(1) != null;
    }

    private void SendInitialState()
    {
        if (initialStateSent || Role != OnlineRole.Host || !IsConnected || !IsGameplayReady()) return;
        try
        {
            if (!SendInitialWorldSnapshot() || !SendProgressSnapshot()) return;
            initialStateSent = true;
            initialProgressAcknowledged = false;
            nextInitialProgressRetry = Time.unscaledTime + 1f;
            status = "双方已进入游戏，已发送初始世界进度";
        }
        catch (Exception ex) { Warn("发送初始世界进度失败: " + ex.Message); }
    }

    private void ApplyProgress(Protocol.ProgressState state)
    {
        if (state == null) return;
        RebuildSwitchCache();
        foreach (var switchState in state.Switches)
        {
            if (!TryFindSwitch(switchState.Path, out var system) || system == null) continue;
            authoritativeSwitchStates[GetStablePath(system.transform)] = switchState.State;
            if (system.currentState == switchState.State) continue;
            try { system.SetState(switchState.State, null); } catch (Exception ex) { Warn("同步门/开关失败: " + ex.Message); }
        }
        try
        {
            if (SaveGameData.instance?.entries != null)
                foreach (var save in state.Saves)
                {
                    var entry = SaveGameData.instance.GetSaveEntry(save.Key);
                    if (entry == null) continue;
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
            SaveManager.Save("GooseOnline.InitialSync");
        }
        catch (Exception ex) { Warn("应用关卡进度失败: " + ex.Message); }
    }

    private bool Send(byte[] data, P2PSend mode)
    {
        if (!peer.IsValid || data == null) return false;
        // Steam's unreliable modes cannot carry more than 1200 bytes.
        // Reliable sends fragment larger world snapshots inside Steam.
        if (data.Length > MaxUnreliablePacketBytes && (mode == P2PSend.Unreliable || mode == P2PSend.UnreliableNoDelay))
            mode = P2PSend.Reliable;
        try
        {
            if (SteamNetworking.SendP2PPacket(peer, data, data.Length, Channel, mode)) return true;
            if (Time.unscaledTime >= nextSendWarning)
            {
                nextSendWarning = Time.unscaledTime + 2f;
                Warn("Steam 拒绝发送网络包: " + data.Length + " bytes, " + mode);
            }
        }
        catch (Exception ex)
        {
            if (Time.unscaledTime >= nextSendWarning)
            {
                nextSendWarning = Time.unscaledTime + 2f;
                Warn("发送网络包失败: " + ex.Message);
            }
        }
        return false;
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
                break;
            case MessageType.Welcome:
                if (Role == OnlineRole.Client) { RefreshPeerInfo(); IsConnected = true; status = "已连接到房主"; }
                break;
            case MessageType.ClientPose:
                if (Role == OnlineRole.Host && Protocol.TryReadPose(packet, out var pose)) QueueRemotePose(pose);
                break;
            case MessageType.HostPose:
                if (Role == OnlineRole.Client && Protocol.TryReadPose(packet, out var hostPose)) QueueRemotePose(hostPose);
                break;
            case MessageType.GameReady:
                if (Role == OnlineRole.Host)
                {
                    remoteGameReady = true;
                    SendInitialState();
                }
                break;
            case MessageType.InitialWorldSnapshot:
                if (Role == OnlineRole.Client && !initialWorldApplied)
                {
                    if (Protocol.TryReadWorld(packet, out var state))
                    {
                        worldSnapshotsReceived++;
                        ApplyWorld(state);
                        initialWorldApplied = true;
                        if (initialProgressApplied) status = "已完成主机世界进度同步";
                    }
                    else if (Time.unscaledTime >= nextWorldSnapshotWarning)
                    {
                        nextWorldSnapshotWarning = Time.unscaledTime + 2f;
                        Warn("主机世界快照解析失败");
                    }
                }
                break;
            case MessageType.WorldSnapshot:
                if (Role == OnlineRole.Client)
                {
                    if (Protocol.TryReadWorld(packet, out var liveWorld))
                    {
                        worldSnapshotsReceived++;
                        ApplyLiveWorld(liveWorld);
                    }
                    else if (Time.unscaledTime >= nextWorldSnapshotWarning)
                    {
                        nextWorldSnapshotWarning = Time.unscaledTime + 2f;
                        Warn("主机实时世界快照解析失败");
                    }
                }
                break;
            case MessageType.GoalAwarded:
                if (Role == OnlineRole.Client && Protocol.TryReadGoal(packet, out var goalName)) ApplyGoal(goalName);
                break;
            case MessageType.ProgressSnapshot:
                if (Role == OnlineRole.Client)
                {
                    if (Protocol.TryReadProgress(packet, out var progress))
                    {
                        progressSnapshotsReceived++;
                        if (!initialProgressApplied)
                        {
                            ApplyProgress(progress);
                            initialProgressApplied = true;
                            if (initialWorldApplied) status = "已完成主机世界进度同步";
                        }
                        Send(Protocol.Encode(MessageType.ProgressApplied, packet.Tick), P2PSend.Reliable);
                    }
                    else if (Time.unscaledTime >= nextProgressSnapshotWarning)
                    {
                        nextProgressSnapshotWarning = Time.unscaledTime + 2f;
                        Warn("主机进度快照解析失败，Tick=" + packet.Tick + ", Bytes=" + (packet.Payload == null ? 0 : packet.Payload.Length));
                    }
                }
                break;
            case MessageType.ProgressApplied:
                if (Role == OnlineRole.Host)
                {
                    initialProgressAcknowledged = true;
                    Log("客户端已确认进度同步，Tick=" + packet.Tick);
                }
                break;
            case MessageType.Leave:
                IsConnected = false; status = "对方已离开"; break;
        }
    }

    private void ApplyWorld(Protocol.WorldState state)
    {
        // Host P1 is the remote goose on the client; host P2 is the client's local goose.
        if (remotePose == null)
        {
            ApplyGoosePose(1, state.GooseA);
            QueueRemotePose(state.GooseA);
        }
        ApplyGoosePose(0, state.GooseB);
        ApplyWorldObjects(state);
    }

    private void ApplyLiveWorld(Protocol.WorldState state)
    {
        ApplyWorldObjects(state);
    }

    private void ApplyWorldObjects(Protocol.WorldState state)
    {
        RebuildPropCache();
        var suppressedProps = GetWorldSuppressedProps();
        foreach (var propState in state.Props)
        {
            if (!TryFindProp(propState.Path, out var prop) || prop == null || prop.rb == null) continue;
            if (suppressedProps.Contains(prop)) continue;
            prop.transform.SetPositionAndRotation(propState.Position, propState.Rotation);
            prop.rb.velocity = propState.Velocity;
            remotePropTimes[GetPropKey(prop)] = Time.unscaledTime;
        }
        RebuildBrainCache();
        foreach (var brainState in state.Brains)
        {
            if (TryFindBrain(brainState.Path, out var brain) && brain != null)
            {
                if (Role == OnlineRole.Client && brain.agent != null)
                {
                    brain.agent.updatePosition = false;
                    brain.agent.updateRotation = false;
                    remoteBrainAgents.Add(brain);
                    if (brain.agent.isOnNavMesh) brain.agent.nextPosition = brain.transform.position;
                    remoteBrainTargets[brain] = CloneWorldObject(brainState);
                    authoritativeBrainStates[GetStablePath(brain.transform)] = brainState.BrainState ?? string.Empty;
                    ApplyBrainState(brain, brainState.BrainState);
                }
            }
        }
        ApplyNpcHeldProps(state);
        ApplyWorldSwitches(state);
    }

    private void ApplyWorldSwitches(Protocol.WorldState state)
    {
        RebuildSwitchCache();
        foreach (var switchState in state.Switches)
        {
            if (!TryFindSwitch(switchState.Path, out var system) || system == null)
            {
                if (Time.unscaledTime >= nextMissingSwitchWarning)
                {
                    nextMissingSwitchWarning = Time.unscaledTime + 2f;
                    Warn("找不到远端开关: " + switchState.Path);
                }
                continue;
            }
            authoritativeSwitchStates[GetStablePath(system.transform)] = switchState.State;
            if (system.currentState == switchState.State) continue;
            try { system.SetState(switchState.State, null); }
            catch (Exception ex) { Warn("同步实时开关失败: " + ex.Message); }
        }
    }

    private void ApplyNpcHeldProps(Protocol.WorldState state)
    {
        foreach (var propState in state.Props)
        {
            if (string.IsNullOrEmpty(propState.HeldByBrainPath)) continue;
            if (!TryFindProp(propState.Path, out var prop) || prop == null) continue;
            if (!TryFindBrain(propState.HeldByBrainPath, out var brain) || brain == null || brain.holder == null) continue;
            if (brain.holder.holding == prop) continue;
            var handle = FindHandleForProp(brain.holder, prop);
            if (handle == null || handle.prop == null) continue;
            try { brain.holder.PickUp(handle); }
            catch (Exception ex) { Warn("同步 NPC 携带物品失败: " + ex.Message); }
        }
    }

    private void TrackLocalHeldProp(string heldPath)
    {
        heldPath ??= string.Empty;
        if (!string.IsNullOrEmpty(lastLocalHeldPropPath) && string.IsNullOrEmpty(heldPath))
            localPropReleaseGraceUntil[lastLocalHeldPropPath] = Time.unscaledTime + 1.25f;
        lastLocalHeldPropPath = heldPath;
    }

    private HashSet<Prop> GetWorldSuppressedProps()
    {
        var suppressed = new HashSet<Prop>();
        foreach (var goose in GetGeese())
        {
            var held = goose != null && goose.dragger != null && goose.dragger.active ? goose.dragger.holdable : goose?.holder?.holding;
            if (held != null) suppressed.Add(held);
        }
        if (remotePose != null && TryFindProp(remotePose.HeldPropPath, out var remoteHeld) && remoteHeld != null)
            suppressed.Add(remoteHeld);

        var expired = new List<string>();
        foreach (var pair in localPropReleaseGraceUntil)
        {
            if (pair.Value <= Time.unscaledTime) { expired.Add(pair.Key); continue; }
            if (TryFindProp(pair.Key, out var released) && released != null) suppressed.Add(released);
        }
        foreach (var key in expired) localPropReleaseGraceUntil.Remove(key);
        return suppressed;
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
        var desiredNormalizedPath = NormalizePropKey(desiredPath);
        if (remoteHeldStateKnown && string.Equals(remoteAppliedHeldPropPath, desiredNormalizedPath, StringComparison.Ordinal) && remoteAppliedDragging == pose.IsDragging)
            return;
        var currentProp = goose.dragger.active ? goose.dragger.holdable : goose.holder.holding;
        var currentPath = currentProp == null ? string.Empty : GetPropKey(currentProp);
        var currentDragging = goose.dragger.active;
        var samePath = string.Equals(currentPath, desiredPath, StringComparison.Ordinal) ||
            string.Equals(NormalizePropKey(currentPath), desiredNormalizedPath, StringComparison.Ordinal);
        if (samePath && currentDragging == pose.IsDragging)
        {
            remoteHeldStateKnown = true;
            remoteAppliedHeldPropPath = desiredNormalizedPath;
            remoteAppliedDragging = pose.IsDragging;
            return;
        }

        if (currentProp != null)
        {
            try { goose.Drop(); } catch (Exception ex) { Warn("释放远端物品失败: " + ex.Message); }
        }
        if (string.IsNullOrEmpty(desiredPath))
        {
            remoteHeldStateKnown = true;
            remoteAppliedHeldPropPath = string.Empty;
            remoteAppliedDragging = false;
            return;
        }

        RebuildPropCache();
        if (!TryFindProp(desiredPath, out var prop) || prop == null)
        {
            if (Time.unscaledTime >= nextMissingPropWarning)
            {
                nextMissingPropWarning = Time.unscaledTime + 2f;
                Warn("找不到远端携带物品: " + desiredPath);
            }
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
            remoteHeldStateKnown = true;
            remoteAppliedHeldPropPath = desiredNormalizedPath;
            remoteAppliedDragging = pose.IsDragging;
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
                // Do not call GameManager.ActivateAllGeese here. The original
                // game may have created gooseManagers for one player while
                // preferredPlayerCount is already two, which makes that method
                // index past its internal array.
                EnsureGooseActive(GetGoose(0));
                EnsureGooseActive(GetGoose(1));
            }
            game?.PlayersChanged();
        }
        catch (Exception ex) { Warn("双人模式初始化失败: " + ex.Message); }
    }

    private void ResetInitialSyncState()
    {
        nextSnapshot = 0f;
        localGameReadyAnnounced = false;
        remoteGameReady = false;
        initialStateSent = false;
        initialWorldApplied = false;
        initialProgressApplied = false;
        initialProgressAcknowledged = false;
        nextWorldSnapshotWarning = 0f;
        nextProgressSnapshotWarning = 0f;
        nextSendWarning = 0f;
        nextInitialProgressRetry = 0f;
        nextMissingPropWarning = 0f;
        nextMissingSwitchWarning = 0f;
        nextFullWorldResync = 0f;
        switchSnapshotPending = false;
        authoritativeSwitchStates.Clear();
        lastSentProps.Clear();
        lastSentBrains.Clear();
        lastSentSwitches.Clear();
        remoteBrainTargets.Clear();
        authoritativeBrainStates.Clear();
        RestoreRemoteBrainAgents();
        remoteHeldStateKnown = false;
        remoteAppliedHeldPropPath = string.Empty;
        remoteAppliedDragging = false;
        lastLocalHeldPropPath = string.Empty;
        localPropReleaseGraceUntil.Clear();
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
        propsByAlias.Clear();
        foreach (var prop in UObject.FindObjectsOfType<Prop>())
            if (prop != null) AddPropToCache(prop);
    }

    private void AddPropToCache(Prop prop)
    {
        var key = GetPropKey(prop);
        propsByPath[key] = prop;
        foreach (var alias in GetPropAliases(prop, key))
            if (!propsByAlias.ContainsKey(alias)) propsByAlias[alias] = prop;
    }

    private bool TryFindProp(string requestedKey, out Prop prop)
    {
        prop = null;
        if (string.IsNullOrEmpty(requestedKey)) return false;
        if (propsByPath.TryGetValue(requestedKey, out prop) && prop != null) return true;
        foreach (var alias in GetPropAliases(null, requestedKey))
            if (propsByAlias.TryGetValue(alias, out prop) && prop != null) return true;
        return false;
    }

    private static IEnumerable<string> GetPropAliases(Prop prop, string key)
    {
        if (!string.IsNullOrEmpty(key))
        {
            yield return key;
            yield return NormalizePropKey(key);
            var slash = key.LastIndexOf('/');
            if (slash >= 0 && slash + 1 < key.Length) yield return key.Substring(slash + 1).ToLowerInvariant();
        }
        if (prop != null && !string.IsNullOrEmpty(prop.name)) yield return prop.name.ToLowerInvariant();
    }

    private static string NormalizePropKey(string key)
    {
        return Regex.Replace((key ?? string.Empty).ToLowerInvariant(), @"\[[0-9]+\]", string.Empty);
    }

    private static string GetPropKey(Prop prop)
    {
        if (prop == null) return string.Empty;
        if (prop.startHome != null) return "prop:" + GetStablePath(prop.startHome.transform);
        return "prop-name:" + prop.name;
    }

    private static BeakHandle FindHandleForProp(Goose goose, Prop prop)
    {
        return FindHandleForProp(goose == null ? null : goose.holder, prop);
    }

    private static BeakHandle FindHandleForProp(Holder holder, Prop prop)
    {
        if (prop == null || prop.beakHandles == null || prop.beakHandles.Length == 0) return null;
        var origin = holder != null ? holder.transform.position : prop.transform.position;
        return prop.beakHandles
            .Where(handle => handle != null && handle.prop == prop && handle.isActiveAndEnabled)
            .OrderBy(handle => (handle.transform.position - origin).sqrMagnitude)
            .FirstOrDefault();
    }

    private void RebuildBrainCache()
    {
        brainsByPath.Clear();
        brainsByAlias.Clear();
        foreach (var brain in UObject.FindObjectsOfType<Brain>())
        {
            if (brain == null) continue;
            var key = GetStablePath(brain.transform);
            brainsByPath[key] = brain;
            AddAlias(brainsByAlias, key, brain);
        }
    }

    private static string GetBrainStateName(Brain brain)
    {
        return brain == null || brain.currentState == null ? string.Empty : brain.currentState.GetType().Name;
    }

    private static void ApplyBrainState(Brain brain, string stateName)
    {
        if (brain == null || brain.brainStates == null || brain.brainStates.allStates == null || string.IsNullOrEmpty(stateName)) return;
        if (brain.currentState != null && string.Equals(brain.currentState.GetType().Name, stateName, StringComparison.Ordinal)) return;
        var target = brain.brainStates.allStates.FirstOrDefault(state => state != null && string.Equals(state.GetType().Name, stateName, StringComparison.Ordinal));
        if (target == null) return;
        try
        {
            var swapTo = brain.brainStates.GetType().GetMethod("SwapTo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            swapTo?.Invoke(brain.brainStates, new object[] { target });
        }
        catch { }
    }

    private void RestoreRemoteBrainAgents()
    {
        foreach (var brain in remoteBrainAgents)
        {
            if (brain == null || brain.agent == null) continue;
            brain.agent.updatePosition = true;
            brain.agent.updateRotation = true;
        }
        remoteBrainAgents.Clear();
    }

    private void RebuildSwitchCache()
    {
        switchesByPath.Clear();
        switchesByAlias.Clear();
        foreach (var system in UObject.FindObjectsOfType<SwitchSystem>())
        {
            if (system == null) continue;
            var key = GetStablePath(system.transform);
            switchesByPath[key] = system;
            AddAlias(switchesByAlias, key, system);
        }
    }

    private static void AddAlias<T>(Dictionary<string, T> aliases, string key, T value) where T : class
    {
        aliases[NormalizePropKey(key)] = value;
        var slash = key.LastIndexOf('/');
        if (slash >= 0 && slash + 1 < key.Length)
        {
            var leaf = key.Substring(slash + 1).ToLowerInvariant();
            aliases[leaf] = value;
            aliases[NormalizePropKey(leaf)] = value;
        }
    }

    private bool TryFindBrain(string key, out Brain brain)
    {
        brain = null;
        if (string.IsNullOrEmpty(key)) return false;
        if (brainsByPath.TryGetValue(key, out brain) && brain != null) return true;
        if (brainsByAlias.TryGetValue(NormalizePropKey(key), out brain) && brain != null) return true;
        var slash = key.LastIndexOf('/');
        if (slash >= 0 && slash + 1 < key.Length)
        {
            var leaf = key.Substring(slash + 1).ToLowerInvariant();
            if (brainsByAlias.TryGetValue(leaf, out brain) && brain != null) return true;
            if (brainsByAlias.TryGetValue(NormalizePropKey(leaf), out brain) && brain != null) return true;
        }
        return false;
    }

    private bool TryFindSwitch(string key, out SwitchSystem system)
    {
        system = null;
        if (string.IsNullOrEmpty(key)) return false;
        if (switchesByPath.TryGetValue(key, out system) && system != null) return true;
        if (switchesByAlias.TryGetValue(NormalizePropKey(key), out system) && system != null) return true;
        var slash = key.LastIndexOf('/');
        if (slash >= 0 && slash + 1 < key.Length)
        {
            var leaf = key.Substring(slash + 1).ToLowerInvariant();
            if (switchesByAlias.TryGetValue(leaf, out system) && system != null) return true;
            if (switchesByAlias.TryGetValue(NormalizePropKey(leaf), out system) && system != null) return true;
        }
        return false;
    }

    internal void RequestImmediateWorldSnapshot()
    {
        if (Role == OnlineRole.Host && IsConnected && IsGameplayReady()) switchSnapshotPending = true;
    }

    internal bool AllowSwitchStateChange(SwitchSystem system, int state)
    {
        if (Role != OnlineRole.Client || system == null) return true;
        if (!authoritativeSwitchStates.TryGetValue(GetStablePath(system.transform), out var authoritativeState)) return true;
        return authoritativeState == state;
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
        lobby = null; peer = default(SteamId); Role = OnlineRole.None; IsConnected = false; status = "单机模式"; connectedPlayerName = "未连接"; connectedPlayerId = "-"; remotePose = null; ResetInitialSyncState(); worldSnapshotsSent = 0; worldSnapshotsReceived = 0; progressSnapshotsSent = 0; progressSnapshotsReceived = 0;
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

[HarmonyPatch(typeof(SwitchSystem), nameof(SwitchSystem.SetState))]
internal static class SwitchStatePatch
{
    private static bool Prefix(SwitchSystem __instance, int state)
    {
        var runtime = OnlineRuntime.Instance;
        return runtime == null || runtime.AllowSwitchStateChange(__instance, state);
    }

    private static void Postfix(SwitchSystem __instance)
    {
        var runtime = OnlineRuntime.Instance;
        if (runtime != null) runtime.RequestImmediateWorldSnapshot();
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







