# Multi-resolution layout sheets (T312)

**Documentation images, not goldens.** Nothing here is read by the scenario runner or any CI comparison.

| Field | Value |
|---|---|
| GPU | NVIDIA GeForce RTX 5060 Ti, Vulkan 1.4.351, Godot 4.7.2 Forward+ |
| Scenario | `layout-matrix` (`Unciv.Godot.Harness/tools/layout-matrix.sh <out> [sizes]`), each screen in an off-screen viewport of the window size, OS scale 1 |
| Sizes | 1366x768, 1920x1080, 2560x1440, 3440x1440, 3840x2160 (the file name is the size; `-0` HUD/menu/city/tech/policies/diplomacy, `-1` trade/overview x3/espionage/pedia, `-2` main menu/new game/host/lobby/join/options) |
| Sheets | each PNG of the scenario downscaled to a 2x3 contact sheet (JPEG), so small text is only legible in the real run; open the scenario output for full size |
| Date | 2026-11-10 |

T333 re-recorded the sheets: a full-screen frame's content is capped at `UiTokens.MaxContentPixels` (2560 screen px) as well as at 1500 logical px, so at 3440x1440 and 3840x2160 the column is narrower and centred (the 3440x1440 and 3840x2160 `-0`, `-1` and `-2` sheets were refreshed), and the New game player rows keep the Human/AI toggle beside the nation name rather than stretching it across the column (the `-2` sheet at every size was refreshed for that). The cap reads the window in real pixels, so it does not grow with the whole automatic scale.
