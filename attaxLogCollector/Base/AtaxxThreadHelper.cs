
using System;
using System.Collections.Generic;

namespace Assets.Scripts.Base.Ataxx {
    public class AtaxxThreadHelper {
        
        public BitboardState boardForThread { get; set; }
        public AtaxxAIEngine.Move[,] killerMovesForThread { get; set; }        
        public AtaxxThreadHelper(int boardSize, int maxDepth) {
            boardForThread = new BitboardState();           
            killerMovesForThread = new AtaxxAIEngine.Move[maxDepth, 2];
        
        }
    }
}
