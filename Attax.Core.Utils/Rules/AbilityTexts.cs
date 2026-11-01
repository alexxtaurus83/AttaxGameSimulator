using System;

namespace Attax.Core.Utils {
    [Serializable]
    public sealed class AbilityText {
        public string name;
        public string description;
        public string instruction;

        public AbilityText Clone() {
            return new AbilityText {
                name = this.name,
                description = this.description,
                instruction = this.instruction
            };
        }
    }

    [Serializable]
    public sealed class AbilityTexts {
        public AbilityText superJump;
        public AbilityText convert;
        public AbilityText block;
        public AbilityText wall;
        public AbilityText doubleMove;
        public AbilityText spawn;
        public AbilityText superClone;
        public AbilityText unblock;
        public AbilityText bomb;

        public AbilityTexts Clone() {
            return new AbilityTexts {
                superJump = this.superJump?.Clone(),
                convert = this.convert?.Clone(),
                block = this.block?.Clone(),
                wall = this.wall?.Clone(),
                doubleMove = this.doubleMove?.Clone(),
                spawn = this.spawn?.Clone(),
                superClone = this.superClone?.Clone(),
                unblock = this.unblock?.Clone(),
                bomb = this.bomb?.Clone()
            };
        }

        public AbilityText Get(AbilityKind kind) {
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
                default: return null;
            }
        }
    }
}
