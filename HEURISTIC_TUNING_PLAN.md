# Heuristic tuning plan (v3: parameter tuning only)

This replaces the earlier versions. Decisions that shape it: **shipped = depth 3 + quiescence** (depth 4 is only a "very hard" setting and is not
tuned), **blocked cells 0 to 6 are part of the game**, and **this plan covers parameter tuning only**; difficulty levels are out of scope (section 2).
It is a targeted check of what can be tuned, not a search for a super AI. Written for a new session: section 0 is the hand-off.

Status: tooling is built and tested. **No tuning run has been done.** Everything marked "measured" says where it was measured; everything else is a hypothesis.

---

## 0. Hand-off

1. Verify the tree: `dotnet build Attax.sln -c Release`, then `dotnet test Attax.Core.Tests/Attax.Core.Tests.csproj -c Release`
   (176 tests, about 2.5 minutes on a free machine) and `python -m unittest discover -s tests_py -t .` (59, 5 skipped).
2. Publish to a scratch folder, never over a folder another job may be running from:
   `dotnet publish Attax.Console/Attax.Console.csproj -c Release -o E:\tmp_attax\tune_exe`. The base file `engine-params.json` is copied next to it.
3. **Parameter values live in `Attax.Core/engine-params.json` only.** To test a candidate: a small file with only the changed values
   (`params=file.json`) or `p.<group>.<name>=value` in the player spec. Layers: base file, then file, then overrides. See the Readme.
4. Use `--blocked 0,6` in every arena and every position log for tuning: that is the game.
5. Keep experiment state in `tuning/` (add it to `.gitignore`): `ledger.csv`, `params/*.json`, `logs/*.txt`.
6. Questions that still change the plan are in section 9.

## 1. What the numbers mean

**Elo difference** is a way to say "how much stronger" using the share of points one player wins against another:

| P1 wins this share of points vs P2 | Elo | what it feels like |
| --- | --- | --- |
| 50% | 0 | equal |
| 53% | +21 | slightly better; needs hundreds of games to even see |
| 57% | +49 | clearly better |
| 64% | +100 | clearly stronger: wins about 2 games of 3 |
| 76% | +200 | much stronger |
| 91% | +400 | hopeless for the other player |

**"Nothing beats the defaults by 10 Elo"** means: after tuning, the best parameter set might win only about 51.5% of points against the current one.
That is real but invisible to a human player, and takes over 3,000 game pairs to confirm. It is a warning that the current constants may already be near
the best for depth 3, so the honest outcome of tuning could be "no change worth shipping". It does **not** mean the tooling is useless; it means
expectations should be small. Tuning gains are tens of Elo at best and expensive to confirm; that is the reason for the sizes in sections 5 and 6.

## 2. Out of scope: difficulty levels

Parked on request; nothing in this plan builds or measures levels. One finding is recorded so it is not lost: the weak settings (depth 1, and depth 2 without
quiescence) are **not usable as easy or normal levels as they are**, because they make moves that let the opponent capture immediately and games end in a few moves.
A level has to be tuned properly, per game stage, and that needs its own plan (it is a different question from "can the evaluator weights be improved": it is about how
to weaken the engine without making it blunder). The only measurement that exists is a single 30-pair run against the shipped engine at `--blocked 0,6`
(depth 1: 10%, depth 2: 8%); it is not repeated or extended here.

## 3. What is worth tuning, and what is not

Regressing every parameter is too much for a "check whether anything is worth changing" goal. This section narrows it using one measurement. The numbers below are
"share of positions where the chosen move changed" when one parameter was moved by roughly +/-50%: **no game result, only where to look**.

**Per-stage view (depth 3, 220 positions from a blocked-board self-play log, `compare-moves`):**

| Change | all | opening (<=14 pieces) | midgame (15-35) | late (>35) |
| --- | --- | --- | --- | --- |
| eval.centerControlWeight 2 -> 3 | 7.3% | 3.0% | **13.7%** | 0% |
| eval.materialLate 40 -> 60 | 6.4% | 4.5% | 9.8% | 2.0% |
| eval.endgameEmptyThreshold 14 -> 20 | 5.5% | 0% | **11.8%** | 0% |
| eval.endgameEmptyThreshold 14 -> 8 | 4.1% | 0% | 7.8% | 2.0% |
| eval.mobilityEarly 3 -> 5 | 2.7% | 3.0% | 3.9% | 0% |
| eval.mobilityLate 4 -> 6 | 1.8% | 0% | 3.9% | 0% |
| eval.potentialMobilityWeight 3 -> 5 | 1.8% | 3.0% | 2.0% | 0% |
| eval.edgeWeight 2 -> 3 | 1.8% | 1.5% | 2.9% | 0% |
| eval.stabilityWeight 14 -> 21 | 1.4% | 0% | 2.0% | 2.0% |
| eval.cornerWeight 5 -> 8 | 0.9% | 1.5% | 1.0% | 0% |
| root.flipPoints 0 -> 1 | 0.5% | 0% | 1.0% | 0% |
| root.riskPoints 0.8 -> 1.6, root.nonCapturingJumpPenalty 6 -> 12 | **0.0%** | 0% | 0% | 0% |

(Small sample: a 1% figure is 2 positions out of 220; do not read single decimals. An earlier open-board measurement, 800 positions at depth 2,
agrees on the ordering: centre control, material and the phase threshold on top, root bonuses at the bottom.)

What this says (fact, then interpretation):
* The **midgame (15-35 pieces) is where parameters change moves**, not the opening or the late game. The late game barely moves (0-2%): with 14 or
  fewer empty squares depth 3 sees nearly everything.
* The **root bonuses** (`root.*`) change 0-1% of moves at depth 3, so they cannot move strength. Treat them as inert; do not spend games on them.
* The eval levers that matter are **space and mobility** (centre control, mobility, potential mobility), **material weighting**, and the **phase switch**
  (`endgameEmptyThreshold`: when the engine stops counting mobility and starts counting stability).

**About your observation that the engine's strength is "to surround you and block your moves":** that is the mobility family, and it is a small number of
parameters. In the evaluator it is `mobilityEarly/Late` (my reachable squares minus theirs), `potentialMobilityWeight` (squares next to my reachable squares) and
`centerControlWeight`. The decisive stage is the midgame. The plan therefore tunes **only this family plus material and the phase threshold, in the midgame**, not all 40 numbers.

**Parameters to tune (the whole list, about 8):**

| Priority | Parameter | Why |
| --- | --- | --- |
| 1 | `eval.centerControlWeight` | biggest effect (13.7% of midgame moves) |
| 1 | `eval.materialLate` (relative to `materialEarly = 30`, which is fixed as the anchor) | biggest effect after the above |
| 1 | `eval.endgameEmptyThreshold` | 11.8% of midgame moves; also the phase switch, interacts with blocks (`14` is an empty-square count; with b blocked cells the endgame starts earlier in pieces) |
| 2 | `eval.mobilityEarly`, `eval.mobilityLate` | the "block them in" term |
| 2 | `eval.potentialMobilityWeight` | same family |
| 3 | `eval.edgeWeight`, `eval.cornerWeight`, `eval.stabilityWeight` | smaller effect; only if priority 1-2 show anything |
| skip | all of `root.*`, `eval.stabilityLowMult`, `eval.stabilityFullMinPieces`, tables, `search.*` | inert in measurement, or not a strength lever (search gates are a speed question) |

**Not worth doing unless the first group shows something:** the 49-cell tables, the aggression table, the null-move and quiescence gates.

## 4. How parameters interact (so they are not tuned as if independent)

The evaluator is a **sum** of terms, so only ratios between terms matter. The coupled groups, from the code:

* **Mobility family** (`mobilityEarly/Late`, `potentialMobilityWeight`, `centerControlWeight`): all are "space" terms, all active only while more than
  `endgameEmptyThreshold` squares are empty. They push in the same direction, so the first test is **one joint move** of the whole family by a factor
  (x0.7, x1.4); after that, one member against the others.
* **`materialLate` relative to the family**: material is the anchor (`materialEarly` fixed at 30). Raising `materialLate` is the same as lowering every other
  term late in the game. Test it as a ratio.
* **`endgameEmptyThreshold`** removes all the midgame terms at once and switches on stability. It couples to everything above and to the **number of
  blocked cells** (blocks reduce empties without adding pieces), so it must be tested with `--blocked 0,6`, not on an open board.
* Material and mobility weights are interpolated by piece count and **cut to whole numbers**, so a change of 1 can move several piece counts. Move by at least 2.

Rules: eval before everything else; the accepted values become the new baseline file (`tuning/params/base_N.json`) after each block; after a block is
accepted, test the combination of the last two accepted changes once against the baseline; finish with a run of the whole accepted set against the
original (section 6, phase 4).

## 5. Cost of a game (measured)

`classic:depth=3` with quiescence (the shipped setting) against a tweaked copy of itself, `--blocked 0,6`, 48 pairs, 10 games in parallel, machine about 35% loaded by
another job:

| Quantity | Value |
| --- | --- |
| time per pair (two games) | **5.4 s** (earlier open-board runs under heavier load: 9.4 s) |
| time per move | about 220-235 ms (longest 2.1 s) |
| nodes per move | about 240,000-250,000 (about 1.1 million nodes per second per core) |
| game length | about 94 plies |
| pairs that score exactly 0.5 (both games split) | 24 of 48 |
| per-pair standard deviation | **0.34** (earlier open-board runs: 0.25-0.33) |

The standard deviation is higher with blocks than on an open board, so this plan uses **0.32**. Fixed-sample pairs for 80% power at 95% confidence are
`7.84 x sd^2 / gain^2`:

| True gain (share of points over 50%) | Elo | Pairs, sd 0.32 | Hours at 5.4 s/pair, shipped setting |
| --- | --- | --- | --- |
| +10 points of score (60%) | ~+70 | 80 | 0.1 |
| +5 (55%) | ~+35 | 320 | 0.5 |
| +3 (53%) | ~+21 | 890 | 1.3 |
| +2 (52%) | ~+14 | 2,000 | 3.0 |
| +1 (51%) | ~+7 | 8,000 | 12 |

A sequential test (`--sprt`) stops early. A simulation (pair sd 0.29; 400 runs each) said: to tell "no difference" from "+20 Elo", about 400 pairs on average; from
"+40 Elo", about 115. At sd 0.32 these grow by about 20%.
**Realistic limit: gains below about +20 Elo (53%) are not confirmable in a night at the shipped setting; gains below +10 Elo are not confirmable at all.**

## 6. The plan, in stages, with sizes

Everything is `--blocked 0,6`. **Screening is done at depth 3 with quiescence off (about 1 s/pair, about 5x cheaper than the shipped setting)**, confirmation at the shipped
setting. The screening cost is from an earlier run on a loaded machine (1.0 s per pair); re-measure it in phase 0.

### Phase 0: setup (about 1 hour of work, no compute)
* Add `tuning/` to `.gitignore`; write `tune.ps1` that runs one arena and appends its `RESULT` line to `tuning/ledger.csv`. (The `RESULT` line, fingerprints, block range
  and seed are already in the output.)
* Free the machine (no training job). Re-measure the cost table of section 5 once.

### Phase 1: sanity (about 30 minutes)
* Baseline against itself at `--blocked 0,6`, 100 pairs, shipped setting: must be exactly 50.0% with sd 0.
* `params=file` with the base values against none: must be exactly 50.0%.
* Check that Unity's parallel root search gives the same strength as the arena's serial search (Unity passes `DisableParallelRootSearch = useMLForMainGame`, so for the normal
  heuristic game it is **false**, i.e. parallel root search is on). A one-line spec key is needed (`parallel=true`), then 100 pairs against the serial engine. If it differs,
  tune against the parallel setting.
Rule: continue only if the first two are exactly 50.0%.

### Phase 2: the midgame family, screening (about 4-6 hours)
For each priority-1 parameter in each direction, and one joint move of the mobility family:
* `arena --p1 "classic:depth=3,quiescence=false,<change>" --p2 "classic:depth=3,quiescence=false" --blocked 0,6 --pairs 600 --sprt 0,20 --minPairs 60 --seed S`
* A new seed per candidate. About 12 candidates, each about 300-600 pairs, about 1-2 s per pair at this setting: **about 4-6 hours.** Stop early if the first six all say
  "no difference" (that is the likely outcome).
* A candidate is **promising** if the SPRT accepts "+20 Elo" (H1) or the score is above 53% after 600 pairs; **dead** if H0 is accepted.

### Phase 3: confirmation (about 3 hours)
Each promising candidate against the current baseline at **the shipped setting** (depth 3, quiescence on), `--blocked 0,6`, a fresh seed, `--sprt 0,20`, up to 800 pairs
(5.4 s/pair = 1.2 hours worst case each). Accept only if the SPRT accepts H1 or the interval's lower end is above 50%.

### Phase 4: final check (about 2 hours)
The accepted set against the original at the shipped setting, 800 pairs, plus against `classic:depth=2,p.search.quiescenceMinRootDepth=2` (does the margin hold against a different
opponent?), and nodes per second and ms per move equal within 3%. Ship only if all hold; otherwise keep the defaults.

### Total, honestly
* **One night for screening, one night for confirmation, if anything is promising.** If the first six screening candidates are all neutral, **stop after about 3 hours**
  and conclude the defaults stand. Realistic expectation: stop there.
* Everything else (root bonuses, tables, search gates, difficulty levels): not planned.

## 7. Stage-specific parameters (your question: "for some stages of the game")

The evaluator already switches stage by piece count and empty squares: material and mobility weights are interpolated between 4 and 49 pieces
(`materialEarly/Late`, `mobilityEarly/Late`), and the whole mobility block switches off at `endgameEmptyThreshold`. So "different values per stage" is
**already** the structure. What is not possible with today's `EngineParams` is "a different value only in the opening" for, say, `centerControlWeight`.
If phase 2 shows that a weight matters in the midgame but hurts elsewhere, the cheap extension is a second value (`centerControlWeightLate`) and the interpolation
that material and mobility already have. That is a few lines in `CompiledEngineParams`, but it is not worth building before phase 2 says it is needed.

**About your tactic observation (blocking the opponent's moves):** the strongest evidence for it would be a direct measurement, not a regression: take
positions where the engine's chosen move leaves the opponent with fewer legal moves than the alternatives, and check whether those moves are the ones that
win. That can be done with `compare-moves` plus a small analysis of the log. It would show whether the mobility terms already capture the tactic or whether a **new
term** (for example "opponent has no moves next turn", which the game rules reward by a win) is missing. Today a stuck player loses at the terminal check, but
the leaf evaluator only sees mobility as a weighted difference, not as "zero means loss". A **new feature is a code change, not a parameter**, and is worth a
separate experiment after phase 2.

## 8. What makes a result trustworthy

* A result needs a ledger row, a seed and a named baseline file. A run not in the ledger did not happen.
* A new seed for every confirmation, or the lucky openings are reused.
* With 12 screening candidates at a 5% error rate, about one will look good by chance; phase 3 exists for that.
* If the interval contains 50%, the answer is "no evidence", not "slightly better".
* Equal depth is not equal cost: anything that changes nodes per second (search gates) must be compared at equal time (`time=` and `--parallel 1`).
* Self-play strength against the previous version is not the same as a better experience for a human player.

## 9. Questions that still change the plan

1. Do you want the trapping idea (section 7) investigated before tuning, since it could be worth more than any weight?
2. Is the machine free of other jobs for the tuning nights? All timings above were measured with another job running.
