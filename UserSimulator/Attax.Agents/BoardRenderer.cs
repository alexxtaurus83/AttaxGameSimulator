using System.Text;
using Attax.Core;
using Attax.Core.Utils;

namespace Attax.Agents {
    public static class BoardRenderer {
        public static string Render(BitboardState b, PlayerColor? sideToMove, int? redCredits = null, int? blueCredits = null) {
            var sb = new StringBuilder();
            sb.AppendLine("  a b c d e f g");
            int redCount = 0;
            int blueCount = 0;

            for (int y = 0; y < AttaxConstants.BaseConst.BoardSize; y++) {
                sb.Append(y + 1);
                sb.Append(' ');
                for (int x = 0; x < AttaxConstants.BaseConst.BoardSize; x++) {
                    int idx = y * AttaxConstants.BaseConst.BoardSize + x;
                    ulong bit = 1UL << idx;
                    char c = '.';
                    if ((b.RedPieces & bit) != 0) {
                        c = 'R';
                        redCount++;
                    } else if ((b.BluePieces & bit) != 0) {
                        c = 'B';
                        blueCount++;
                    } else if ((b.BlockedSquares & bit) != 0) {
                        c = '#';
                    }
                    sb.Append(c);
                    if (x < AttaxConstants.BaseConst.BoardSize - 1) sb.Append(' ');
                }
                sb.AppendLine();
            }

            string redCredStr = redCredits.HasValue ? $" ({redCredits.Value} credits)" : "";
            string blueCredStr = blueCredits.HasValue ? $" ({blueCredits.Value} credits)" : "";
            string toMoveStr = sideToMove.HasValue ? sideToMove.Value.ToString() : "None";
            sb.Append($"Red {redCount}{redCredStr} | Blue {blueCount}{blueCredStr} | to move: {toMoveStr}");

            return sb.ToString();
        }
    }
}
