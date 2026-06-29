AI search depth is capped at 3 (by design)
The CPU opponent uses fixed‑depth alpha‑beta search with a hand‑crafted heuristic evaluation. Counter‑intuitively, depth 3 plays the strongest — depth 4, 5, and 6 play progressively worse, not better. We cap aiDepth at 3 deliberately.

Why deeper search plays worse
This is the classic horizon effect, and Ataxx makes it unusually severe. A single move can flip a whole cluster of pieces, so the board's evaluation can swing wildly from one ply to the next. With a fixed search depth, the engine can find a line that looks winning right at its search horizon — e.g. it captures a wave of pieces and appears far ahead — while the opponent's refutation (recapturing that wave) sits just one ply beyond what the search can see.

The deeper the search, the more of these "mirage" lines it finds and the more confidently it chases them, abandoning solid play for an illusion that never materializes. Concretely, in our testing a depth‑6 search scored a pointless non‑capturing jump at ~+600 "pieces‑worth" while the engine was actually behind — a position that collapsed as soon as the opponent replied. Search scores were also seen flip‑flopping by the equivalent of ±1–2 pieces on every additional ply, which is the same instability viewed from another angle.

Depth 3 sits in the sweet spot: deep enough to see immediate tactics (captures, direct threats, one‑move recaptures), but too shallow to chase the deep mirages. It consistently plays concrete, sensible, winning moves.

Why we don't "just fix" the deeper search
This behavior is inherent to fixed‑depth search with a heuristic evaluator in a high‑volatility game — it is not a bug. Making depth 6 reliably stronger than depth 3 would require a substantially heavier search (e.g. a full capture‑resolving quiescence) and/or a much more sophisticated evaluation, both of which are large efforts that risk degrading the depth‑3 play that already works well. Rather than trade away a known‑good opponent for an uncertain one, we cap at depth 3 and improve strength within depth 3 through targeted evaluation/move‑selection tuning.