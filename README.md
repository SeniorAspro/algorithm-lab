# Algorithm Lab

A journaled series of algorithm and simulation projects built in Unity, from a few hours long to a few weeks long. Each project is its own Unity project with a README that records what I built, what broke and what I learned.

## Projects

| # | Project | Core idea | Size | Status |
|---|---------|-----------|------|--------|
| 01 | Game of Life | Cellular automata | Hours | 🟡 In progress |
| 02 | Maze + Solver Race | Recursive backtracking, BFS / A* | Hours | ⚪ Planned |
| 03 | Boids | Flocking, emergent behavior | Hours | ⚪ Planned |
| 04 | Falling Sand | Cellular automata with materials | A day | ⚪ Planned |
| 05 | Tower Defense | A* / flow fields | 1–2 weeks | ⚪ Planned |
| 06 | Evolution Simulation | Natural selection, predator–prey | 2–3 weeks | ⚪ Planned |
| 07 | Snake: Neuroevolution | Neural nets + genetic algorithm (from scratch) | 1–2 weeks | ⚪ Planned |
| 08 | Snake: ML-Agents | Reinforcement learning (PPO) | 1–2 weeks | ⚪ Planned |
| 09 | Wave Function Collapse | Constraint-based procedural buildings | 2–3 weeks | ⚪ Planned |

Status: ⚪ Planned · 🟡 In progress · 🟢 Done

## Repo layout

```
algorithm-lab/
├── README.md                ← this file (the index)
├── JOURNAL_TEMPLATE.md      ← copy into each new project's README
├── .gitignore               ← Unity ignores, works for nested projects
└── 01-game-of-life/         ← one Unity project per folder
    ├── README.md            ← project journal
    ├── Assets/
    ├── Packages/
    └── ProjectSettings/
```

Only `Assets/`, `Packages/` and `ProjectSettings/` are committed for each project; Unity regenerates everything else.

I'm writing every project myself to learn. The `reference` branch holds an earlier version written with help, kept only to compare against after finishing my own.

## Starting a new project

1. Create a new Unity project (Unity 6, 2D or 3D template) **inside** this repo folder, named like `02-maze-solver`.
2. Copy `JOURNAL_TEMPLATE.md` into it as `README.md`.
3. Add it to the table above.
4. Commit early, commit often. Small commits like "add BFS visualization" make the history part of the journal.

## Built with

- Unity 6
- C#
