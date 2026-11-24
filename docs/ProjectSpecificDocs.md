# Project docs index

Read the file for your topic before changing it. When doc and code disagree, the code wins: fix the doc in the same change.

| Doc | Read when you... | Update when... |
|---|---|---|
| [`../Readme.md`](../Readme.md) | need the layout, the player modes and the CLI | a project, mode or command changes |
| [`../engineReadme.md`](../engineReadme.md) | need the engine overview and rule decisions | an engine rule decision changes |
| [`../HEURISTIC_TUNING_PLAN.md`](../HEURISTIC_TUNING_PLAN.md) | tune heuristic parameters | a tuning plan step changes |
| [`../IMPLEMENTED-ENGINE_RULES_API_SPEC.md`](../IMPLEMENTED-ENGINE_RULES_API_SPEC.md) | need the pointer to the maintained engine API reference in AttaxShared | the reference moves |
| [`TESTING.md`](TESTING.md) | select or run tests, check C#/Python parity | a command or gate changes |
| [`productStructure.md`](productStructure.md) | look for a project or sibling repo | a project or repo moves |

The maintained `AttaxGame` API reference (abilities, credits, footprints, validation order, turn flow, fingerprints, opening generator, AI ability policy) is `..\AttaxShared\docs\IMPLEMENTED-ENGINE_RULES_API_SPEC.md`; update it in AttaxShared in the same change as any rules behavior change.
