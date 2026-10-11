using System;
using System.Collections.Generic;

namespace Attax.Core.Utils {
    [Serializable]
    public sealed class GameRulesConfig {
        public const int CurrentSchemaVersion = 1;
        public int schemaVersion;
        public EconomyRules economy;
        public TurnRules turn;
        public PlayerAbilitiesConfig playerAbilities;

        public GameRulesConfig Clone() {
            GameRulesConfig copy = new GameRulesConfig();
            copy.schemaVersion = this.schemaVersion;
            copy.economy = this.economy != null ? this.economy.Clone() : null;
            copy.turn = this.turn != null ? this.turn.Clone() : null;
            copy.playerAbilities = this.playerAbilities != null ? this.playerAbilities.Clone() : null;
            return copy;
        }

        public bool IsAbilityEnabled(PlayerColor player, AbilityKind kind) {
            if (kind == AbilityKind.None) return true;
            if (playerAbilities == null) return false;
            return playerAbilities.IsEnabled(player, kind);
        }

        public List<string> Validate() {
            List<string> errors = new List<string>();

            if (schemaVersion != CurrentSchemaVersion) {
                errors.Add("Unsupported schemaVersion: " + schemaVersion);
            }

            if (economy == null) {
                errors.Add("economy group is missing");
            } else {
                if (economy.maxCredits <= 0) {
                    errors.Add("maxCredits must be positive");
                }
                if (economy.startingCredits < 0) {
                    errors.Add("startingCredits cannot be negative");
                }
                if (economy.maxCredits > 0 && economy.startingCredits > economy.maxCredits) {
                    errors.Add("startingCredits cannot exceed maxCredits");
                }
                if (economy.captureReward < 0) {
                    errors.Add("captureReward cannot be negative");
                }
                if (economy.footprintReward < 0) {
                    errors.Add("footprintReward cannot be negative");
                }

                if (economy.abilityCosts == null) {
                    errors.Add("abilityCosts group is missing");
                } else {
                    if (economy.abilityCosts.superJump < 0) errors.Add("superJump cost cannot be negative");
                    if (economy.abilityCosts.convert < 0) errors.Add("convert cost cannot be negative");
                    if (economy.abilityCosts.block < 0) errors.Add("block cost cannot be negative");
                    if (economy.abilityCosts.wall < 0) errors.Add("wall cost cannot be negative");
                    if (economy.abilityCosts.doubleMove < 0) errors.Add("doubleMove cost cannot be negative");
                    if (economy.abilityCosts.spawn < 0) errors.Add("spawn cost cannot be negative");
                    if (economy.abilityCosts.superClone < 0) errors.Add("superClone cost cannot be negative");
                    if (economy.abilityCosts.unblock < 0) errors.Add("unblock cost cannot be negative");
                    if (economy.abilityCosts.bomb < 0) errors.Add("bomb cost cannot be negative");
                }
            }

            if (turn == null) {
                errors.Add("turn group is missing");
            } else {
                if (turn.maxAbilityActionsPerTurn != 1) {
                    errors.Add("maxAbilityActionsPerTurn must be exactly 1");
                }
            }

            if (playerAbilities == null) {
                errors.Add("playerAbilities group is missing");
            } else {
                if (playerAbilities.red == null) {
                    errors.Add("playerAbilities.red group is missing");
                }
                if (playerAbilities.blue == null) {
                    errors.Add("playerAbilities.blue group is missing");
                }
            }

            return errors;
        }

        public ulong Fingerprint() {
            ulong hash = 14695981039346656037UL;
            hash = FnvFeed(hash, (ulong)(long)schemaVersion);
            hash = FnvFeed(hash, economy != null && economy.enabled ? 1UL : 0UL);
            hash = FnvFeed(hash, (ulong)(long)(economy != null ? economy.startingCredits : 0));
            hash = FnvFeed(hash, (ulong)(long)(economy != null ? economy.maxCredits : 0));
            hash = FnvFeed(hash, (ulong)(long)(economy != null ? economy.captureReward : 0));
            hash = FnvFeed(hash, (ulong)(long)(economy != null ? economy.footprintReward : 0));

            AbilityCosts costs = economy != null ? economy.abilityCosts : null;
            hash = FnvFeed(hash, (ulong)(long)(costs != null ? costs.superJump : 0));
            hash = FnvFeed(hash, (ulong)(long)(costs != null ? costs.convert : 0));
            hash = FnvFeed(hash, (ulong)(long)(costs != null ? costs.block : 0));
            hash = FnvFeed(hash, (ulong)(long)(costs != null ? costs.wall : 0));
            hash = FnvFeed(hash, (ulong)(long)(costs != null ? costs.doubleMove : 0));
            hash = FnvFeed(hash, (ulong)(long)(costs != null ? costs.spawn : 0));
            hash = FnvFeed(hash, (ulong)(long)(costs != null ? costs.superClone : 0));
            hash = FnvFeed(hash, (ulong)(long)(costs != null ? costs.unblock : 0));
            hash = FnvFeed(hash, (ulong)(long)(costs != null ? costs.bomb : 0));

            hash = FnvFeed(hash, (ulong)(long)(turn != null ? turn.maxAbilityActionsPerTurn : 0));

            AbilityToggles redToggles = playerAbilities != null ? playerAbilities.red : null;
            hash = FnvFeed(hash, redToggles != null && redToggles.superJump ? 1UL : 0UL);
            hash = FnvFeed(hash, redToggles != null && redToggles.convert ? 1UL : 0UL);
            hash = FnvFeed(hash, redToggles != null && redToggles.block ? 1UL : 0UL);
            hash = FnvFeed(hash, redToggles != null && redToggles.wall ? 1UL : 0UL);
            hash = FnvFeed(hash, redToggles != null && redToggles.doubleMove ? 1UL : 0UL);
            hash = FnvFeed(hash, redToggles != null && redToggles.spawn ? 1UL : 0UL);
            hash = FnvFeed(hash, redToggles != null && redToggles.superClone ? 1UL : 0UL);
            hash = FnvFeed(hash, redToggles != null && redToggles.unblock ? 1UL : 0UL);
            hash = FnvFeed(hash, redToggles != null && redToggles.bomb ? 1UL : 0UL);

            AbilityToggles blueToggles = playerAbilities != null ? playerAbilities.blue : null;
            hash = FnvFeed(hash, blueToggles != null && blueToggles.superJump ? 1UL : 0UL);
            hash = FnvFeed(hash, blueToggles != null && blueToggles.convert ? 1UL : 0UL);
            hash = FnvFeed(hash, blueToggles != null && blueToggles.block ? 1UL : 0UL);
            hash = FnvFeed(hash, blueToggles != null && blueToggles.wall ? 1UL : 0UL);
            hash = FnvFeed(hash, blueToggles != null && blueToggles.doubleMove ? 1UL : 0UL);
            hash = FnvFeed(hash, blueToggles != null && blueToggles.spawn ? 1UL : 0UL);
            hash = FnvFeed(hash, blueToggles != null && blueToggles.superClone ? 1UL : 0UL);
            hash = FnvFeed(hash, blueToggles != null && blueToggles.unblock ? 1UL : 0UL);
            hash = FnvFeed(hash, blueToggles != null && blueToggles.bomb ? 1UL : 0UL);

            return hash;
        }

        public int GetAbilityCost(AbilityKind kind) {
            if (economy == null || !economy.enabled || economy.abilityCosts == null) {
                return 0;
            }

            int cost = 0;
            switch (kind) {
                case AbilityKind.SuperJump: cost = economy.abilityCosts.superJump; break;
                case AbilityKind.Convert: cost = economy.abilityCosts.convert; break;
                case AbilityKind.Block: cost = economy.abilityCosts.block; break;
                case AbilityKind.Wall: cost = economy.abilityCosts.wall; break;
                case AbilityKind.DoubleMove: cost = economy.abilityCosts.doubleMove; break;
                case AbilityKind.Spawn: cost = economy.abilityCosts.spawn; break;
                case AbilityKind.SuperClone: cost = economy.abilityCosts.superClone; break;
                case AbilityKind.Unblock: cost = economy.abilityCosts.unblock; break;
                case AbilityKind.Bomb: cost = economy.abilityCosts.bomb; break;
                default: return 0;
            }

            return cost > 0 ? cost : 0;
        }

        private static ulong FnvFeed(ulong hash, ulong val) {
            unchecked {
                for (int i = 0; i < 8; i++) {
                    byte b = (byte)((val >> (i * 8)) & 0xFF);
                    hash ^= b;
                    hash *= 1099511628211UL;
                }
            }
            return hash;
        }
    }
}
