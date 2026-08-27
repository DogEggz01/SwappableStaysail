using System;
using System.Collections.Generic;
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
                    out string json) ||
                string.IsNullOrEmpty(json))
            {
                return;
            }

            try
            {
                SailpackSaveFile save = JsonUtility.FromJson<SailpackSaveFile>(json);
                if (save?.sailpacks == null)
                {
                    return;
                }

                foreach (SailpackSaveEntry entry in save.sailpacks)
                {
                    if (entry != null && entry.instanceId > 0 && entry.record != null)
                    {
                        entry.record.Normalize();
                        Records[entry.instanceId] = entry.record.Copy();
                    }
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
                SailpackSaveFile save = new SailpackSaveFile
                {
                    sailpacks = Records
                        .Select(pair => new SailpackSaveEntry
                        {
                            instanceId = pair.Key,
                            record = pair.Value
                        })
                        .ToArray()
                };
                if (GameState.modData == null)
                {
                    GameState.modData = new Dictionary<string, string>();
                }

                GameState.modData[SwappableStaysailPlugin.PluginGuid] =
                    JsonUtility.ToJson(save);
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
                UnityEngine.Object.FindObjectsOfType<SailpackData>();
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
