using System;
using System.Collections.Generic;
using System.IO;
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
}

internal static class Protocol
{
    internal const uint Magic = 0x474F4F53; // GOOS
    internal const byte Version = 3;
    internal const int HeaderBytes = sizeof(uint) + sizeof(byte) + sizeof(byte) + sizeof(int) + sizeof(int);
    internal const int MaxPacketBytes = 64 * 1024;

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

    internal static bool TryReadPose(Packet packet, out Pose pose)
    {
        pose = null;
        if (packet?.Type != MessageType.ClientPose) return false;
        try
        {
            using (var ms = new MemoryStream(packet.Payload, false))
            using (var reader = new BinaryReader(ms)) { pose = ReadPose(reader); return true; }
        }
        catch { return false; }
    }

    internal static byte[] EncodeWorld(int tick, WorldState state) => EncodeWorld(MessageType.WorldSnapshot, tick, state);

    internal static byte[] EncodeInitialWorld(int tick, WorldState state) => EncodeWorld(MessageType.InitialWorldSnapshot, tick, state);

    private static byte[] EncodeWorld(MessageType type, int tick, WorldState state) => Encode(type, tick, writer =>
    {
        WritePose(writer, state.GooseA);
        WritePose(writer, state.GooseB);
        writer.Write(Math.Min(state.Props.Count, 512));
        for (var i = 0; i < state.Props.Count && i < 512; i++)
        {
            var prop = state.Props[i];
            writer.Write(prop.Path ?? string.Empty);
            WritePose(writer, new Pose { Position = prop.Position, Rotation = prop.Rotation, Velocity = prop.Velocity });
        }
        writer.Write(Math.Min(state.Brains.Count, 128));
        for (var i = 0; i < state.Brains.Count && i < 128; i++)
        {
            var brain = state.Brains[i];
            writer.Write(brain.Path ?? string.Empty);
            WritePose(writer, new Pose { Position = brain.Position, Rotation = brain.Rotation, Velocity = brain.Velocity });
        }
    });

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
                    var pose = ReadPose(reader);
                    state.Props.Add(new PropState { Path = path, Position = pose.Position, Rotation = pose.Rotation, Velocity = pose.Velocity });
                }
                var brainCount = reader.ReadInt32();
                if (brainCount < 0 || brainCount > 128) return false;
                for (var i = 0; i < brainCount; i++)
                {
                    var path = reader.ReadString();
                    var pose = ReadPose(reader);
                    state.Brains.Add(new PropState { Path = path, Position = pose.Position, Rotation = pose.Rotation, Velocity = pose.Velocity });
                }
                return true;
            }
        }
        catch { return false; }
    }

    internal static byte[] EncodeProgress(int tick, ProgressState state) => Encode(MessageType.ProgressSnapshot, tick, writer =>
    {
        writer.Write(state.Area);
        writer.Write(Math.Min(state.Switches.Count, 512));
        for (var i = 0; i < state.Switches.Count && i < 512; i++)
        {
            writer.Write(state.Switches[i].Path ?? string.Empty);
            writer.Write(state.Switches[i].State);
        }
        writer.Write(Math.Min(state.Saves.Count, 1024));
        for (var i = 0; i < state.Saves.Count && i < 1024; i++)
        {
            writer.Write(state.Saves[i].Key ?? string.Empty);
            writer.Write(state.Saves[i].BoolValue);
            writer.Write(state.Saves[i].IntValue);
        }
        writer.Write(Math.Min(state.Goals.Count, 256));
        for (var i = 0; i < state.Goals.Count && i < 256; i++)
        {
            writer.Write(state.Goals[i].Name ?? string.Empty);
            writer.Write(state.Goals[i].Awarded);
            writer.Write(state.Goals[i].Hidden);
        }
    });

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
