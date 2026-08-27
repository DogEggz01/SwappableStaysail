using System;
using HarmonyLib;
using cakeslice;
using UnityEngine;

namespace SwappableStaysail
{
    [HarmonyPatch(typeof(ShipyardDocuments), "Update")]
    internal static class SailmakerEntryPatch
    {
        private static bool cloning;

        [HarmonyPostfix]
        private static void Postfix(ShipyardDocuments __instance)
        {
            if (cloning || __instance == null || __instance.shipyard == null ||
                __instance.GetComponent<SailmakerEntryInstalled>() != null)
            {
                return;
            }

            cloning = true;
            try
            {
                GameObject clone = UnityEngine.Object.Instantiate(
                    __instance.gameObject,
                    __instance.transform.parent);
                clone.SetActive(false);
                clone.name = "Sail Maker";
                ShipyardDocuments copiedDocuments =
                    clone.GetComponent<ShipyardDocuments>();
                GameObject copiedKeeper = copiedDocuments != null
                    ? copiedDocuments.keeper
                    : null;

                // A live ShipyardDocuments clone carries its existing Outline
                // and interaction state. Strip all interaction components before
                // the clone is activated, then give it one clean Sail Maker button.
                foreach (GoPointerButton oldButton in
                         clone.GetComponentsInChildren<GoPointerButton>(true))
                {
                    UnityEngine.Object.DestroyImmediate(oldButton);
                }
                foreach (Outline oldOutline in
                         clone.GetComponentsInChildren<Outline>(true))
                {
                    UnityEngine.Object.DestroyImmediate(oldOutline);
                }
                foreach (SailmakerEntryInstalled copiedMarker in
                         clone.GetComponentsInChildren<SailmakerEntryInstalled>(true))
                {
                    UnityEngine.Object.DestroyImmediate(copiedMarker);
                }

                Collider sourceCollider = __instance.GetComponent<Collider>();
                Collider cloneCollider = clone.GetComponent<Collider>();
                foreach (Collider copiedCollider in
                         clone.GetComponentsInChildren<Collider>(true))
                {
                    if (copiedCollider != cloneCollider)
                    {
                        UnityEngine.Object.DestroyImmediate(copiedCollider);
                    }
                }

                Vector3 lateral = Vector3.zero;
                bool hasAuthoredPose = TryGetAuthoredPose(
                    __instance.gameObject.scene.name,
                    out Vector3 localPosition,
                    out Vector3 localEulerAngles);
                if (hasAuthoredPose)
                {
                    clone.transform.position =
                        __instance.transform.TransformPoint(localPosition);
                    clone.transform.rotation =
                        __instance.transform.rotation *
                        Quaternion.Euler(localEulerAngles);
                }
                else
                {
                    // Retain a safe fallback for modded or future shipyards that
                    // do not have a hand-authored pose.
                    lateral = GetSurfaceLateral(
                        sourceCollider,
                        __instance.transform);
                    float separation =
                        GetProjectedHalfExtent(sourceCollider, lateral) * 2f +
                        0.45f;
                    clone.transform.position = __instance.transform.position +
                                               lateral * separation;
                }

                SailmakerOrderButton button =
                    clone.AddComponent<SailmakerOrderButton>();
                button.Initialize(__instance.shipyard);
                SailmakerEntryStateMirror stateMirror =
                    clone.AddComponent<SailmakerEntryStateMirror>();
                stateMirror.Initialize(__instance.keeper, copiedKeeper);

                GameObject deliveryObject =
                    new GameObject("sail package delivery point");
                deliveryObject.transform.SetParent(clone.transform, false);
                deliveryObject.transform.localPosition = new Vector3(0f, 0.55f, 0.8f);
                SailmakerSession.RegisterDeliveryPoint(
                    __instance.shipyard,
                    deliveryObject.transform);

                __instance.gameObject.AddComponent<SailmakerEntryInstalled>();
                clone.SetActive(true);
                for (int attempt = 0;
                     !hasAuthoredPose &&
                     sourceCollider != null && cloneCollider != null &&
                     sourceCollider.bounds.Intersects(cloneCollider.bounds) &&
                     attempt < 8;
                     attempt++)
                {
                    clone.transform.position += lateral * 0.25f;
                }
            }
            catch (Exception exception)
            {
                SwappableStaysailPlugin.Log?.LogError(
                    $"Could not create sailmaker entry: {exception}");
            }
            finally
            {
                cloning = false;
            }
        }

        private static bool TryGetAuthoredPose(
            string sceneName,
            out Vector3 localPosition,
            out Vector3 localEulerAngles)
        {
            switch (sceneName)
            {
                case "island 1 A Gold Rock":
                    localPosition = new Vector3(-0.646f, -0.66f, 0f);
                    localEulerAngles = new Vector3(-1.716f, 0f, -27.634f);
                    return true;
                case "island 9 E Dragon Cliffs":
                    localPosition = new Vector3(-0.267f, -0.758f, 0f);
                    localEulerAngles = new Vector3(0f, 0f, 20.456f);
                    return true;
                case "island 15 M (Fort)":
                    localPosition = new Vector3(-0.993f, -1.933f, 0f);
                    localEulerAngles = new Vector3(0f, 0f, -15.067f);
                    return true;
                case "island 27 Lagoon SwampShipyard":
                    localPosition = new Vector3(-0.699f, -0.891f, 0f);
                    localEulerAngles = new Vector3(0f, 0f, -157.151f);
                    return true;
                default:
                    localPosition = Vector3.zero;
                    localEulerAngles = Vector3.zero;
                    return false;
            }
        }

        private static float GetProjectedHalfExtent(
            Collider collider,
            Vector3 direction)
        {
            if (collider == null)
            {
                return 0.6f;
            }
            Vector3 extent = collider.bounds.extents;
            Vector3 axis = direction.normalized;
            return Mathf.Abs(axis.x) * extent.x +
                   Mathf.Abs(axis.y) * extent.y +
                   Mathf.Abs(axis.z) * extent.z;
        }

        private static Vector3 GetSurfaceLateral(
            Collider collider,
            Transform source)
        {
            Vector3 normal = source.forward;
            BoxCollider box = collider as BoxCollider;
            if (box != null)
            {
                Vector3 size = box.size;
                if (size.x <= size.y && size.x <= size.z)
                {
                    normal = source.right;
                }
                else if (size.y <= size.x && size.y <= size.z)
                {
                    normal = source.up;
                }
            }

            Vector3 lateral = Vector3.Cross(Vector3.up, normal);
            if (lateral.sqrMagnitude < 0.01f)
            {
                lateral = Vector3.ProjectOnPlane(source.right, Vector3.up);
            }
            if (lateral.sqrMagnitude < 0.01f)
            {
                lateral = source.right;
            }
            lateral.Normalize();
            if (Vector3.Dot(lateral, source.right) < 0f)
            {
                lateral = -lateral;
            }
            return lateral;
        }
    }

    internal sealed class SailmakerEntryInstalled : MonoBehaviour
    {
    }

    internal sealed class SailmakerEntryStateMirror : MonoBehaviour
    {
        private GameObject sourceKeeper;
        private GameObject sailmakerKeeper;

        internal void Initialize(GameObject source, GameObject copy)
        {
            sourceKeeper = source;
            sailmakerKeeper = copy;
            Synchronize();
        }

        private void LateUpdate()
        {
            Synchronize();
        }

        private void Synchronize()
        {
            if (sourceKeeper == null || sailmakerKeeper == null)
            {
                return;
            }
            bool visible = sourceKeeper.activeSelf;
            if (sailmakerKeeper.activeSelf != visible)
            {
                sailmakerKeeper.SetActive(visible);
            }
        }
    }
}

