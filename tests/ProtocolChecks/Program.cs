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

var hello = Protocol.Encode(MessageType.Hello, 0, w => { w.Write("0.3.4"); w.Write(Protocol.Version); });
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

var extraByte = welcome.Concat(new byte[] { 1 }).ToArray();
Check(!Protocol.TryDecode(extraByte, out _), "Unannounced extra bytes accepted");
var corruptLength = (byte[])welcome.Clone();
Array.Copy(BitConverter.GetBytes(-1), 0, corruptLength, 10, 4);
Check(!Protocol.TryDecode(corruptLength, out _), "Negative length accepted");
var maxPayload = new byte[Protocol.MaxPacketBytes - Protocol.HeaderBytes];
Check(Protocol.TryDecode(Protocol.Encode(MessageType.Ping, 0, w => w.Write(maxPayload)), out _), "Maximum packet rejected");
Check(!Protocol.TryDecode(new byte[Protocol.MaxPacketBytes + 1], out _), "Oversized packet accepted");
Check(!Protocol.TryDecode(null, out _), "Null packet accepted");
Console.WriteLine("PASS: empty controls, Hello/Welcome exchange, truncation, corrupt lengths and packet limits.");
