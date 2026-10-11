using System;

namespace Attax.Core.Utils {
    public struct ActionOutcome {
        public ActionKind Kind;
        public AbilityKind Ability;
        public PlayerColor Actor;
        public ulong FlippedMask;
        public ulong RemovedMask;
        public ulong PlacedMask;
        public ulong BlockedAddedMask;
        public ulong BlockedClearedMask;
        public ulong FootprintPlacedMask;
        public ulong FootprintClearedMask;
        public int CreditsSpent;
        public int ActorCreditsGained;
        public int VictimCreditsGained;
        public bool TurnAdvanced;
        public TerminalResult Result;
    }
}
