using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SwappableStaysail
{
    internal static class SailpackPersistenceCodec
    {
        private const string Prefix = "SSP3:";
        private const int FormatVersion = 3;
        private const int MaximumRecordCount = 10000;
        private const int MaximumEncodedLength = 16 * 1024 * 1024;

        private static bool IsCurrentFormat(string payload)
        {
            return payload != null &&
                   payload.StartsWith(Prefix, StringComparison.Ordinal);
        }

        internal static string Encode(
            IEnumerable<KeyValuePair<int, SailpackRecord>> records)
        {
            List<KeyValuePair<int, SailpackRecord>> validRecords =
                (records ?? Enumerable.Empty<KeyValuePair<int, SailpackRecord>>())
                .Where(pair => pair.Key > 0 && pair.Value != null)
                .OrderBy(pair => pair.Key)
                .ToList();
            if (validRecords.Count > MaximumRecordCount)
            {
                throw new InvalidDataException(
                    $"Sail package record count {validRecords.Count} exceeds " +
                    $"the supported maximum of {MaximumRecordCount}.");
            }

            using (MemoryStream stream = new MemoryStream())
            {
                using (BinaryWriter writer = new BinaryWriter(
                           stream,
                           Encoding.UTF8,
                           true))
                {
                    writer.Write(FormatVersion);
                    writer.Write(validRecords.Count);
                    foreach (KeyValuePair<int, SailpackRecord> pair in validRecords)
                    {
                        WriteRecord(writer, pair.Key, pair.Value);
                    }
                }

                return Prefix + Convert.ToBase64String(stream.ToArray());
            }
        }

        internal static Dictionary<int, SailpackRecord> Decode(string payload)
        {
            if (!IsCurrentFormat(payload))
            {
                throw new InvalidDataException(
                    "Sail package metadata does not use the current format.");
            }
            if (payload.Length > MaximumEncodedLength)
            {
                throw new InvalidDataException(
                    "Sail package metadata exceeds the supported size.");
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(payload.Substring(Prefix.Length));
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException(
                    "Sail package metadata contains invalid base64 data.",
                    exception);
            }

            using (MemoryStream stream = new MemoryStream(bytes, false))
            using (BinaryReader reader = new BinaryReader(
                       stream,
                       Encoding.UTF8,
                       true))
            {
                int version = reader.ReadInt32();
                if (version != FormatVersion)
                {
                    throw new InvalidDataException(
                        $"Unsupported sail package metadata version {version}.");
                }

                int count = reader.ReadInt32();
                if (count < 0 || count > MaximumRecordCount)
                {
                    throw new InvalidDataException(
                        $"Invalid sail package record count {count}.");
                }

                Dictionary<int, SailpackRecord> decoded =
                    new Dictionary<int, SailpackRecord>(count);
                for (int i = 0; i < count; i++)
                {
                    int instanceId = reader.ReadInt32();
                    if (instanceId <= 0 || decoded.ContainsKey(instanceId))
                    {
                        throw new InvalidDataException(
                            $"Invalid or duplicate sail package ID {instanceId}.");
                    }

                    SailpackRecord record = ReadRecord(reader);
                    record.Normalize();
                    decoded.Add(instanceId, record);
                }

                if (stream.Position != stream.Length)
                {
                    throw new InvalidDataException(
                        "Sail package metadata contains unexpected trailing data.");
                }
                return decoded;
            }
        }

        private static void WriteRecord(
            BinaryWriter writer,
            int instanceId,
            SailpackRecord record)
        {
            record.Normalize();
            writer.Write(instanceId);
            writer.Write(record.sailPrefabIndex);
            writer.Write(record.sailColor);
            writer.Write(record.scaleY);
            writer.Write(record.scaleZ);
            writer.Write(record.packageMass);
            writer.Write(record.installHeight);
            writer.Write(record.minAngle);
            writer.Write(record.maxAngle);
            writer.Write(record.targetBoatSceneIndex);
            writer.Write(record.targetMastOrderIndex);
            writer.Write(record.targetStayName ?? string.Empty);
            writer.Write(record.sailName ?? string.Empty);
        }

        private static SailpackRecord ReadRecord(BinaryReader reader)
        {
            return new SailpackRecord
            {
                sailPrefabIndex = reader.ReadInt32(),
                sailColor = reader.ReadInt32(),
                scaleY = reader.ReadSingle(),
                scaleZ = reader.ReadSingle(),
                packageMass = reader.ReadSingle(),
                installHeight = reader.ReadSingle(),
                minAngle = reader.ReadSingle(),
                maxAngle = reader.ReadSingle(),
                targetBoatSceneIndex = reader.ReadInt32(),
                targetMastOrderIndex = reader.ReadInt32(),
                targetStayName = reader.ReadString(),
                sailName = reader.ReadString()
            };
        }
    }
}
