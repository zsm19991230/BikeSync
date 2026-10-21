using System.Buffers.Binary;
using System.Globalization;
using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;

namespace BikeSync.Core.Providers;

internal sealed record FitPreparation(byte[] Content, string Summary, bool MetricsCalculated);

/// <summary>
/// Lossless-enough FIT patcher for uploads to iGPSPORT. MyWhoosh writes the session end
/// timestamp (and sometimes no NP/IF/TSS) into the exported FIT. iGPSPORT reads the
/// session timestamp as the activity date, so we correct it and enrich power metrics.
/// </summary>
internal static class FitPreparer
{
    private const long FitEpoch = 631065600;
    private const ushort SessionGlobal = 18;
    private const ushort ActivityGlobal = 34;
    private const ushort RecordGlobal = 20;

    public static void Validate(byte[] raw, PlatformId platform)
    {
        try
        {
            ValidateOrThrow(raw);
        }
        catch (InvalidDataException ex)
        {
            throw new PlatformApiException(platform, 422, "活动文件不是有效的 FIT 文件：" + ex.Message);
        }
    }

    public static FitPreparation Prepare(byte[] raw, TimeZoneInfo timeZone, double ftp)
    {
        ValidateOrThrow(raw);
        var frames = Parse(raw);
        var messages = frames.OfType<DataFrame>().Select(f => f.Message).ToArray();
        var sessions = messages.Where(m => m.Definition.Global == SessionGlobal).ToArray();
        var activities = messages.Where(m => m.Definition.Global == ActivityGlobal).ToArray();
        var records = messages.Where(m => m.Definition.Global == RecordGlobal).ToArray();
        if (sessions.Length != 1 || activities.Length != 1 || records.Length == 0)
        {
            throw new InvalidDataException("缺少唯一的 session / activity / record 数据。");
        }

        var session = sessions[0];
        var begin = session.Number(2) ??
                    throw new InvalidDataException("session 缺少开始时间。");
        var timer = (session.Number(8) ?? throw new InvalidDataException("session 缺少计时时长。")) / 1000d;
        var elapsed = (session.Number(7) ?? (ulong)Math.Round(timer * 1000)) / 1000d;

        double? maxSpeed = null;
        foreach (var record in records)
        {
            var speed = record.Number(73) ?? record.Number(6);
            if (speed is { } value)
            {
                maxSpeed = Math.Max(maxSpeed ?? 0, value);
            }
        }

        if (maxSpeed is null)
        {
            throw new InvalidDataException("record 中没有速度采样。");
        }

        var metrics = TryCalculateMetrics(records, timer, elapsed, ftp);
        var end = begin + (ulong)Math.Round(elapsed);
        var endInstant = DateTimeOffset.FromUnixTimeSeconds(FitEpoch + (long)end);
        var offset = timeZone.GetUtcOffset(endInstant);
        var local = (ulong)Math.Max(0, end + offset.TotalSeconds);

        var sessionReplacements = new Dictionary<byte, (byte Size, byte Base, ulong Value)>
        {
            [253] = (4, 0x86, end),
            [15] = (2, 0x84, Math.Min(65535, (ulong)Math.Round(maxSpeed.Value))),
            [125] = (4, 0x86, (ulong)Math.Round(maxSpeed.Value)),
        };
        if (metrics is { } m)
        {
            sessionReplacements[34] = (2, 0x84, m.NormalizedPower);
            sessionReplacements[35] = (2, 0x84, m.TssX10);
            sessionReplacements[36] = (2, 0x84, m.IfX1000);
            sessionReplacements[45] = (2, 0x84, m.ThresholdPower);
        }

        var activityReplacements = new Dictionary<byte, (byte Size, byte Base, ulong Value)>
        {
            [253] = (4, 0x86, end),
            [5] = (4, 0x86, local),
        };

        var payload = new List<byte>(raw.Length + 64);
        foreach (var frame in frames)
        {
            switch (frame)
            {
                case DefinitionFrame definition:
                    payload.AddRange(definition.Raw);
                    break;
                case DataFrame data when data.Message.Definition.Global == SessionGlobal:
                    payload.AddRange(data.Message.Rewrite(sessionReplacements));
                    break;
                case DataFrame data when data.Message.Definition.Global == ActivityGlobal:
                    payload.AddRange(data.Message.Rewrite(activityReplacements));
                    break;
                case DataFrame data:
                    payload.AddRange(data.Message.RawData);
                    break;
            }
        }

        var headerLength = raw[0];
        var output = new byte[headerLength + payload.Count + 2];
        raw.AsSpan(0, headerLength).CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4, 4), (uint)payload.Count);
        if (headerLength == 14)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(12, 2), Crc16(output.AsSpan(0, 12)));
        }

        payload.CopyTo(output, headerLength);
        BinaryPrimitives.WriteUInt16LittleEndian(
            output.AsSpan(output.Length - 2, 2),
            Crc16(output.AsSpan(0, output.Length - 2)));

        ValidateOrThrow(output);
        var checkedMessages = Parse(output).OfType<DataFrame>().Select(f => f.Message).ToArray();
        var afterRecords = checkedMessages.Where(m => m.Definition.Global == RecordGlobal).ToArray();
        if (afterRecords.Length != records.Length ||
            !afterRecords.Zip(records).All(pair => pair.First.RawData.AsSpan().SequenceEqual(pair.Second.RawData)))
        {
            throw new InvalidDataException("FIT 采样字节发生变化，已拒绝上传。");
        }

        var summary = metrics is { } result
            ? $"已修正时间，NP={result.NormalizedPower} W，IF={result.IntensityFactor:0.000}，TSS={result.TrainingStressScore:0.0}"
            : "已修正时间（功率采样不连续，未补充 NP/IF/TSS）";
        return new FitPreparation(output, summary, metrics is not null);
    }

    private static PowerMetrics? TryCalculateMetrics(
        IReadOnlyList<FitMessage> records,
        double timer,
        double elapsed,
        double ftp)
    {
        if (!double.IsFinite(ftp) || ftp <= 0 || ftp >= 65535)
        {
            return null;
        }

        if (Math.Abs(timer - records.Count) > 0.01 || Math.Abs(elapsed - timer) > 0.01)
        {
            return null;
        }

        ulong? previous = null;
        var window = new Queue<double>();
        var rolling = 0d;
        var fourth = 0d;
        var count = 0;
        foreach (var record in records)
        {
            var timestamp = record.Number(253);
            var power = record.Number(7);
            if (timestamp is null || power is null)
            {
                return null;
            }

            if (previous is { } last && timestamp != last + 1)
            {
                return null;
            }

            previous = timestamp;
            var watts = power.Value;
            window.Enqueue(watts);
            rolling += watts;
            if (window.Count > 30)
            {
                rolling -= window.Dequeue();
            }

            if (window.Count == 30)
            {
                fourth += Math.Pow(rolling / 30d, 4);
                count++;
            }
        }

        if (count == 0)
        {
            return null;
        }

        var np = Math.Pow(fourth / count, 0.25);
        var intensity = np / ftp;
        var tss = timer / 3600d * intensity * intensity * 100d;
        if (!double.IsFinite(np) || np >= 65534.5 || intensity >= 65.5345 || tss >= 6553.45)
        {
            return null;
        }

        return new PowerMetrics(
            (ulong)Math.Round(np),
            (ulong)Math.Round(intensity * 1000),
            (ulong)Math.Round(tss * 10),
            (ulong)Math.Round(ftp),
            intensity,
            tss);
    }

    private sealed record PowerMetrics(
        ulong NormalizedPower,
        ulong IfX1000,
        ulong TssX10,
        ulong ThresholdPower,
        double IntensityFactor,
        double TrainingStressScore);

    private static void ValidateOrThrow(byte[] raw)
    {
        if (raw.Length < 14 || raw[8] != (byte)'.' || raw[9] != (byte)'F' ||
            raw[10] != (byte)'I' || raw[11] != (byte)'T')
        {
            throw new InvalidDataException("缺少 .FIT 签名。");
        }

        var header = raw[0];
        if (header is not (12 or 14))
        {
            throw new InvalidDataException("FIT 头长度无效。");
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4, 4));
        if ((long)header + size + 2 != raw.Length)
        {
            throw new InvalidDataException("FIT 文件长度不一致。");
        }

        if (header == 14 && (raw[12] != 0 || raw[13] != 0) && Crc16(raw.AsSpan(0, 14)) != 0)
        {
            throw new InvalidDataException("FIT 头 CRC 无效。");
        }

        if (Crc16(raw) != 0)
        {
            throw new InvalidDataException("FIT 文件 CRC 无效。");
        }
    }

    private static List<FitFrame> Parse(byte[] raw)
    {
        var definitions = new FitDefinition?[16];
        var frames = new List<FitFrame>();
        var pos = (int)raw[0];
        var end = raw.Length - 2;
        while (pos < end)
        {
            var start = pos;
            var header = raw[pos++];
            if ((header & 0x80) != 0)
            {
                throw new InvalidDataException("暂不支持压缩时间戳 FIT。");
            }

            var local = (byte)(header & 0x0F);
            if ((header & 0x40) != 0)
            {
                if (pos + 5 > end)
                {
                    throw new InvalidDataException("FIT 定义截断。");
                }

                var big = raw[pos + 1] != 0;
                var global = big
                    ? BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(pos + 2, 2))
                    : BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(pos + 2, 2));
                var fieldCount = raw[pos + 4];
                pos += 5;
                if (pos + fieldCount * 3 > end)
                {
                    throw new InvalidDataException("FIT 字段定义截断。");
                }

                var fields = new List<FitField>(fieldCount);
                for (var i = 0; i < fieldCount; i++)
                {
                    fields.Add(new FitField(raw[pos], raw[pos + 1], raw[pos + 2]));
                    pos += 3;
                }

                var developers = new List<byte[]>();
                if ((header & 0x20) != 0)
                {
                    if (pos >= end)
                    {
                        throw new InvalidDataException("FIT developer 定义截断。");
                    }

                    var devCount = raw[pos++];
                    if (pos + devCount * 3 > end)
                    {
                        throw new InvalidDataException("FIT developer 字段截断。");
                    }

                    for (var i = 0; i < devCount; i++)
                    {
                        developers.Add(raw.AsSpan(pos, 3).ToArray());
                        pos += 3;
                    }
                }

                var definition = new FitDefinition(local, global, big, fields, developers);
                definitions[local] = definition;
                frames.Add(new DefinitionFrame(raw.AsSpan(start, pos - start).ToArray()));
            }
            else
            {
                var definition = definitions[local] ??
                                 throw new InvalidDataException("FIT 缺少本地消息定义。");
                var length = definition.Fields.Sum(f => (int)f.Size) +
                             definition.Developers.Sum(d => (int)d[1]);
                if (pos + length > end)
                {
                    throw new InvalidDataException("FIT 数据截断。");
                }

                frames.Add(new DataFrame(new FitMessage(
                    definition,
                    raw.AsSpan(start, pos + length - start).ToArray())));
                pos += length;
            }
        }

        return frames;
    }

    private static ushort Crc16(ReadOnlySpan<byte> data)
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

    private abstract record FitFrame;
    private sealed record DefinitionFrame(byte[] Raw) : FitFrame;
    private sealed record DataFrame(FitMessage Message) : FitFrame;

    private sealed record FitField(byte Id, byte Size, byte Base);

    private sealed class FitDefinition
    {
        public FitDefinition(
            byte local,
            ushort global,
            bool bigEndian,
            IReadOnlyList<FitField> fields,
            IReadOnlyList<byte[]> developers)
        {
            Local = local;
            Global = global;
            BigEndian = bigEndian;
            Fields = fields;
            Developers = developers;
        }

        public byte Local { get; }
        public ushort Global { get; }
        public bool BigEndian { get; }
        public IReadOnlyList<FitField> Fields { get; }
        public IReadOnlyList<byte[]> Developers { get; }
    }

    private sealed class FitMessage
    {
        public FitMessage(FitDefinition definition, byte[] rawData)
        {
            Definition = definition;
            RawData = rawData;
        }

        public FitDefinition Definition { get; }
        public byte[] RawData { get; }

        public ulong? Number(byte fieldId)
        {
            var offset = 1;
            foreach (var field in Definition.Fields)
            {
                var span = RawData.AsSpan(offset, field.Size);
                if (field.Id == fieldId)
                {
                    ulong value = field.Size switch
                    {
                        1 => span[0],
                        2 => Definition.BigEndian
                            ? BinaryPrimitives.ReadUInt16BigEndian(span)
                            : BinaryPrimitives.ReadUInt16LittleEndian(span),
                        4 => Definition.BigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(span)
                            : BinaryPrimitives.ReadUInt32LittleEndian(span),
                        _ => 0,
                    };
                    if (field.Size is not (1 or 2 or 4))
                    {
                        return null;
                    }

                    var invalid = field.Size == 8
                        ? ulong.MaxValue
                        : (1UL << (field.Size * 8)) - 1;
                    return value == invalid ? null : value;
                }

                offset += field.Size;
            }

            return null;
        }

        public byte[] Rewrite(IReadOnlyDictionary<byte, (byte Size, byte Base, ulong Value)> replacements)
        {
            var remaining = new Dictionary<byte, (byte Size, byte Base, ulong Value)>(replacements);
            var fields = new List<byte>();
            var payload = new List<byte> { Definition.Local };
            var offset = 1;
            foreach (var field in Definition.Fields)
            {
                fields.Add(field.Id);
                fields.Add(field.Size);
                fields.Add(field.Base);
                if (remaining.Remove(field.Id, out var replacement))
                {
                    if (replacement.Size != field.Size || replacement.Base != field.Base)
                    {
                        throw new InvalidDataException($"FIT 汇总字段 {field.Id} 类型不匹配。");
                    }

                    AppendNumber(payload, replacement.Value, field.Size, Definition.BigEndian);
                }
                else
                {
                    payload.AddRange(RawData.AsSpan(offset, field.Size).ToArray());
                }

                offset += field.Size;
            }

            foreach (var (id, replacement) in remaining)
            {
                fields.Add(id);
                fields.Add(replacement.Size);
                fields.Add(replacement.Base);
                AppendNumber(payload, replacement.Value, replacement.Size, Definition.BigEndian);
            }

            payload.AddRange(RawData.AsSpan(offset).ToArray());
            var hasDevelopers = Definition.Developers.Count > 0;
            var output = new List<byte>
            {
                (byte)((hasDevelopers ? 0x60 : 0x40) | Definition.Local),
                0,
                (byte)(Definition.BigEndian ? 1 : 0),
            };
            if (Definition.BigEndian)
            {
                output.Add((byte)(Definition.Global >> 8));
                output.Add((byte)Definition.Global);
            }
            else
            {
                output.Add((byte)Definition.Global);
                output.Add((byte)(Definition.Global >> 8));
            }

            output.Add((byte)(fields.Count / 3));
            output.AddRange(fields);
            if (hasDevelopers)
            {
                output.Add((byte)Definition.Developers.Count);
                foreach (var developer in Definition.Developers)
                {
                    output.AddRange(developer);
                }
            }

            output.AddRange(payload);
            return output.ToArray();
        }

        private static void AppendNumber(List<byte> output, ulong value, byte size, bool bigEndian)
        {
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
            if (bigEndian)
            {
                output.AddRange(buffer[(8 - size)..].ToArray());
            }
            else
            {
                BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
                output.AddRange(buffer[..size].ToArray());
            }
        }
    }
}
