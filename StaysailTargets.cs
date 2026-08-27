using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SwappableStaysail
{
    internal static class StaysailTargets
    {
        internal static bool IsEligible(Mast mast)
        {
            return mast != null && mast.onlyStaysails && !IsBermudaMast(mast);
        }

        internal static bool IsBermudaMast(Mast mast)
        {
            if (mast == null)
            {
                return false;
            }

            BoatPartOption option = mast.GetComponent<BoatPartOption>();
            string identity = option != null && !string.IsNullOrEmpty(option.optionName)
                ? option.optionName
                : mast.gameObject.name;
            identity = identity.ToLowerInvariant().Replace('_', ' ');
            return identity.Contains("bermuda") &&
                   identity.Contains("mast") &&
                   !identity.Contains("stay");
        }

        internal static int GetBoatSceneIndex(Mast mast)
        {
            if (mast == null)
            {
                return -1;
            }

            SaveableObject saveable = null;
            if (mast.shipRigidbody != null)
            {
                saveable = mast.shipRigidbody.GetComponent<SaveableObject>() ??
                           mast.shipRigidbody.GetComponentInParent<SaveableObject>();
            }
            saveable = saveable ?? mast.GetComponentInParent<SaveableObject>();
            return saveable != null ? saveable.sceneIndex : -1;
        }

        internal static string GetStayName(Mast mast)
        {
            if (mast == null)
            {
                return "Staysail stay";
            }

            BoatPartOption option = mast.GetComponent<BoatPartOption>();
            string name = option != null && !string.IsNullOrEmpty(option.optionName)
                ? option.optionName
                : mast.gameObject.name;
            name = name.Replace('_', ' ').Trim();
            return string.IsNullOrEmpty(name) ? "Staysail stay" : name;
        }

        internal static Mast Resolve(SailpackData data)
        {
            SailpackRecord record = data?.Record;
            if (record == null)
            {
                return null;
            }

            if (Matches(data.RuntimeTarget, record))
            {
                return data.RuntimeTarget;
            }

            Mast[] candidates = Array.Empty<Mast>();
            SaveLoadManager manager = SaveLoadManager.instance;
            if (manager != null)
            {
                SaveableObject[] objects = manager.GetCurrentObjects();
                int boatIndex = record.targetBoatSceneIndex;
                if (objects != null && boatIndex >= 0 &&
                    boatIndex < objects.Length && objects[boatIndex] != null)
                {
                    candidates = objects[boatIndex]
                        .GetComponentsInChildren<Mast>(false)
                        .Where(mast => Matches(mast, record))
                        .ToArray();
                }
            }

            if (candidates.Length == 0)
            {
                candidates = UnityEngine.Object.FindObjectsOfType<Mast>()
                    .Where(mast => Matches(mast, record))
                    .ToArray();
            }

            Mast resolved = candidates.FirstOrDefault(mast =>
                string.Equals(
                    GetStayName(mast),
                    record.targetStayName,
                    StringComparison.OrdinalIgnoreCase));
            if (resolved == null && candidates.Length == 1)
            {
                resolved = candidates[0];
            }
            if (resolved != null)
            {
                data.BindTarget(resolved);
            }
            return resolved;
        }

        private static bool Matches(Mast mast, SailpackRecord record)
        {
            return mast != null && mast.gameObject.activeInHierarchy &&
                   IsEligible(mast) &&
                   GetBoatSceneIndex(mast) == record.targetBoatSceneIndex &&
                   mast.orderIndex == record.targetMastOrderIndex;
        }

        internal static void EnsureRuntimeButtons(Mast mast)
        {
            if (!IsEligible(mast))
            {
                return;
            }

            foreach (GameObject sailObject in mast.sails ?? new List<GameObject>())
            {
                EnsureRemovalButton(mast, sailObject);
            }
        }

        internal static void EnsureRemovalButton(Mast mast, GameObject sailObject)
        {
            Sail sail = sailObject != null ? sailObject.GetComponent<Sail>() : null;
            if (!IsEligible(mast) || sail == null ||
                sail.category != SailCategory.staysail)
            {
                return;
            }

            ReefEffectAnimUniversal reef =
                sail.GetComponentInChildren<ReefEffectAnimUniversal>(true);
            Renderer renderer = reef != null ? reef.furledSail : null;
            if (renderer == null)
            {
                return;
            }

            FurledStaysailButton button =
                renderer.GetComponent<FurledStaysailButton>();
            if (button == null)
            {
                BoxCollider collider = renderer.gameObject.AddComponent<BoxCollider>();
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null)
                {
                    collider.center = filter.sharedMesh.bounds.center;
                    collider.size = filter.sharedMesh.bounds.size;
                }
                collider.isTrigger = true;
                FurledStaysailHitbox marker =
                    renderer.gameObject.AddComponent<FurledStaysailHitbox>();
                marker.Set(collider);
                button = renderer.gameObject.AddComponent<FurledStaysailButton>();
                button.Initialize(mast, sail, collider);
                return;
            }
            button.Initialize(mast, sail);
        }
    }
}
