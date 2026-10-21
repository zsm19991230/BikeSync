using System.Buffers.Binary;
using System.Text;

namespace BikeSync.Core.Providers;

public sealed record FitTrackPoint(
    DateTimeOffset Timestamp,
    double? Latitude,
    double? Longitude,
    double? DistanceMeters,
    double? SpeedMetersPerSecond,
    double? AltitudeMeters,
    int? HeartRate,
    int? Cadence,
    int? PowerWatts,
    int? TemperatureCelsius);

public sealed record FitWorkout(
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    string Name,
    IReadOnlyList<FitTrackPoint> Points,
    double TotalDistanceMeters,
    double TotalElapsedSeconds);

/// <summary>
/// Writes a conservative FIT activity file for sources that only expose GPX or
/// stream arrays (currently Xingzhe). The output is intentionally minimal but
/// contains file_id, record, session and activity messages plus a valid CRC.
/// </summary>
public static class FitWriter
{
    private const byte ProtocolVersion = 0x10;
    private const ushort ProfileVersion = 100;
    private static readonly DateTimeOffset FitEpoch =
        new(1989, 12, 31, 0, 0, 0, TimeSpan.Zero);

    public static byte[] Write(FitWorkout workout, byte sport = 2)
    {
        var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            WriteFileId(writer, workout);
            WriteRecords(writer, workout);
            WriteSession(writer, workout, sport);
            WriteActivity(writer, workout);
        }

        var data = payload.ToArray();
        var file = new byte[14 + data.Length + 2];
        file[0] = 14;
        file[1] = ProtocolVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2, 2), ProfileVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4, 4), (uint)data.Length);
        Encoding.ASCII.GetBytes(".FIT").CopyTo(file, 8);
        var headerCrc = Crc16(file.AsSpan(0, 12));
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(12, 2), headerCrc);
        data.CopyTo(file, 14);
        var fileCrc = Crc16(file.AsSpan(0, file.Length - 2));
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(file.Length - 2, 2), fileCrc);
        return file;
    }

    private static void WriteFileId(BinaryWriter writer, FitWorkout workout)
    {
        WriteDefinition(writer, 0, 0, new[]
        {
            Field(0, 1, BaseType.Enum),
            Field(1, 2, BaseType.UInt16),
            Field(2, 2, BaseType.UInt16),
            Field(3, 4, BaseType.UInt32),
            Field(4, 4, BaseType.UInt32),
        });
        WriteDataHeader(writer, 0);
        writer.Write((byte)4); // activity
        writer.Write((ushort)1); // Garmin
        writer.Write((ushort)1);
        writer.Write(1u);
        writer.Write(ToFitTime(workout.StartTime));
    }

    private static void WriteRecords(BinaryWriter writer, FitWorkout workout)
    {
        if (workout.Points.Count == 0)
        {
            return;
        }

        WriteDefinition(writer, 1, 20, new[]
        {
            Field(253, 4, BaseType.UInt32),
            Field(0, 4, BaseType.SInt32),
            Field(1, 4, BaseType.SInt32),
            Field(5, 4, BaseType.UInt32),
            Field(2, 2, BaseType.UInt16),
            Field(6, 2, BaseType.UInt16),
            Field(3, 1, BaseType.UInt8),
            Field(4, 1, BaseType.UInt8),
            Field(7, 2, BaseType.UInt16),
        });

        foreach (var point in workout.Points)
        {
            WriteDataHeader(writer, 1);
            writer.Write(ToFitTime(point.Timestamp));
            writer.Write(point.Latitude is { } lat ? Semicircles(lat) : int.MaxValue);
            writer.Write(point.Longitude is { } lon ? Semicircles(lon) : int.MaxValue);
            writer.Write(point.DistanceMeters is { } distance
                ? (uint)Math.Clamp((long)Math.Round(distance * 100), 0, uint.MaxValue - 1)
                : uint.MaxValue);
            writer.Write(point.AltitudeMeters is { } altitude
                ? EncodeUInt16(altitude, 5, 500)
                : ushort.MaxValue);
            writer.Write(point.SpeedMetersPerSecond is { } speed
                ? EncodeUInt16(speed, 1000, 0)
                : ushort.MaxValue);
            writer.Write(point.HeartRate is { } hr ? (byte)Math.Clamp(hr, 0, 254) : byte.MaxValue);
            writer.Write(point.Cadence is { } cadence ? (byte)Math.Clamp(cadence, 0, 254) : byte.MaxValue);
            writer.Write(point.PowerWatts is { } power
                ? (ushort)Math.Clamp(power, 0, ushort.MaxValue - 1)
                : ushort.MaxValue);
        }
    }

    private static void WriteSession(BinaryWriter writer, FitWorkout workout, byte sport)
    {
        WriteDefinition(writer, 2, 18, new[]
        {
            Field(253, 4, BaseType.UInt32),
            Field(2, 4, BaseType.UInt32),
            Field(7, 4, BaseType.UInt32),
            Field(8, 4, BaseType.UInt32),
            Field(9, 4, BaseType.UInt32),
            Field(5, 1, BaseType.Enum),
            Field(6, 1, BaseType.Enum),
            Field(14, 2, BaseType.UInt16),
            Field(15, 2, BaseType.UInt16),
            Field(11, 2, BaseType.UInt16),
            Field(12, 2, BaseType.UInt16),
            Field(16, 1, BaseType.UInt8),
            Field(17, 1, BaseType.UInt8),
            Field(18, 1, BaseType.UInt8),
            Field(19, 1, BaseType.UInt8),
            Field(20, 2, BaseType.UInt16),
            Field(21, 2, BaseType.UInt16),
            Field(22, 2, BaseType.UInt16),
        });

        var duration = Math.Max(0, workout.TotalElapsedSeconds);
        var points = workout.Points;
        var heartRates = points.Where(p => p.HeartRate is > 0).Select(p => p.HeartRate!.Value).ToArray();
        var cadences = points.Where(p => p.Cadence is > 0).Select(p => p.Cadence!.Value).ToArray();
        var powers = points.Where(p => p.PowerWatts is > 0).Select(p => p.PowerWatts!.Value).ToArray();
        var ascents = points
            .Select((p, i) => i == 0 || p.AltitudeMeters is null || points[i - 1].AltitudeMeters is null
                ? 0
                : Math.Max(0, p.AltitudeMeters!.Value - points[i - 1].AltitudeMeters!.Value))
            .Sum();
        var descents = points
            .Select((p, i) => i == 0 || p.AltitudeMeters is null || points[i - 1].AltitudeMeters is null
                ? 0
                : Math.Max(0, points[i - 1].AltitudeMeters!.Value - p.AltitudeMeters!.Value))
            .Sum();
        var avgSpeed = duration > 0 ? workout.TotalDistanceMeters / duration : 0;
        var maxSpeed = points
            .Where(p => p.SpeedMetersPerSecond is not null)
            .Select(p => p.SpeedMetersPerSecond!.Value)
            .DefaultIfEmpty(0)
            .Max();

        WriteDataHeader(writer, 2);
        writer.Write(ToFitTime(workout.EndTime));
        writer.Write(ToFitTime(workout.StartTime));
        writer.Write(EncodeUInt32(duration, 1000));
        writer.Write(EncodeUInt32(duration, 1000));
        writer.Write(EncodeUInt32(workout.TotalDistanceMeters, 100));
        writer.Write(sport);
        writer.Write((byte)0);
        writer.Write(EncodeUInt16(avgSpeed, 1000, 0));
        writer.Write(EncodeUInt16(maxSpeed, 1000, 0));
        writer.Write((ushort)Math.Clamp(ascents, 0, ushort.MaxValue - 1));
        writer.Write((ushort)Math.Clamp(descents, 0, ushort.MaxValue - 1));
        writer.Write(heartRates.Length == 0 ? byte.MaxValue : (byte)heartRates.Average());
        writer.Write(heartRates.Length == 0 ? byte.MaxValue : (byte)heartRates.Max());
        writer.Write(cadences.Length == 0 ? byte.MaxValue : (byte)cadences.Average());
        writer.Write(cadences.Length == 0 ? byte.MaxValue : (byte)cadences.Max());
        writer.Write(powers.Length == 0 ? ushort.MaxValue : (ushort)powers.Average());
        writer.Write(powers.Length == 0 ? ushort.MaxValue : (ushort)powers.Max());
        writer.Write(ushort.MaxValue); // calories unknown
    }

    private static void WriteActivity(BinaryWriter writer, FitWorkout workout)
    {
        WriteDefinition(writer, 3, 34, new[]
        {
            Field(253, 4, BaseType.UInt32),
            Field(0, 4, BaseType.UInt32),
            Field(1, 2, BaseType.UInt16),
            Field(2, 1, BaseType.Enum),
            Field(3, 1, BaseType.Enum),
            Field(4, 1, BaseType.Enum),
        });
        WriteDataHeader(writer, 3);
        writer.Write(ToFitTime(workout.EndTime));
        writer.Write(EncodeUInt32(workout.TotalElapsedSeconds, 1000));
        writer.Write((ushort)1);
        writer.Write((byte)0);
        writer.Write((byte)26);
        writer.Write((byte)1);
    }

    private static void WriteDefinition(
        BinaryWriter writer,
        byte localType,
        ushort globalMessage,
        IReadOnlyList<FitField> fields)
    {
        writer.Write((byte)(0x40 | localType));
        writer.Write((byte)0);
        writer.Write((byte)0); // little endian
        writer.Write(globalMessage);
        writer.Write((byte)fields.Count);
        foreach (var field in fields)
        {
            writer.Write(field.Number);
            writer.Write(field.Size);
            writer.Write(field.BaseType);
        }
    }

    private static void WriteDataHeader(BinaryWriter writer, byte localType) =>
        writer.Write(localType);

    private static FitField Field(byte number, byte size, byte baseType) =>
        new(number, size, baseType);

    private static int Semicircles(double degrees) =>
        (int)Math.Round(degrees * (int.MaxValue / 180.0));

    private static ushort EncodeUInt16(double value, double scale, double offset)
    {
        var encoded = Math.Round((value + offset) * scale);
        if (encoded >= ushort.MaxValue)
        {
            return ushort.MaxValue - 1;
        }

        return (ushort)Math.Max(0, encoded);
    }

    private static uint EncodeUInt32(double value, double scale)
    {
        var encoded = Math.Round(value * scale);
        if (encoded >= uint.MaxValue)
        {
            return uint.MaxValue - 1;
        }

        return (uint)Math.Max(0, encoded);
    }

    private static uint ToFitTime(DateTimeOffset value) =>
        (uint)Math.Max(0, (value.ToUniversalTime() - FitEpoch).TotalSeconds);

    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        var crc = (ushort)0;
        foreach (var value in data)
        {
            crc = (ushort)((crc >> 8) ^ CrcTable[(crc ^ value) & 0xFF]);
        }

        return crc;
    }

    private static readonly ushort[] CrcTable = BuildCrcTable();

    private static ushort[] BuildCrcTable()
    {
        var table = new ushort[256];
        for (var index = 0; index < table.Length; index++)
        {
            var crc = (ushort)index;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? (ushort)((crc >> 1) ^ 0xA001)
                    : (ushort)(crc >> 1);
            }

            table[index] = crc;
        }

        return table;
    }

    private readonly record struct FitField(byte Number, byte Size, byte BaseType);

    private static class BaseType
    {
        public const byte Enum = 0x00;
        public const byte UInt8 = 0x02;
        public const byte UInt16 = 0x84;
        public const byte SInt32 = 0x85;
        public const byte UInt32 = 0x86;
    }
}
