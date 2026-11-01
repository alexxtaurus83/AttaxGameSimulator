using System;
using Attax.Core;
using Attax.Core.Utils;
using Attax.Server.Contracts.Actions;
using Attax.Server.Contracts.Common;
using Attax.Server.Contracts.Gameplay;

namespace Attax.UserSimulator {
    public static class SnapshotMapper {
        public static GameState ToState(GameSnapshotDto s) {
            if (s == null) throw new ArgumentNullException(nameof(s));

            ulong red = HexFormat.ParseHex(s.RedPiecesMask);
            ulong blue = HexFormat.ParseHex(s.BluePiecesMask);
            ulong blocked = HexFormat.ParseHex(s.BlockedSquaresMask);
            ulong redFp = HexFormat.ParseHex(s.RedFootprintsMask);
            ulong blueFp = HexFormat.ParseHex(s.BlueFootprintsMask);

            var side = s.SideToMove switch {
                PlayerColorDto.Red => PlayerColor.Red,
                PlayerColorDto.Blue => PlayerColor.Blue,
                _ => PlayerColor.None
            };

            var rules = MapRules(s.Rules);

            var state = GameState.Restore(
                red, blue, blocked, redFp, blueFp, side,
                s.RedCredits, s.BlueCredits,
                s.TurnNumber, s.ActionCount,
                s.MovesRemainingThisTurn, s.AbilityActionsUsedThisTurn,
                rules
            );

            ulong expectedFp = HexFormat.ParseHex(s.StateFingerprint);
            ulong actualFp = state.ComputeFingerprint();
            if (expectedFp != actualFp) {
                throw new InvalidOperationException($"State fingerprint mismatch: expected 0x{expectedFp:X16}, got 0x{actualFp:X16}");
            }

            return state;
        }

        public static ActionIntentDto ToIntent(GameAction a) {
            return a.Kind switch {
                ActionKind.Move => new ActionIntentDto {
                    Kind = ActionKindDto.Move,
                    FromX = a.Move.FromX,
                    FromY = a.Move.FromY,
                    ToX = a.Move.ToX,
                    ToY = a.Move.ToY
                },
                ActionKind.DoubleMove => new ActionIntentDto {
                    Kind = ActionKindDto.DoubleMove
                },
                ActionKind.Spawn => new ActionIntentDto {
                    Kind = ActionKindDto.Spawn,
                    ToX = a.X,
                    ToY = a.Y
                },
                ActionKind.SuperClone => new ActionIntentDto {
                    Kind = ActionKindDto.SuperClone,
                    ToX = a.X,
                    ToY = a.Y
                },
                ActionKind.Convert => new ActionIntentDto {
                    Kind = ActionKindDto.Convert,
                    ToX = a.X,
                    ToY = a.Y
                },
                ActionKind.Block => new ActionIntentDto {
                    Kind = ActionKindDto.Block,
                    ToX = a.X,
                    ToY = a.Y
                },
                ActionKind.Wall => new ActionIntentDto {
                    Kind = ActionKindDto.Wall,
                    ToX = a.X,
                    ToY = a.Y
                },
                ActionKind.Unblock => new ActionIntentDto {
                    Kind = ActionKindDto.Unblock,
                    ToX = a.X,
                    ToY = a.Y
                },
                ActionKind.Bomb => new ActionIntentDto {
                    Kind = ActionKindDto.Bomb,
                    ToX = a.X,
                    ToY = a.Y
                },
                _ => throw new ArgumentOutOfRangeException(nameof(a.Kind), $"Unknown ActionKind: {a.Kind}")
            };
        }

        public static GameRulesConfig MapRules(GameRulesConfigDto? dto) {
            if (dto == null) {
                return new GameRulesConfig {
                    schemaVersion = 1,
                    economy = new EconomyRules { enabled = false }
                };
            }

            var cfg = new GameRulesConfig {
                schemaVersion = dto.SchemaVersion,
                turn = new TurnRules {
                    maxAbilityActionsPerTurn = dto.Turn?.MaxAbilityActionsPerTurn ?? 1
                }
            };

            if (dto.Economy != null) {
                cfg.economy = new EconomyRules {
                    enabled = dto.Economy.Enabled,
                    startingCredits = dto.Economy.StartingCredits,
                    maxCredits = dto.Economy.MaxCredits,
                    captureReward = dto.Economy.CaptureReward,
                    footprintReward = dto.Economy.FootprintReward,
                    abilityCosts = new AbilityCosts {
                        superJump = dto.Economy.AbilityCosts?.SuperJump ?? 100,
                        convert = dto.Economy.AbilityCosts?.Convert ?? 100,
                        block = dto.Economy.AbilityCosts?.Block ?? 100,
                        wall = dto.Economy.AbilityCosts?.Wall ?? 100,
                        doubleMove = dto.Economy.AbilityCosts?.DoubleMove ?? 100,
                        spawn = dto.Economy.AbilityCosts?.Spawn ?? 100,
                        superClone = dto.Economy.AbilityCosts?.SuperClone ?? 100,
                        unblock = dto.Economy.AbilityCosts?.Unblock ?? 100,
                        bomb = dto.Economy.AbilityCosts?.Bomb ?? 100
                    }
                };
            }

            if (dto.PlayerAbilities != null) {
                cfg.playerAbilities = new PlayerAbilitiesConfig {
                    red = MapToggles(dto.PlayerAbilities.Red),
                    blue = MapToggles(dto.PlayerAbilities.Blue)
                };
            }

            if (dto.AbilityTexts != null) {
                cfg.abilityTexts = new AbilityTexts {
                    superJump = MapText(dto.AbilityTexts.SuperJump),
                    convert = MapText(dto.AbilityTexts.Convert),
                    block = MapText(dto.AbilityTexts.Block),
                    wall = MapText(dto.AbilityTexts.Wall),
                    doubleMove = MapText(dto.AbilityTexts.DoubleMove),
                    spawn = MapText(dto.AbilityTexts.Spawn),
                    superClone = MapText(dto.AbilityTexts.SuperClone),
                    unblock = MapText(dto.AbilityTexts.Unblock),
                    bomb = MapText(dto.AbilityTexts.Bomb)
                };
            } else {
                cfg.abilityTexts = DefaultAbilityTexts();
            }

            return cfg;
        }

        private static AbilityText MapText(AbilityTextDto? d) {
            if (d == null) return new AbilityText();
            return new AbilityText {
                name = d.Name,
                description = d.Description,
                instruction = d.Instruction
            };
        }

        private static AbilityTexts DefaultAbilityTexts() {
            return new AbilityTexts {
                superJump = new AbilityText { name = "Super Jump", description = "Jump an owned chip to any distant empty cell (3+ steps away).", instruction = "Select an owned chip to jump, then choose a distant destination." },
                convert = new AbilityText { name = "Chief", description = "Convert an enemy chip directly to your color without capturing neighbors.", instruction = "Select an enemy chip to convert." },
                block = new AbilityText { name = "Block", description = "Remove an enemy chip and turn its cell into a blocked obstacle.", instruction = "Select an enemy chip to block." },
                wall = new AbilityText { name = "Wall", description = "Turn any empty cell into a blocked obstacle.", instruction = "Select an empty cell to place a wall." },
                doubleMove = new AbilityText { name = "Double Move", description = "Take two moves this turn instead of one.", instruction = "" },
                spawn = new AbilityText { name = "Spawn", description = "Place a new chip on any empty cell without capturing neighbors.", instruction = "Select an empty cell to spawn a chip." },
                superClone = new AbilityText { name = "Super Clone", description = "Place a new chip on any empty cell and capture all adjacent enemy chips.", instruction = "Select an empty cell for Super Clone." },
                unblock = new AbilityText { name = "Unblock", description = "Remove a blocked obstacle, opening the cell for moves.", instruction = "Select a blocked obstacle to unblock." },
                bomb = new AbilityText { name = "Bomb", description = "Destroy an enemy chip and clear all marks from its cell.", instruction = "Select an enemy chip to bomb." }
            };
        }

        private static AbilityToggles MapToggles(AbilityTogglesDto? d) {
            if (d == null) return new AbilityToggles();
            return new AbilityToggles {
                superJump = d.SuperJump,
                convert = d.Convert,
                block = d.Block,
                wall = d.Wall,
                doubleMove = d.DoubleMove,
                spawn = d.Spawn,
                superClone = d.SuperClone,
                unblock = d.Unblock,
                bomb = d.Bomb
            };
        }
    }
}
