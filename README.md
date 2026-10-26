# Retravion — starter workflow

This repository skeleton establishes architecture-guided incremental development **before coding**.

1. Read `AGENTS.md` once at the start of a coding session.
2. Discuss one user need using `.pi/prompts/define-need.md`.
3. After approving it, derive requirements and tests with the corresponding prompts.
4. Plan only the first feature using `.pi/prompts/plan-feature.md`.
5. Approve any domain or architectural choices required for that feature.
6. Implement one coherent increment; run tests and independent review before merge.
7. Promote a UI or coding pattern only after a canonical implementation is accepted.

The prompt files are portable prompt bodies. Depending on your Pi installation and extensions, you may invoke them as templates or paste their content into a session; verify the installed Pi prompt-template convention before relying on a slash command.

Do not automatically load all documents or all prompts into every session. Keep the planning, implementation, and review contexts separate.
