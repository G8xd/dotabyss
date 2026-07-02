# Contributing

Thanks for your interest! This is an unofficial datamining/reverse-engineering project.
Please read this before opening an issue or PR.

## Ground rules (non-negotiable)

These keep the project legal and clean. PRs that break them will be closed.

1. **Never commit game content.** No audio, images, text, Live2D models, `.moc3`, bundles,
   dumps, or anything derived from *Dot Abyss X*. All of it lives under the git-ignored `data/`
   and `build/`. If `git status` shows game data, your `.gitignore` or paths are wrong — fix
   that, don't commit it.
2. **Never commit third-party code or binaries.** The Live2D Cubism SDK (`unity/Assets/Live2D/`)
   and the tools (`tools/bin/`) are git-ignored on purpose. Contributors fetch them themselves
   (see the README). Don't vendor them back in.
3. **Never commit secrets.** No keystores, tokens, or credentials. The Android debug keystore is
   generated locally (README setup).
4. **No hardcoded absolute paths.** Resolve every path through `tools/daconfig.py` /
   `config.json`. Machine-specific values go in `config.local.json` (git-ignored).

## Project shape

Two parts — see [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md):

- **`tools/`** — the Python pipeline + `da` CLI (an ETL DAG).
- **`unity/Assets/{DA_Runtime,Editor}`** — the player and its editor build tooling.

## Adding a pipeline stage

1. Add `tools/pipeline/<your_stage>.py`. Read paths from `daconfig` (`import daconfig as C`);
   never hardcode. Make it **re-runnable / skip-existing** so game updates only reprocess what
   changed.
2. Add a `cmd_<your_stage>` dispatcher + `sub.add_parser(...)` in `tools/da.py`.
3. Add a row to the `da` CLI table in `README.md`, and update `docs/ARCHITECTURE.md` if it
   changes the DAG.

## Python style & checks (Astral stack)

We use [`uv`](https://github.com/astral-sh/uv), [`ruff`](https://docs.astral.sh/ruff/), and
[`ty`](https://docs.astral.sh/ty/).

```sh
uv sync --group dev          # set up the environment + dev tools
uv run ruff format tools/    # format
uv run ruff check tools/     # lint (must pass — CI gates on this)
uv run ty check tools/       # type-check (advisory for now)
```

- `ruff format` + `ruff check` must be clean before you push. CI runs both.
- `ty` is **advisory** while the codebase is still largely untyped — improvements welcome, but
  it won't block your PR.
- Config lives in `pyproject.toml`. If you need to relax a rule, do it there with a comment
  explaining why, not with scattered `# noqa`.

## C# / Unity style

- Match the surrounding conventions in `Assets/DA_Runtime` and `Assets/Editor`.
- Keep runtime code in `DA_Runtime` and editor-only code in `Editor` (it must not ship in a
  build). Don't add `UnityEditor` references to runtime scripts.
- Unity `.meta` files are part of the change — commit them alongside their assets.

## Commits & PRs

- Small, focused PRs with a clear description of **what** and **why**.
- Note how you verified the change (which `da` command / build / scene you ran).
- Be respectful; see [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

## Reporting issues

Use the issue templates. Do **not** attach game assets or copyrighted material to issues.
