# Valheim Exploration Fixed

[Exploration](https://github.com/blaxxun-boop/Exploration) by **blaxxun**, adapted to the current
Valheim build.

## Why this exists

The upstream mod has not been adapted to Valheim 1.0 yet, and the released 1.0.4 build stops
working in three separate ways:

- It patches `Container.RPC_OpenRespons`, which 1.0 renamed to `RPC_OpenResponse`. The patch fails,
  and because HarmonyX does not catch a failing patch, every patch declared after it is skipped -
  including the wishbone radius patch and SkillManager's `IsSkillValid`. Custom skill levels are
  then thrown away by `Skills.Load()`, so the Exploration skill resets to zero on every load and
  the mod appears to do nothing at all.
- Valheim 1.0 added a `bool log` parameter to `Character.Message`, so the released build throws
  `MissingMethodException` on every one of those messages.
- Several patch methods dereference `Player.m_localPlayer` without a null check, which throws on a
  dedicated server.

`build.ps1` repairs all of it, plus one genuine bug in the mod: the treasure chest multiplication
test was inverted, so it fired when the skill was *below* the configured level instead of above.
The version number stays at 1.0.4 so ServerSync keeps accepting clients that run the original build.

## What the mod does

- Adds an **Exploration** skill that levels up while you explore
- Higher skill increases your movement speed and your exploration radius
- Increases the wishbone and beacon radius with your skill
- Exploration level requirements for writing and reading the cartography table
- A chance to multiply the contents of treasure chests at higher levels
- Configurable skill experience gain and loss
- Can be installed on a server to enforce the configuration

## Upstream

The mod itself is entirely the work of **blaxxun**:
<https://github.com/blaxxun-boop/Exploration>

This repository only carries the adaptation to Valheim 1.0.
