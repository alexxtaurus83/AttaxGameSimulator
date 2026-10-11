using System;

namespace Attax.Core.Utils {
    [Serializable]
    public sealed class AbilityToggles {
        public bool superJump;
        public bool convert;
        public bool block;
        public bool wall;
        public bool doubleMove;
        public bool spawn;
        public bool superClone;
        public bool unblock;
        public bool bomb;

        public AbilityToggles Clone() {
            AbilityToggles copy = new AbilityToggles();
            copy.superJump = this.superJump;
            copy.convert = this.convert;
            copy.block = this.block;
            copy.wall = this.wall;
            copy.doubleMove = this.doubleMove;
            copy.spawn = this.spawn;
            copy.superClone = this.superClone;
            copy.unblock = this.unblock;
            copy.bomb = this.bomb;
            return copy;
        }

        public bool IsEnabled(AbilityKind kind) {
            switch (kind) {
                case AbilityKind.SuperJump: return superJump;
                case AbilityKind.Convert: return convert;
                case AbilityKind.Block: return block;
                case AbilityKind.Wall: return wall;
                case AbilityKind.DoubleMove: return doubleMove;
                case AbilityKind.Spawn: return spawn;
                case AbilityKind.SuperClone: return superClone;
                case AbilityKind.Unblock: return unblock;
                case AbilityKind.Bomb: return bomb;
                default: return true;
            }
        }
    }
}
