#nullable enable
using System;
using System.Globalization;
using System.Text;

namespace Attax.Core.Utils.Validation {
    /// <summary>
    /// Shared display-name rule used by the server policy and the Unity client mirror.
    /// Lengths are supplied by the caller so the server can keep its configurable limits.
    /// </summary>
    public static class DisplayNameValidator {
        public const int DefaultMinLength = 3;
        public const int DefaultMaxLength = 24;

        private static readonly string[] Reserved = { "admin", "server", "system", "moderator", "attax", "support" };

        public static bool TryNormalize(string? raw, int minLength, int maxLength, out string normalized, out DisplayNameCheck result) {
            normalized = string.Empty;
            result = DisplayNameCheck.Invalid;

            if (string.IsNullOrWhiteSpace(raw)) return false;

            string candidate = raw!.Normalize(NormalizationForm.FormC).Trim();
            if (candidate.Length < minLength || candidate.Length > maxLength) return false;
            if (candidate.Contains("  ")) return false;

            foreach (char c in candidate) {
                UnicodeCategory cat = char.GetUnicodeCategory(c);
                if (cat == UnicodeCategory.Control || cat == UnicodeCategory.Format || cat == UnicodeCategory.PrivateUse
                    || cat == UnicodeCategory.Surrogate || cat == UnicodeCategory.OtherNotAssigned) {
                    return false;
                }
                bool ok = char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-' || c == '.';
                if (!ok) return false;
            }

            foreach (string name in Reserved) {
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)) {
                    result = DisplayNameCheck.Reserved;
                    return false;
                }
            }

            normalized = candidate;
            result = DisplayNameCheck.Valid;
            return true;
        }
    }
}
