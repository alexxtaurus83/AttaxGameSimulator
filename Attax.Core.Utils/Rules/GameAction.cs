using System;

namespace Attax.Core.Utils {
    public struct GameAction {
        public ActionKind Kind;
        public Move Move;
        public int X;
        public int Y;

        public static GameAction MoveAction(Move m) {
            return new GameAction {
                Kind = ActionKind.Move,
                Move = m,
                X = 0,
                Y = 0
            };
        }

        public static GameAction Targeted(ActionKind kind, int x, int y) {
            return new GameAction {
                Kind = kind,
                Move = default,
                X = x,
                Y = y
            };
        }

        public static GameAction DoubleMoveAction() {
            return new GameAction {
                Kind = ActionKind.DoubleMove,
                Move = default,
                X = 0,
                Y = 0
            };
        }
    }
}
