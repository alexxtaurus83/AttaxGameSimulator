using System;

namespace Attax.Core.Utils {
    public struct Move : IEquatable<Move> {
        public int FromX, FromY, ToX, ToY;

        public Move(int fx, int fy, int tx, int ty) {
            FromX = fx;
            FromY = fy;
            ToX = tx;
            ToY = ty;
        }

        public bool Equals(Move other) {
            return FromX == other.FromX && FromY == other.FromY && ToX == other.ToX && ToY == other.ToY;
        }

        public override bool Equals(object? obj) {
            return obj is Move other && Equals(other);
        }

        public override int GetHashCode() {
            return HashCode.Combine(FromX, FromY, ToX, ToY);
        }

        public static bool operator ==(Move left, Move right) {
            return left.Equals(right);
        }

        public static bool operator !=(Move left, Move right) {
            return !(left == right);
        }

        public override string ToString() {
            return $"({FromX},{FromY})->({ToX},{ToY})";
        }
    }
}
