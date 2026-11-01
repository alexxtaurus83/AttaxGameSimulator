namespace Attax.Core.Utils {
    /// <summary>What TryConvertEnemyChip turns the targeted enemy chip into.</summary>
    public enum ChipConversion {
        /// <summary>The chip changes owner and becomes a chip of the acting player.</summary>
        ToPlayer,
        /// <summary>The chip is removed and its cell becomes a permanently blocked cell (neither player's).</summary>
        ToBlocked
    }
}
