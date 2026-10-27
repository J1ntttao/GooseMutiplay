using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace GooseOnline;

internal enum MessageType : byte
{
    Hello = 1,
    Welcome = 2,
    ClientPose = 3,
    WorldSnapshot = 4,
    Leave = 5,
    Ping = 6,
    GoalAwarded = 7,
    ProgressSnapshot = 8,
    GameReady = 9,
    InitialWorldSnapshot = 10,
    ProgressApplied = 11,
    HostPose = 12,
}

internal static class Protocol
{
    internal const uint Magic = 0x474F4F53; // GOOS
    internal const byte Version = 5;
    internal const int HeaderBytes = sizeof(uint) + sizeof(byte) + sizeof(byte) + sizeof(int) + sizeof(int);
    internal const int MaxPacketBytes = 64 * 1024;
    internal const int MaxLivePacketBytes = 1200;

    internal sealed class Packet
    {
        public MessageType Type;
        public int Tick;
        public byte[] Payload;
    }

    internal sealed class Pose
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public float NeckAmount;
        public bool NeckToggle;
        public bool IsDragging;
        public string HeldPropPath;
    }

    internal sealed class PropState
    {
        public string Path;
        public string HeldByBrainPath;
        public string BrainState;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
    }

    internal sealed class WorldState
    {
        public int Tick;
        public Pose GooseA;
        public Pose GooseB;
        public List<PropState> Props = new List<PropState>();
        public List<PropState> Brains = new List<PropState>();
        public List<SwitchState> Switches = new List<SwitchState>();
    }

    internal sealed class SwitchState
    {
        public string Path;
        public int State;
    }

    internal sealed class SaveState
    {
        public string Key;
        public bool BoolValue;
        public int IntValue;
    }

    internal sealed class GoalState
    {
        public string Name;
        public bool Awarded;
        public bool Hidden;
    }

    internal sealed class ProgressState
    {
        public int Area;
        public List<SwitchState> Switches = new List<SwitchState>();
        public List<SaveState> Saves = new List<SaveState>();
        public List<GoalState> Goals = new List<GoalState>();
    }

    internal static byte[] Encode(MessageType type, int tick, Action<BinaryWriter> body = null)
    {
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write((byte)type);
            writer.Write(tick);
            using (var payload = new MemoryStream())
            using (var payloadWriter = new BinaryWriter(payload))
            {
                body?.Invoke(payloadWriter);
                payloadWriter.Flush();
                var bytes = payload.ToArray();
                if (bytes.Length > MaxPacketBytes - HeaderBytes) throw new InvalidDataException("Payload too large");
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
            writer.Flush();
            return ms.ToArray();
        }
    }

    internal static bool TryDecode(byte[] data, out Packet packet)
    {
        packet = null;
        if (data == null || data.Length < HeaderBytes || data.Length > MaxPacketBytes) return false;
        try
        {
            using (var ms = new MemoryStream(data, false))
            using (var reader = new BinaryReader(ms))
            {
                if (reader.ReadUInt32() != Magic) return false;
                if (reader.ReadByte() != Version) return false;
                var type = (MessageType)reader.ReadByte();
                var tick = reader.ReadInt32();
                var length = reader.ReadInt32();
                if (length < 0 || length > MaxPacketBytes || length != ms.Length - ms.Position) return false;
                packet = new Packet { Type = type, Tick = tick, Payload = reader.ReadBytes(length) };
                return packet.Payload.Length == length;
            }
        }
        catch { return false; }
    }

    internal static byte[] EncodePose(int tick, Pose pose) => Encode(MessageType.ClientPose, tick, writer => WritePose(writer, pose));

    internal static byte[] EncodeHostPose(int tick, Pose pose) => Encode(MessageType.HostPose, tick, writer => WritePose(writer, pose));

    internal static bool TryReadPose(Packet packet, out Pose pose)
    {
        pose = null;
        if (packet?.Type != MessageType.ClientPose && packet?.Type != MessageType.HostPose) return false;
        try
        {
            using (var ms = new MemoryStream(packet.Payload, false))
            using (var reader = new BinaryReader(ms)) { pose = ReadPose(reader); return true; }
        }
        catch { return false; }
    }

    internal static byte[] EncodeWorld(int tick, WorldState state) => EncodeWorld(MessageType.WorldSnapshot, tick, state, MaxPacketBytes);

    internal static byte[] EncodeLiveWorld(int tick, WorldState state) => EncodeWorld(MessageType.WorldSnapshot, tick, state, MaxLivePacketBytes);

    internal static byte[] EncodeInitialWorld(int tick, WorldState state) => EncodeWorld(MessageType.InitialWorldSnapshot, tick, state, MaxPacketBytes);

    private static byte[] EncodeWorld(MessageType type, int tick, WorldState state, int packetLimit) => Encode(type, tick, writer =>
    {
        WritePose(writer, state.GooseA);
        WritePose(writer, state.GooseB);
        var remaining = packetLimit - HeaderBytes - 12 - MeasurePose(state.GooseA) - MeasurePose(state.GooseB);
        var switchItems = FitItems(state.Switches.Take(512), WriteSwitch, ref remaining);
        var brainItems = FitItems(state.Brains.Take(128), WriteBrain, ref remaining);
        var propItems = FitItems(state.Props.Take(512).OrderByDescending(p => !string.IsNullOrEmpty(p.HeldByBrainPath)), WriteProp, ref remaining);
        writer.Write(propItems.Count);
        foreach (var item in propItems) writer.Write(item);
        writer.Write(brainItems.Count);
        foreach (var item in brainItems) writer.Write(item);
        writer.Write(switchItems.Count);
        foreach (var item in switchItems) writer.Write(item);
    });

    private static List<byte[]> FitItems<T>(IEnumerable<T> items, Action<BinaryWriter, T> write, ref int remaining)
    {
        var result = new List<byte[]>();
        foreach (var item in items)
        {
            using (var ms = new MemoryStream())
            using (var itemWriter = new BinaryWriter(ms))
            {
                write(itemWriter, item);
                itemWriter.Flush();
                var bytes = ms.ToArray();
                if (bytes.Length > remaining) continue;
                result.Add(bytes);
                remaining -= bytes.Length;
            }
        }
        return result;
    }

    private static void WriteProp(BinaryWriter writer, PropState prop)
    {
        writer.Write(prop.Path ?? string.Empty);
        writer.Write(prop.HeldByBrainPath ?? string.Empty);
        WritePose(writer, new Pose { Position = prop.Position, Rotation = prop.Rotation, Velocity = prop.Velocity });
    }

    private static void WriteBrain(BinaryWriter writer, PropState brain)
    {
        writer.Write(brain.Path ?? string.Empty);
        writer.Write(brain.HeldByBrainPath ?? string.Empty);
        writer.Write(brain.BrainState ?? string.Empty);
        WritePose(writer, new Pose { Position = brain.Position, Rotation = brain.Rotation, Velocity = Vector3.zero });
    }

    private static void WriteSwitch(BinaryWriter writer, SwitchState switchState)
    {
        writer.Write(switchState.Path ?? string.Empty);
        writer.Write(switchState.State);
    }

    private static int MeasurePose(Pose pose)
    {
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms))
        {
            WritePose(writer, pose);
            writer.Flush();
            return (int)ms.Length;
        }
    }

    internal static bool TryReadWorld(Packet packet, out WorldState state)
    {
        state = null;
        if (packet?.Type != MessageType.WorldSnapshot && packet?.Type != MessageType.InitialWorldSnapshot) return false;
        try
        {
            using (var ms = new MemoryStream(packet.Payload, false))
            using (var reader = new BinaryReader(ms))
            {
                state = new WorldState { Tick = packet.Tick, GooseA = ReadPose(reader), GooseB = ReadPose(reader) };
                var count = reader.ReadInt32();
                if (count < 0 || count > 512) return false;
                for (var i = 0; i < count; i++)
                {
                    var path = reader.ReadString();
                    var heldByBrainPath = reader.ReadString();
                    var pose = ReadPose(reader);
                    state.Props.Add(new PropState { Path = path, HeldByBrainPath = heldByBrainPath, Position = pose.Position, Rotation = pose.Rotation, Velocity = pose.Velocity });
                }
                var brainCount = reader.ReadInt32();
                if (brainCount < 0 || brainCount > 128) return false;
                for (var i = 0; i < brainCount; i++)
                {
                    var path = reader.ReadString();
                    var heldByBrainPath = reader.ReadString();
                    var brainState = reader.ReadString();
                    var pose = ReadPose(reader);
                    state.Brains.Add(new PropState { Path = path, HeldByBrainPath = heldByBrainPath, BrainState = brainState, Position = pose.Position, Rotation = pose.Rotation, Velocity = pose.Velocity });
                }
                var switchCount = reader.ReadInt32();
                if (switchCount < 0 || switchCount > 512) return false;
                for (var i = 0; i < switchCount; i++)
                    state.Switches.Add(new SwitchState { Path = reader.ReadString(), State = reader.ReadInt32() });
                return true;
            }
        }
        catch { return false; }
    }

    internal static byte[] EncodeProgress(int tick, ProgressState state) => Encode(MessageType.ProgressSnapshot, tick, writer =>
    {
        writer.Write(state.Area);
        var remaining = MaxPacketBytes - HeaderBytes - 16; // area plus three list counts
        var goalItems = FitItems(state.Goals.Take(256), WriteGoal, ref remaining);
        var switchItems = FitItems(state.Switches.Take(512), WriteSwitch, ref remaining);
        var saveItems = FitItems(state.Saves.Take(1024), WriteSave, ref remaining);
        writer.Write(switchItems.Count);
        foreach (var item in switchItems) writer.Write(item);
        writer.Write(saveItems.Count);
        foreach (var item in saveItems) writer.Write(item);
        writer.Write(goalItems.Count);
        foreach (var item in goalItems) writer.Write(item);
    });

    private static void WriteSave(BinaryWriter writer, SaveState save)
    {
        writer.Write(save.Key ?? string.Empty);
        writer.Write(save.BoolValue);
        writer.Write(save.IntValue);
    }

    private static void WriteGoal(BinaryWriter writer, GoalState goal)
    {
        writer.Write(goal.Name ?? string.Empty);
        writer.Write(goal.Awarded);
        writer.Write(goal.Hidden);
    }

    internal static bool TryReadProgress(Packet packet, out ProgressState state)
    {
        state = null;
        if (packet?.Type != MessageType.ProgressSnapshot) return false;
        try
        {
            using (var ms = new MemoryStream(packet.Payload, false))
            using (var reader = new BinaryReader(ms))
            {
                state = new ProgressState { Area = reader.ReadInt32() };
                var switchCount = reader.ReadInt32();
                if (switchCount < 0 || switchCount > 512) return false;
                for (var i = 0; i < switchCount; i++)
                    state.Switches.Add(new SwitchState { Path = reader.ReadString(), State = reader.ReadInt32() });
                var saveCount = reader.ReadInt32();
                if (saveCount < 0 || saveCount > 1024) return false;
                for (var i = 0; i < saveCount; i++)
                    state.Saves.Add(new SaveState { Key = reader.ReadString(), BoolValue = reader.ReadBoolean(), IntValue = reader.ReadInt32() });
                var goalCount = reader.ReadInt32();
                if (goalCount < 0 || goalCount > 256) return false;
                for (var i = 0; i < goalCount; i++)
                    state.Goals.Add(new GoalState { Name = reader.ReadString(), Awarded = reader.ReadBoolean(), Hidden = reader.ReadBoolean() });
                return true;
            }
        }
        catch { return false; }
    }

    internal static byte[] EncodeGoal(int tick, string goalName) => Encode(MessageType.GoalAwarded, tick, writer => writer.Write(goalName ?? string.Empty));

    internal static bool TryReadGoal(Packet packet, out string goalName)
    {
        goalName = null;
        if (packet?.Type != MessageType.GoalAwarded) return false;
        try
        {
            using (var ms = new MemoryStream(packet.Payload, false))
            using (var reader = new BinaryReader(ms)) { goalName = reader.ReadString(); return goalName.Length <= 256; }
        }
        catch { return false; }
    }

    private static void WritePose(BinaryWriter writer, Pose pose)
    {
        pose ??= new Pose();
        writer.Write(pose.Position.x); writer.Write(pose.Position.y); writer.Write(pose.Position.z);
        writer.Write(pose.Rotation.x); writer.Write(pose.Rotation.y); writer.Write(pose.Rotation.z); writer.Write(pose.Rotation.w);
        writer.Write(pose.Velocity.x); writer.Write(pose.Velocity.y); writer.Write(pose.Velocity.z);
        writer.Write(pose.NeckAmount);
        writer.Write(pose.NeckToggle);
        writer.Write(pose.IsDragging);
        writer.Write(pose.HeldPropPath ?? string.Empty);
    }

    private static Pose ReadPose(BinaryReader reader) => new Pose
    {
        Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
        Rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
        Velocity = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
        NeckAmount = reader.ReadSingle(),
        NeckToggle = reader.ReadBoolean(),
        IsDragging = reader.ReadBoolean(),
        HeldPropPath = reader.ReadString()
    };
}
