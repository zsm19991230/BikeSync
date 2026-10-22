using System.Buffers.Binary;
using BikeSync.Core.Models;
using BikeSync.Core.Providers;
using BikeSync.Core.Services;

namespace BikeSync.Tests;

public sealed class FitPreparerTests
{
    [Fact]
    public void Prepare_rewrites_session_end_and_power_metrics()
    {
        var raw = Fixture(Enumerable.Repeat(200UL, 60).ToArray(), 60);

        var prepared = FitPreparer.Prepare(raw, TimeZoneInfo.Utc, 250);

        FitPreparer.Validate(prepared.Content, PlatformId.IGpsport);
        Assert.True(prepared.MetricsCalculated);
        Assert.Contains("NP=200", prepared.Summary);
        Assert.True(prepared.Content.Length > raw.Length);
    }

    [Fact]
    public void ActivityTrackReader_reads_fit_records()
    {
        var raw = Fixture(new ulong[] { 100, 200, 300 }, 3);

        var track = ActivityTrackReader.Read(raw, ActivityFileFormat.Fit, DateTimeOffset.UtcNow);

        Assert.Equal(3, track.Points.Count);
        Assert.Equal(3, track.GpsPointCount);
        Assert.Equal(200d, track.AveragePowerWatts);
        Assert.Equal(300d, track.MaxPowerWatts);
    }

    private static byte[] Fixture(ulong[] powers, ulong duration)
    {
        const ulong begin = 1161259925;
        var payload = new List<byte>();
        payload.AddRange(Message(0, new (byte Id, byte Size, byte Base, ulong Value)[]
        {
            (0, 1, 0, 4), (1, 2, 0x84, 1),
        }));
        for (var i = 0; i < powers.Length; i++)
        {
            payload.AddRange(Message(20, new (byte, byte, byte, ulong)[]
            {
                (253, 4, 0x86, begin + (ulong)i),
                (6, 2, 0x84, 8394),
                (7, 2, 0x84, powers[i]),
                (0, 4, 0x85, 471254495),
                (1, 4, 0x85, 1619872000),
            }));
        }

        payload.AddRange(Message(18, new (byte, byte, byte, ulong)[]
        {
            (253, 4, 0x86, begin),
            (2, 4, 0x86, begin),
            (7, 4, 0x86, duration * 1000),
            (8, 4, 0x86, duration * 1000),
            (9, 4, 0x86, 2698005),
            (22, 2, 0x84, 33),
        }));
        payload.AddRange(Message(34, new (byte, byte, byte, ulong)[]
        {
            (253, 4, 0x86, begin),
            (5, 4, 0x86, 1792329152),
        }));

        var header = new byte[14];
        header[0] = 14;
        header[1] = 0x20;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), (uint)payload.Count);
        ".FIT"u8.CopyTo(header.AsSpan(8, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12, 2), Crc(header.AsSpan(0, 12)));

        var output = new byte[header.Length + payload.Count + 2];
        header.CopyTo(output, 0);
        payload.CopyTo(output, header.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(
            output.AsSpan(output.Length - 2, 2),
            Crc(output.AsSpan(0, output.Length - 2)));
        return output;
    }

    private static byte[] Message(ushort global, (byte Id, byte Size, byte Base, ulong Value)[] fields)
    {
        var result = new List<byte> { 0x40, 0, 0 };
        result.Add((byte)global);
        result.Add((byte)(global >> 8));
        result.Add((byte)fields.Length);
        foreach (var field in fields)
        {
            result.Add(field.Id);
            result.Add(field.Size);
            result.Add(field.Base);
        }

        result.Add(0); // data message header, local message type 0
        foreach (var field in fields)
        {
            var buffer = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, field.Value);
            result.AddRange(buffer[..field.Size]);
        }

        return result.ToArray();
    }

    private static ushort Crc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc >> 1) ^ ((crc & 1) != 0 ? 0xA001 : 0));
            }
        }

        return crc;
    }
}
