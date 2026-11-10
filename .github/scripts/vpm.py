#!/usr/bin/env python3
"""Publish the packages under Packages/ as a VPM (VCC / ALCOM) listing.

  vpm.py release            GitHub release for every Packages/<name> whose version has none yet
  vpm.py listing --out DIR  DIR/index.json (the VPM listing), DIR/index.html and DIR/<name>/CHANGELOG.md,
                            from all releases

Runs in GitHub Actions with GH_TOKEN and GITHUB_REPOSITORY set, through the runner's `gh` CLI, in a checkout
with full history and every release tag. Each release carries the package zip plus a `package.json` asset that
is the exact listing entry (manifest + url + zipSHA256), so building the listing never re-downloads zips.

Changelogs come from commit messages: a version lists the commits touching Packages/<name>/ since the
previous released version, with a leading "<displayName> [<version>]:" dropped from the subject. That list
is the version's release notes, and every listing entry gets a `changelogUrl` to the package's CHANGELOG.md
unless its package.json sets one.
"""
import argparse
import hashlib
import html
import json
import os
import re
import subprocess
import sys
import tempfile
import textwrap
import zipfile
from pathlib import Path
from urllib.parse import quote

ROOT = Path(__file__).resolve().parents[2]
PACKAGES = ROOT / "Packages"
CONFIG = ROOT / "listing.json"
ZIP_DATE = (1980, 1, 1, 0, 0, 0)


def gh(*args, check=True):
    return subprocess.run(["gh", *args], check=check, text=True, capture_output=True)


def git(*args):
    return subprocess.run(["git", "-C", str(ROOT), *args],
                          check=True, text=True, encoding="utf-8", capture_output=True).stdout


def commit_of(ref):
    """Commit hash of ref, or None when the checkout does not have it."""
    result = subprocess.run(["git", "-C", str(ROOT), "rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}"],
                            text=True, capture_output=True)
    return result.stdout.strip() if result.returncode == 0 else None


def release_tags():
    return [release["tagName"] for release in
            json.loads(gh("release", "list", "--limit", "1000", "--json", "tagName").stdout)]


def repository():
    return os.environ["GITHUB_REPOSITORY"]


def tag_for(manifest):
    return f"{manifest['name']}-v{manifest['version']}"


def previous_release(manifest, tags):
    """Tag of the highest released version below manifest's, or None for a package's first release."""
    prefix = f"{manifest['name']}-v"
    older = [tag[len(prefix):] for tag in tags
             if tag.startswith(prefix) and version_key(tag[len(prefix):]) < version_key(manifest["version"])]
    return f"{prefix}{max(older, key=version_key)}" if older else None


def changes(manifest, end, start):
    """Markdown list of the commits in start..end (all of end's history when start is None) touching the package."""
    log = git("log", "--no-merges", "--format=%H%x1f%s%x1f%b%x1e",
              f"{start}..{end}" if start else end, "--", f"Packages/{manifest['name']}")
    prefix = re.compile(rf"^{re.escape(manifest['displayName'])}(\s+v?\d[\w.+-]*)?\s*:\s*", re.IGNORECASE)
    items = []
    for record in filter(None, (record.strip() for record in log.split("\x1e"))):
        sha, subject, body = record.split("\x1f")
        subject = prefix.sub("", subject.strip(), count=1)
        item = f"- {subject[:1].upper()}{subject[1:]} ([`{sha[:7]}`](https://github.com/{repository()}/commit/{sha}))"
        if body.strip():
            item += "\n\n" + textwrap.indent(body.strip(), "  ")
        items.append(item)
    return "\n\n".join(items)


def changelog(versions):
    """CHANGELOG.md of one package, newest version first, from its listing entries (version -> entry)."""
    sections = []
    previous = None
    for version in sorted(versions, key=version_key):
        tag = tag_for(versions[version])
        commit = commit_of(tag)
        if commit is None:
            print(f"::warning::tag {tag} is not in the checkout; {version} is left out of the changelog.")
            continue
        notes = changes(versions[version], commit, previous) or "No commits touched the package."
        date = git("log", "-1", "--format=%cs", commit).strip()
        sections.append(f"## {version} ({date})\n\n{notes}\n")
        previous = commit
    latest = versions[max(versions, key=version_key)]
    return f"# {latest['displayName']} changelog\n\n" + "\n".join(reversed(sections))


def local_packages():
    for manifest_path in sorted(PACKAGES.glob("*/package.json")):
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        for field in ("name", "displayName", "version"):
            if not manifest.get(field):
                sys.exit(f"{manifest_path}: missing \"{field}\"")
        if manifest["name"] != manifest_path.parent.name:
            sys.exit(f"{manifest_path}: \"name\" {manifest['name']!r} must equal its folder name")
        yield manifest_path.parent, manifest


def package_files(package_dir):
    """Archive path -> bytes for every file of the package; the package root is the zip root."""
    return {
        path.relative_to(package_dir).as_posix(): path.read_bytes()
        for path in sorted(package_dir.rglob("*"))
        if path.is_file()
    }


def write_zip(files, dest):
    """Fixed timestamps and order, so the same files always produce the same zip."""
    with zipfile.ZipFile(dest, "w") as archive:
        for name, data in files.items():
            info = zipfile.ZipInfo(name, ZIP_DATE)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            archive.writestr(info, data)
    return hashlib.sha256(dest.read_bytes()).hexdigest()


def download(tag, pattern, directory):
    """Path of the release asset matching pattern, or None when the release has none."""
    result = gh("release", "download", tag, "--pattern", pattern, "--dir", str(directory), check=False)
    if result.returncode != 0:
        return None
    matches = sorted(Path(directory).glob(pattern))
    return matches[0] if matches else None


def cmd_release(_args):
    commit = os.environ["GITHUB_SHA"]
    dist = ROOT / "dist"
    tags = release_tags()
    for package_dir, manifest in local_packages():
        tag = tag_for(manifest)
        files = package_files(package_dir)

        if gh("release", "view", tag, check=False).returncode == 0:
            with tempfile.TemporaryDirectory() as tmp:
                released = download(tag, "*.zip", tmp)
                if released is None:
                    print(f"::warning::{tag} exists without a zip asset; delete that release to republish it.")
                    continue
                with zipfile.ZipFile(released) as archive:
                    unchanged = {n: archive.read(n) for n in archive.namelist()} == files
            if not unchanged:
                print(f"::warning file=Packages/{manifest['name']}/package.json::"
                      f"{manifest['name']} changed since {manifest['version']} was released; "
                      f"bump \"version\" to publish the change.")
            continue

        out = dist / tag
        out.mkdir(parents=True, exist_ok=True)
        zip_path = out / f"{manifest['name']}-{manifest['version']}.zip"
        digest = write_zip(files, zip_path)
        entry = dict(manifest,
                     url=f"https://github.com/{repository()}/releases/download/{tag}/{zip_path.name}",
                     zipSHA256=digest)
        entry_path = out / "package.json"
        entry_path.write_text(json.dumps(entry, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

        previous = previous_release(manifest, tags)
        start = commit_of(previous) if previous else None
        if previous and start is None:
            print(f"::warning::tag {previous} is not in the checkout; {tag} notes cover the whole history.")
        gh("release", "create", tag, str(zip_path), str(entry_path),
           "--target", commit,
           "--title", f"{manifest['displayName']} {manifest['version']}",
           "--notes", changes(manifest, commit, start) or manifest.get("description", ""))
        print(f"Released {tag}")


def version_key(version):
    core, _, prerelease = version.partition("-")
    numbers = tuple(int(part) if part.isdigit() else 0 for part in core.split("+")[0].split("."))
    return numbers, prerelease == "", prerelease


def cmd_listing(args):
    config = json.loads(CONFIG.read_text(encoding="utf-8"))
    owner, repo = repository().split("/")
    listing = {
        "name": config["name"],
        "id": config["id"],
        "url": config.get("url") or f"https://{owner.lower()}.github.io/{repo}/index.json",
        "author": config["author"],
        "packages": {},
    }

    for tag in release_tags():
        with tempfile.TemporaryDirectory() as tmp:
            entry_path = download(tag, "package.json", tmp)
            entry = json.loads(entry_path.read_text(encoding="utf-8")) if entry_path else None
        if not entry or "url" not in entry:
            print(f"::warning::{tag} has no listing package.json asset; left out of the listing.")
            continue
        listing["packages"].setdefault(entry["name"], {"versions": {}})["versions"][entry["version"]] = entry

    out = Path(args.out)
    base = listing["url"].rsplit("/", 1)[0]
    for name, package in listing["packages"].items():
        (out / name).mkdir(parents=True, exist_ok=True)
        (out / name / "CHANGELOG.md").write_text(changelog(package["versions"]), encoding="utf-8")
        for entry in package["versions"].values():
            entry.setdefault("changelogUrl", f"{base}/{quote(name)}/CHANGELOG.md")

    out.mkdir(parents=True, exist_ok=True)
    (out / "index.json").write_text(json.dumps(listing, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    (out / "index.html").write_text(render_page(listing), encoding="utf-8")
    print(f"Listed {sum(len(p['versions']) for p in listing['packages'].values())} versions "
          f"of {len(listing['packages'])} packages at {listing['url']}")


def render_page(listing):
    url = listing["url"]
    rows = []
    for name, package in sorted(listing["packages"].items()):
        versions = sorted(package["versions"], key=version_key, reverse=True)
        latest = package["versions"][versions[0]]
        changelog_link = (f"<br><a href=\"{html.escape(latest['changelogUrl'])}\">Changelog</a>"
                          if latest.get("changelogUrl") else "")
        rows.append(
            "<tr>"
            f"<td><strong>{html.escape(latest.get('displayName', name))}</strong><br><code>{html.escape(name)}</code></td>"
            f"<td>{html.escape(latest.get('description', ''))}</td>"
            f"<td><a href=\"{html.escape(latest['url'])}\">{html.escape(versions[0])}</a>"
            f"{f'<br><small>{len(versions)} versions</small>' if len(versions) > 1 else ''}"
            f"{changelog_link}</td>"
            "</tr>")
    body = "\n".join(rows) or "<tr><td colspan=\"3\">No packages released yet.</td></tr>"
    title = html.escape(listing["name"])
    return f"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{title}</title>
<style>
  body {{ font-family: system-ui, sans-serif; max-width: 960px; margin: 2rem auto; padding: 0 1rem; color: #e6e6e6; background: #1b1b1f; }}
  a {{ color: #8ab4ff; }}
  .add {{ display: inline-block; padding: .6rem 1.1rem; border-radius: 6px; background: #3b6ef5; color: #fff; text-decoration: none; font-weight: 600; }}
  code {{ background: #2a2a31; padding: .1rem .35rem; border-radius: 4px; }}
  table {{ width: 100%; border-collapse: collapse; margin-top: 1.5rem; }}
  th, td {{ text-align: left; vertical-align: top; padding: .6rem; border-bottom: 1px solid #33333b; }}
</style>
</head>
<body>
<h1>{title}</h1>
<p><a class="add" href="vcc://vpm/addRepo?url={quote(url, safe='')}">Add to VCC / ALCOM</a></p>
<p>Or add this repository URL by hand: <code>{html.escape(url)}</code></p>
<table>
<thead><tr><th>Package</th><th>Description</th><th>Latest</th></tr></thead>
<tbody>
{body}
</tbody>
</table>
</body>
</html>
"""


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("release").set_defaults(run=cmd_release)
    listing = commands.add_parser("listing")
    listing.add_argument("--out", required=True)
    listing.set_defaults(run=cmd_listing)
    args = parser.parse_args()
    args.run(args)


if __name__ == "__main__":
    main()
