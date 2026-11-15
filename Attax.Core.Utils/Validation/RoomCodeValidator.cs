#nullable enable
using System;

namespace Attax.Core.Utils.Validation {
    /// <summary>Shared private-room code format (alphabet and length) used by the server and the Unity client.</summary>
    public static class RoomCodeValidator {
        public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        public const int Length = 6;

        public static bool TryNormalize(string? input, out string normalized) {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(input)) return false;
            string trimmed = input!.Trim().ToUpperInvariant();
            if (trimmed.Length != Length) return false;
            for (int i = 0; i < trimmed.Length; i++) {
                if (Alphabet.IndexOf(trimmed[i]) < 0) return false;
            }
            normalized = trimmed;
            return true;
        }
    }
}
