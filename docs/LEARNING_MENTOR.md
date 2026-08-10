# Learning Mentor

The optional Learning Mentor works with GitHub Copilot CLI, OpenAI Codex, and
Claude Code. Clone with `--recurse-submodules` (or run
`git submodule update --init --recursive`), install Python 3 for the Mentor
infrastructure, then start your AI client from the repository root and select
the `learning-mentor` agent. Python is not required by the Azure course itself.

Progress is structured SQLite data stored outside this repository at
`$LEARNING_MENTOR_DB`, `$XDG_DATA_HOME/learning-mentor/state.sqlite3`, or
`~/.local/share/learning-mentor/state.sqlite3`. It contains progress for all
Mentor-enabled courses, but not source code or conversations.

## Transfer progress between machines

Close all Mentor sessions on machine A, then run:

```bash
mkdir -p .learning-mentor-transfer
python3 .agents/skills/guided-learning/scripts/learning_state.py backup \
  --output .learning-mentor-transfer/state.sqlite3
```

Transfer that private file securely to machine B and restore it there:

```bash
python3 .agents/skills/guided-learning/scripts/learning_state.py restore \
  --input .learning-mentor-transfer/state.sqlite3 --replace
```

Restore replaces, rather than merges, all local Mentor state. When state exists,
`--replace` first creates a timestamped recovery database beside it. Never
commit transfer files; delete the transferred copy after verification.
