using System;
using UnityEngine;

namespace SwappableStaysail
{
    [Serializable]
    internal sealed class SailpackRecord
    {
        public int sailPrefabIndex;
        public int sailColor;
        public float scaleY = 1f;
        public float scaleZ = 1f;
        public float packageMass = SailpackWeight.LegacyPackageMass;
        public float installHeight;
        public float minAngle;
        public float maxAngle;
        public int targetBoatSceneIndex;
        public int targetMastOrderIndex;
        public string targetStayName;
        public string sailName;

        internal static SailpackRecord FromSail(Sail sail, Mast mast)
        {
            return new SailpackRecord
            {
                sailPrefabIndex = sail.prefabIndex,
                sailColor = sail.activeColor,
                scaleY = sail.GetScaleY(),
                scaleZ = sail.GetScaleZ(),
                packageMass = SailpackWeight.FromSail(sail),
                installHeight = sail.GetCurrentInstallHeight(),
                minAngle = sail.minAngle,
                maxAngle = sail.maxAngle,
                targetBoatSceneIndex = StaysailTargets.GetBoatSceneIndex(mast),
                targetMastOrderIndex = mast.orderIndex,
                targetStayName = StaysailTargets.GetStayName(mast),
                sailName = string.IsNullOrEmpty(sail.sailName)
                    ? sail.gameObject.name
                    : sail.sailName
            };
        }

        internal string GetDisplayName()
        {
            string stay = string.IsNullOrEmpty(targetStayName)
                ? "Staysail stay"
                : targetStayName;
            string sail = string.IsNullOrEmpty(sailName)
                ? "Staysail"
                : sailName;
            int percentage = Mathf.RoundToInt(scaleY * 100f);
            return $"{stay} {sail} ({percentage}%)";
        }

        internal SailpackRecord Copy()
        {
            return (SailpackRecord)MemberwiseClone();
        }

        internal void Normalize()
        {
            if (float.IsNaN(packageMass) || float.IsInfinity(packageMass) ||
                packageMass <= 0f)
            {
                // Version-1 records did not store weight. Preserve their original
                // fixed package mass instead of guessing from mutable game settings.
                packageMass = SailpackWeight.LegacyPackageMass;
            }
        }
    }

    internal static class SailpackWeight
    {
        internal const float LegacyPackageMass = 0.1f;

        internal static float FromSail(Sail sail)
        {
            if (sail == null)
            {
                return LegacyPackageMass;
            }

            // Match NAND Tweaks' shipyard weight calculation. For staysails the
            // displayed weight is GetRealSailPower() * 20. Capturing it now keeps
            // the package stable even if sail balance settings change later.
            float baseMass = Mathf.Max(0f, sail.GetRealSailPower() * 20f);
            float categoryMass;
            if (sail.category == SailCategory.junk ||
                sail.category == SailCategory.gaff)
            {
                categoryMass = baseMass;
            }
            else if (sail.category == SailCategory.staysail)
            {
                categoryMass = 0f;
            }
            else
            {
                categoryMass = baseMass * 0.5f;
            }
            return Mathf.Max(LegacyPackageMass, baseMass + categoryMass);
        }
    }

    [Serializable]
    internal sealed class SailpackSaveEntry
    {
        public int instanceId;
        public SailpackRecord record;
    }

    [Serializable]
    internal sealed class SailpackSaveFile
    {
        public int formatVersion = 2;
        public SailpackSaveEntry[] sailpacks = Array.Empty<SailpackSaveEntry>();
    }
}
