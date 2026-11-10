# Zoey's Addons

Unity editor tools for VRChat avatar projects, published as a VPM repository for VCC and ALCOM.

**Add the repository:** open <https://notzoeydev.github.io/ZoeyAddons/> and press *Add to VCC / ALCOM*, or add
`https://notzoeydev.github.io/ZoeyAddons/index.json` under Settings → Packages in either app.

| Package | What it does |
|---|---|
| `dev.zoey.unused-asset-finder` | Finds materials and textures under `Assets/` that aren't used in any of the chosen scenes. *Tools → Unused Asset Finder.* |

Each package's changelog is built from the commit messages that touched `Packages/<name>/` between its releases, so
write them for users: a leading `<Display Name> [<version>]:` is dropped from the subject, and the body is kept. It is
the release's notes on GitHub and is published as a rendered page at `https://notzoeydev.github.io/ZoeyAddons/<name>/`
(raw markdown at `<name>/CHANGELOG.md`), which every listed version points to with `changelogUrl` (ALCOM links it
from the package).

