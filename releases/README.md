# Agent release notes

C-Sweet displays versioned release notes next to the update icon on Installed Agents.

For every new agent version:

1. Add `releases/<version>.md`, using the exact `version` from the root `csweet-plugin.json` (without a `v` prefix).
2. Describe user-visible changes, fixes, configuration changes, and migration steps. State breaking changes explicitly.
3. Commit the note together with the manifest and implementation version changes. Keep earlier notes in place.
4. Use UTF-8 Markdown, at most 64 KiB. Simple headings and bullet lists also read well in the plain-text popup. Do not embed HTML or secrets.

C-Sweet reads this file at the same resolved commit as the update manifest, never from a moving branch. Missing notes do not block an update. Prerelease and build metadata remain part of the filename, for example `releases/1.3.0-beta.1.md`.

The first file added for an existing version is a tracking baseline; it is not a reconstructed changelog.
