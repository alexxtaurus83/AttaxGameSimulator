using System;

namespace Attax.Core.Utils {
    [Serializable]
    public sealed class AbilityCosts {
        public int superJump;
        public int convert;
        public int block;
        public int wall;
        public int doubleMove;
        public int spawn;
        public int superClone;
        public int unblock;
        public int bomb;

        public AbilityCosts Clone() {
            var copy = new AbilityCosts();
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
    }
}
