using System;
using UnityEngine;

namespace SwappableStaysail
{
    internal sealed class SailmakerPreview
    {
        internal Mast Mast { get; }
        internal Sail Sail { get; }

        internal SailmakerPreview(Mast mast, Sail sail)
        {
            Mast = mast;
            Sail = sail;
        }
    }

    internal sealed class SailmakerMastRigState
    {
        private readonly GPButtonRopeWinch[] leftAngleWinch;
        private readonly GPButtonRopeWinch[] rightAngleWinch;
        private readonly GPButtonRopeWinch[] midAngleWinch;
        private readonly GPButtonRopeWinch[] reefWinch;
        private readonly Transform[] midRopeAtt;
        private readonly Transform[] mastReefAtt;
        private readonly Transform[] mastReefAttExtension;

        internal SailmakerMastRigState(Mast mast)
        {
            leftAngleWinch = mast.leftAngleWinch;
            rightAngleWinch = mast.rightAngleWinch;
            midAngleWinch = mast.midAngleWinch;
            reefWinch = mast.reefWinch;
            midRopeAtt = mast.midRopeAtt;
            mastReefAtt = mast.mastReefAtt;
            mastReefAttExtension = mast.mastReefAttExtension;
        }

        internal void EnsureCapacity(Mast mast, int count)
        {
            mast.leftAngleWinch = Expand(leftAngleWinch, count);
            mast.rightAngleWinch = Expand(rightAngleWinch, count);
            mast.midAngleWinch = Expand(midAngleWinch, count);
            mast.reefWinch = Expand(reefWinch, count);
            mast.midRopeAtt = Expand(midRopeAtt, count);
            mast.mastReefAtt = Expand(mastReefAtt, count);
            mast.mastReefAttExtension = Expand(mastReefAttExtension, count);
        }

        internal void Restore(Mast mast)
        {
            mast.leftAngleWinch = leftAngleWinch;
            mast.rightAngleWinch = rightAngleWinch;
            mast.midAngleWinch = midAngleWinch;
            mast.reefWinch = reefWinch;
            mast.midRopeAtt = midRopeAtt;
            mast.mastReefAtt = mastReefAtt;
            mast.mastReefAttExtension = mastReefAttExtension;
        }

        private static T[] Expand<T>(T[] source, int count)
        {
            if (source == null || source.Length == 0 || source.Length >= count)
            {
                return source;
            }
            T[] expanded = new T[count];
            Array.Copy(source, expanded, source.Length);
            T fallback = source[source.Length - 1];
            for (int i = source.Length; i < expanded.Length; i++)
            {
                expanded[i] = fallback;
            }
            return expanded;
        }
    }

    internal sealed class SailmakerOrderButton : GoPointerButton
    {
        private Shipyard shipyard;

        internal void Initialize(Shipyard target)
        {
            shipyard = target;
            lookText = "Sail Maker";
        }

        public override void ExtraLateUpdate()
        {
            if (shipyard == null)
            {
                lookText = "";
            }
            else if (shipyard.GetBoatsInTriggerCount() < 1)
            {
                lookText = "(no boat in the Sail Maker area)";
            }
            else if (shipyard.GetBoatsInTriggerCount() > 1)
            {
                lookText = "(too many boats in the Sail Maker area)";
            }
            else
            {
                lookText = "Sail Maker";
            }
        }

        public override void OnActivate()
        {
            if (shipyard == null || GameState.currentShipyard != null ||
                shipyard.GetBoatsInTriggerCount() != 1)
            {
                return;
            }
            SailmakerSession.Request(shipyard);
            shipyard.ActivateDocuments();
        }
    }
}
