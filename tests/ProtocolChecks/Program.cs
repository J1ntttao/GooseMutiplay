using GooseOnline;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

// These control packets intentionally have no payload, including the host's Welcome.
foreach (var type in new[] { MessageType.Welcome, MessageType.Leave, MessageType.Ping })
{
    var bytes = Protocol.Encode(type, 42);
    Check(bytes.Length == 14, "Wire header must be 14 bytes");
    Check(Protocol.TryDecode(bytes, out var decoded), type + " must decode without payload");
    Check(decoded.Type == type && decoded.Tick == 42 && decoded.Payload.Length == 0, "Control packet changed");
    for (var i = 0; i < bytes.Length; i++)
        Check(!Protocol.TryDecode(bytes.Take(i).ToArray(), out _), "Truncated header accepted");
}

var progressAck = Protocol.Encode(MessageType.ProgressApplied, 8);
Check(Protocol.TryDecode(progressAck, out var progressAckPacket), "Progress acknowledgement cannot decode");
Check(progressAckPacket.Type == MessageType.ProgressApplied && progressAckPacket.Tick == 8 && progressAckPacket.Payload.Length == 0, "Progress acknowledgement changed");

var hello = Protocol.Encode(MessageType.Hello, 0, w => { w.Write("0.4.19"); w.Write(Protocol.Version); });
Check(Protocol.TryDecode(hello, out var receivedHello), "Host cannot receive Hello");
Check(receivedHello.Type == MessageType.Hello, "Wrong handshake request");
var welcome = Protocol.Encode(MessageType.Welcome, 1);
Check(Protocol.TryDecode(welcome, out var receivedWelcome), "Client cannot receive Welcome");
Check(receivedWelcome.Type == MessageType.Welcome, "Wrong handshake response");

var poseBytes = Protocol.EncodePose(7, new Protocol.Pose
{
    Position = new UnityEngine.Vector3(1f, 2f, 3f),
    Rotation = new UnityEngine.Quaternion(0f, 0.5f, 0f, 0.5f),
    Velocity = new UnityEngine.Vector3(4f, 5f, 6f),
    NeckAmount = 0.75f,
    NeckToggle = true,
    IsDragging = false,
    HeldPropPath = "World/Prop[3]"
});
Check(Protocol.TryDecode(poseBytes, out var posePacket), "Pose packet cannot decode");
Check(Protocol.TryReadPose(posePacket, out var pose), "Pose state cannot decode");
Check(Math.Abs(pose.NeckAmount - 0.75f) < 0.001f && pose.NeckToggle && pose.HeldPropPath == "World/Prop[3]", "Goose action state changed");
var hostPoseBytes = Protocol.EncodeHostPose(8, new Protocol.Pose { Position = new UnityEngine.Vector3(9f, 0f, 0f) });
Check(Protocol.TryDecode(hostPoseBytes, out var hostPosePacket), "Host pose packet cannot decode");
Check(hostPosePacket.Type == MessageType.HostPose && Protocol.TryReadPose(hostPosePacket, out var hostPose) && hostPose.Position.x == 9f, "Host pose changed");

var progressBytes = Protocol.EncodeProgress(8, new Protocol.ProgressState
{
    Area = 2,
    Switches = { new Protocol.SwitchState { Path = "World/Door[1]", State = 1 } },
    Saves = { new Protocol.SaveState { Key = "goalGarden", BoolValue = true, IntValue = 3 } },
    Goals = { new Protocol.GoalState { Name = "goalGarden", Awarded = true, Hidden = false } }
});
Check(Protocol.TryDecode(progressBytes, out var progressPacket), "Progress packet cannot decode");
Check(Protocol.TryReadProgress(progressPacket, out var progress), "Progress state cannot decode");
Check(progress.Area == 2 && progress.Switches.Count == 1 && progress.Saves[0].BoolValue && progress.Goals[0].Awarded, "Progress state changed");

var largeProgressState = new Protocol.ProgressState { Area = 3 };
for (var i = 0; i < 512; i++)
    largeProgressState.Switches.Add(new Protocol.SwitchState { Path = "World/Switch[" + i + "]/LongName", State = i % 3 });
for (var i = 0; i < 1024; i++)
    largeProgressState.Saves.Add(new Protocol.SaveState { Key = "saveKey[" + i + "]/LongName", BoolValue = i % 2 == 0, IntValue = i });
for (var i = 0; i < 256; i++)
    largeProgressState.Goals.Add(new Protocol.GoalState { Name = "goal[" + i + "]/LongName", Awarded = i % 2 == 0, Hidden = false });
var boundedProgressBytes = Protocol.EncodeProgress(9, largeProgressState);
Check(boundedProgressBytes.Length <= Protocol.MaxPacketBytes, "Large progress snapshot exceeded packet limit");
Check(Protocol.TryDecode(boundedProgressBytes, out var boundedProgressPacket), "Bounded large progress packet cannot decode");
Check(Protocol.TryReadProgress(boundedProgressPacket, out var boundedProgress), "Bounded large progress cannot decode");
Check(boundedProgress.Switches.Count > 0 && boundedProgress.Goals.Count > 0, "Large progress priority state was dropped");

var initialWorldBytes = Protocol.EncodeInitialWorld(9, new Protocol.WorldState
{
    GooseA = new Protocol.Pose { Position = new UnityEngine.Vector3(10f, 0f, 0f) },
    GooseB = new Protocol.Pose { Position = new UnityEngine.Vector3(20f, 0f, 0f) }
});
Check(Protocol.TryDecode(initialWorldBytes, out var initialWorldPacket), "Initial world packet cannot decode");
Check(Protocol.TryReadWorld(initialWorldPacket, out var initialWorld), "Initial world state cannot decode");
Check(initialWorldPacket.Type == MessageType.InitialWorldSnapshot && initialWorld.GooseA.Position.x == 10f && initialWorld.GooseB.Position.x == 20f, "Initial goose positions changed");

// Live snapshots include enough world objects to exceed Steam's unreliable limit.
var liveWorldState = new Protocol.WorldState
{
    GooseA = initialWorld.GooseA,
    GooseB = initialWorld.GooseB,
    Brains = { new Protocol.PropState { Path = "World/Garden/Gardener[0]", BrainState = "SwitchState", Position = new UnityEngine.Vector3(3f, 0f, 4f) } },
    Switches = { new Protocol.SwitchState { Path = "World/Garden/Tap[2]", State = 1 } }
};
for (var i = 0; i < 32; i++)
    liveWorldState.Props.Add(new Protocol.PropState { Path = "World/Garden/Prop[" + i + "]", HeldByBrainPath = i == 7 ? "World/Garden/Gardener[0]" : string.Empty, Position = new UnityEngine.Vector3(i, 0f, 0f) });
for (var worldTick = 10; worldTick < 13; worldTick++)
{
    liveWorldState.GooseA.Position.x = worldTick;
    var liveWorldBytes = Protocol.EncodeWorld(worldTick, liveWorldState);
    Check(liveWorldBytes.Length > 1200, "World snapshot must cover a packet larger than Steam's unreliable limit");
    Check(Protocol.TryDecode(liveWorldBytes, out var livePacket), "Live world packet cannot decode");
    Check(livePacket.Type == MessageType.WorldSnapshot && livePacket.Tick == worldTick, "Live snapshot type or tick changed");
    Check(Protocol.TryReadWorld(livePacket, out var liveWorld), "Live world state cannot decode");
    Check(liveWorld.GooseA.Position.x == worldTick && liveWorld.GooseB.Position.x == 20f, "Live goose positions changed");
    Check(liveWorld.Props.Count == 32 && liveWorld.Props.Any(p => p.HeldByBrainPath == "World/Garden/Gardener[0]") && liveWorld.Props.Any(p => p.Position.x == 31f) && liveWorld.Brains[0].Position.z == 4f && liveWorld.Brains[0].BrainState == "SwitchState" && liveWorld.Switches[0].State == 1, "Live world objects changed");
    Check(!Protocol.TryReadWorld(new Protocol.Packet { Type = livePacket.Type, Payload = livePacket.Payload.Take(livePacket.Payload.Length - 1).ToArray() }, out _), "Truncated live world accepted");
}

var largeWorldState = new Protocol.WorldState { GooseA = initialWorld.GooseA, GooseB = initialWorld.GooseB };
for (var i = 0; i < 512; i++)
{
    largeWorldState.Props.Add(new Protocol.PropState { Path = "World/Dynamic/Prop[" + i + "]/LongName", Position = new UnityEngine.Vector3(i, 0f, 0f) });
    largeWorldState.Switches.Add(new Protocol.SwitchState { Path = "World/Dynamic/Switch[" + i + "]/LongName", State = i % 3 });
}
for (var i = 0; i < 128; i++)
    largeWorldState.Brains.Add(new Protocol.PropState { Path = "World/Dynamic/Brain[" + i + "]/LongName", Position = new UnityEngine.Vector3(i, 0f, 0f) });
var boundedWorldBytes = Protocol.EncodeWorld(99, largeWorldState);
Check(boundedWorldBytes.Length <= Protocol.MaxPacketBytes, "Large world snapshot exceeded packet limit");
Check(Protocol.TryDecode(boundedWorldBytes, out var boundedWorldPacket), "Bounded large world packet cannot decode");
Check(Protocol.TryReadWorld(boundedWorldPacket, out var boundedWorld), "Bounded large world cannot decode");
Check(boundedWorld.Switches.Count > 0 && boundedWorld.Brains.Count > 0, "Large world priority state was dropped");

var compactWorldBytes = Protocol.EncodeLiveWorld(100, new Protocol.WorldState
{
    GooseA = initialWorld.GooseA,
    GooseB = initialWorld.GooseB,
    Brains = { new Protocol.PropState { Path = "World/Garden/Gardener[0]", Position = new UnityEngine.Vector3(4f, 0f, 5f) } },
    Switches = { new Protocol.SwitchState { Path = "World/Garden/Tap[2]", State = 0 } },
    Props = { new Protocol.PropState { Path = "prop:World/Garden/Prop[7]", Position = new UnityEngine.Vector3(7f, 0f, 1f) } }
});
Check(compactWorldBytes.Length <= Protocol.MaxLivePacketBytes, "Live world delta exceeded unreliable packet limit");
Check(Protocol.TryDecode(compactWorldBytes, out var compactWorldPacket), "Live world delta cannot decode");
Check(Protocol.TryReadWorld(compactWorldPacket, out var liveWorldDelta) && liveWorldDelta.Switches.Count == 1 && liveWorldDelta.Brains.Count == 1 && liveWorldDelta.Props.Count == 1, "Live world delta changed");

var extraByte = welcome.Concat(new byte[] { 1 }).ToArray();
Check(!Protocol.TryDecode(extraByte, out _), "Unannounced extra bytes accepted");
var corruptLength = (byte[])welcome.Clone();
Array.Copy(BitConverter.GetBytes(-1), 0, corruptLength, 10, 4);
Check(!Protocol.TryDecode(corruptLength, out _), "Negative length accepted");
var maxPayload = new byte[Protocol.MaxPacketBytes - Protocol.HeaderBytes];
Check(Protocol.TryDecode(Protocol.Encode(MessageType.Ping, 0, w => w.Write(maxPayload)), out _), "Maximum packet rejected");
Check(!Protocol.TryDecode(new byte[Protocol.MaxPacketBytes + 1], out _), "Oversized packet accepted");
Check(!Protocol.TryDecode(null, out _), "Null packet accepted");
Console.WriteLine("PASS: controls, handshake, poses, progress, initial/live worlds over 1200 bytes, truncation, corrupt lengths and packet limits.");
