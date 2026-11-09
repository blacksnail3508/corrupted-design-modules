# corrupted-design-modules

CorruptedDesign Unity modules, installable into any project.

## Use

1. Copy `CorruptedDesignRegistry.cs` into any Unity 6 project, in a folder named `Editor` (for example `Assets/Editor/`).
2. Open **CorruptedDesign / Registry** and press **Refresh**.
3. Install, update or uninstall modules. Dependencies, Unity packages and the CorruptedDesign Hub come along automatically.

Odin Inspector, DOTween and the Spine runtime are not in this repository (paid or separately licensed): import them first, the installer tells you when one is missing.

The window reads `catalog.json` from this repository. For a private repository, paste a GitHub token with read access into the window.

## Repository layout

| Path | What |
|---|---|
| `CorruptedDesignRegistry.cs` | The installer, one file. |
| `catalog.json` | Modules, versions, dependencies, hashes. Generated. |
| `modules/ID-VERSION.zip` | One zip per module version. Generated. |

Both generated parts come from the template project: **CorruptedDesign / Registry Tools / Publish To Folder...**. Bump `version` in a module's `corrupteddesign.module.json` before publishing a change; the publisher refuses to overwrite a published version.
