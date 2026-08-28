using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace SwappableStaysail
{
    internal static class SailpackPersistence
    {
        private static readonly Dictionary<int, SailpackRecord> Records =
            new Dictionary<int, SailpackRecord>();

        internal static bool TryGet(int instanceId, out SailpackRecord record)
        {
            return Records.TryGetValue(instanceId, out record);
        }

        internal static void Register(int instanceId, SailpackRecord record)
        {
            if (instanceId > 0 && record != null)
            {
                record.Normalize();
                Records[instanceId] = record.Copy();
            }
        }

        internal static void Remove(int instanceId)
        {
            if (instanceId > 0)
            {
                Records.Remove(instanceId);
            }
        }

        internal static void Load()
        {
            Records.Clear();
            if (GameState.modData == null ||
                !GameState.modData.TryGetValue(
                    SwappableStaysailPlugin.PluginGuid,
                    out string payload) ||
                string.IsNullOrEmpty(payload))
            {
                return;
            }

            try
            {
                Dictionary<int, SailpackRecord> loaded =
                    SailpackPersistenceCodec.Decode(payload);
                foreach (KeyValuePair<int, SailpackRecord> pair in loaded)
                {
                    Records[pair.Key] = pair.Value.Copy();
                }

                SwappableStaysailPlugin.Log?.LogInfo(
                    $"Loaded {Records.Count} sail package record(s).");
            }
            catch (Exception exception)
            {
                SwappableStaysailPlugin.Log?.LogError(
                    $"Could not load sail package data: {exception}");
            }
        }

        internal static void Dispose()
        {
            Records.Clear();
        }

        internal static void Save()
        {
            try
            {
                ReconcileLiveSailpacks();
                Dictionary<int, SailpackRecord> snapshot = Records.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Copy());
                string payload = SailpackPersistenceCodec.Encode(snapshot);
                Dictionary<int, SailpackRecord> verified =
                    SailpackPersistenceCodec.Decode(payload);
                if (verified.Count != snapshot.Count ||
                    !verified.Keys.OrderBy(id => id)
                        .SequenceEqual(snapshot.Keys.OrderBy(id => id)))
                {
                    throw new InvalidDataException(
                        "Sail package metadata failed its save-time verification.");
                }

                if (GameState.modData == null)
                {
                    GameState.modData = new Dictionary<string, string>();
                }

                GameState.modData[SwappableStaysailPlugin.PluginGuid] =
                    payload;
                SwappableStaysailPlugin.Log?.LogInfo(
                    $"Saved {snapshot.Count} sail package record(s).");
            }
            catch (Exception exception)
            {
                SwappableStaysailPlugin.Log?.LogError(
                    $"Could not save sail package data: {exception}");
            }
        }

        private static void ReconcileLiveSailpacks()
        {
            SailpackData[] live =
                Resources.FindObjectsOfTypeAll<SailpackData>();
            HashSet<int> liveIds = new HashSet<int>();
            foreach (SailpackData data in live)
            {
                SaveablePrefab saveable = data.GetComponent<SaveablePrefab>();
                if (saveable == null || saveable.instanceId <= 0 || !data.HasRecord)
                {
                    continue;
                }

                liveIds.Add(saveable.instanceId);
                Records[saveable.instanceId] = data.Record.Copy();
            }

            // Keep metadata for items cached inside unloaded boats. Only remove an
            // entry when vanilla's live saveable list proves the ID is gone.
            SaveLoadManager manager = SaveLoadManager.instance;
            if (manager == null)
            {
                return;
            }

            List<SaveablePrefab> currentPrefabs = manager.GetCurrentPrefabs();
            if (currentPrefabs != null)
            {
                foreach (SaveablePrefab saveable in currentPrefabs)
                {
                    if (saveable != null && saveable.instanceId > 0)
                    {
                        liveIds.Add(saveable.instanceId);
                    }
                }
            }

            foreach (SaveableObject saveableObject in
                     Resources.FindObjectsOfTypeAll<SaveableObject>())
            {
                if (saveableObject?.localItems == null)
                {
                    continue;
                }
                List<SavePrefabData> cachedItems =
                    saveableObject.localItems.GetCachedItems();
                if (cachedItems == null)
                {
                    continue;
                }
                foreach (SavePrefabData cached in cachedItems)
                {
                    if (cached != null && cached.instanceId > 0)
                    {
                        liveIds.Add(cached.instanceId);
                    }
                }
            }

            List<int> stale = Records.Keys
                .Where(id => !liveIds.Contains(id))
                .ToList();
            foreach (int id in stale)
            {
                Records.Remove(id);
            }
        }

    }
}
