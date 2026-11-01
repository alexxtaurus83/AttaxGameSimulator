using System;

namespace Attax.Core.Utils {
    public enum ActionError {
        None = 0,
        GameOver = 1,
        NotYourTurn = 2,
        OutOfBounds = 3,
        NotYourPiece = 4,
        DestinationNotEmpty = 5,
        SameSquare = 6,
        IllegalDistance = 7,
        NotEnemyChip = 8,
        NotBlockedCell = 9,
        InsufficientCredits = 10,
        AbilityLimitReachedThisTurn = 11,
        DoubleMoveAlreadyActive = 12,
        UnknownAction = 13,
        AbilityDisabled = 14,
        CounterOverflow = 15
    }
}
