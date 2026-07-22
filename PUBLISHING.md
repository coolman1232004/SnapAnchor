# Publishing SnapAnchor on GitHub

This checklist keeps the source repository small, protects local development data, and distributes the installer and portable package in the normal GitHub way.

## 1. License

SnapAnchor is published under the MIT License. Keep the repository's `LICENSE` file with source distributions and retain required third-party notices in binary packages.

## 2. Publish the correct files

Commit:

- Application source (`*.cs`, `*.xaml`, project files)
- `Assets`, `Controls`, `Models`, `Resources`, `Services`, and `Windows`
- Test source under `Tests`
- Packaging source under `packaging`
- OCR language data under `tessdata`
- README, changelog, third-party notices, contribution/security files, and the chosen license

Do not commit:

- `backups/`
- `bin/`, `obj/`, `dist/`, `dist-*`, or `package-smoke/`
- `packaging/staging/` or other generated package folders
- Local settings, history, sessions, screenshots, recordings, logs, or crash dumps
- Signing certificates or private keys (`*.pfx`, `*.p12`, `*.key`, `*.pem`)
- Personal access tokens, passwords, or API keys

The repository `.gitignore` already excludes these paths. Keep the existing local backup folders outside Git; they are rollback archives, not public source history.

## 3. Create the repository

Create an empty GitHub repository named `SnapAnchor` (or another name after a trademark/name check). Do not ask GitHub to generate a README or license if local versions already exist.

From the SnapAnchor source folder:

```powershell
git init
git branch -M main
git add .
git status
git commit -m "Initial public release of SnapAnchor"
git remote add origin https://github.com/YOUR-NAME/SnapAnchor.git
git push -u origin main
```

Before committing, carefully review `git status` and confirm that no backup, build-output, certificate, user-data, or private-media path is staged.

## 4. Create a release for downloads

Do not commit the compiled ZIP or installer into Git history. GitHub blocks ordinary repository files larger than 100 MiB, and the SnapAnchor installer is larger than that. Use a GitHub Release instead.

1. Build and test the chosen version:

   ```powershell
   .\Tests\Run-Smoke.ps1
   .\packaging\Build-Packages.ps1 -Version 2.1.4
   ```

2. Create a version tag such as `v2.1.4` and a matching release title.
3. Upload these files from `dist\` as release assets:
   - `SnapAnchor-Setup-win-x64.exe`
   - `SnapAnchor-Portable-win-x64.zip`
   - `release.json`
4. State clearly that the build is currently unsigned and may trigger Windows SmartScreen.
5. Describe major changes, known limitations, minimum Windows version, and upgrade notes.

Example release notes:

```markdown
## SnapAnchor 1.2.2

### Highlights
- Local-first screenshot capture, inline annotation, pins, OCR, and screen recording
- Clear Pause/Stop recording controls and Play/Discard/Trim review actions
- Searchable history, pin groups, configurable toolbars, and mixed-DPI support

### Downloads
- **Installer:** `SnapAnchor-Setup-win-x64.exe`
- **Portable:** `SnapAnchor-Portable-win-x64.zip`

### Requirements
- Windows 10 version 1809 or later, x64

### Security note
This release is not code-signed. Verify the SHA-256 values in `release.json` before running it.
```

## 5. Configure the GitHub page

- Add the description: `Local-first screenshot, annotation, OCR, pinning, and recording tool for Windows.`
- Add topics: `screenshot`, `screen-capture`, `annotation`, `ocr`, `screen-recorder`, `windows`, `wpf`, `dotnet`, `productivity`, `local-first`.
- Enable Issues for bug reports and feature requests.
- Enable Discussions only if you want to maintain a community forum.
- Add repository social preview artwork after the name and visual identity are final.
- Enable Dependabot alerts and secret scanning where available.
- Protect `main` after the initial push if other contributors will have write access.

## 6. Legal and identity checks

- Keep SnapAnchor's name, icon, screenshots, wording, and toolbar artwork distinct from commercial products.
- Do not upload the downloaded Snipaste portable folder, binaries, configuration, screenshots used as reference, or any Snipaste/PixPin assets.
- Avoid advertising SnapAnchor as an “exact clone,” official edition, cracked version, or replacement containing paid/proprietary features.
- Describe public behaviour factually and say that SnapAnchor is independent and unaffiliated.
- Run a proper trademark/name search before a broad public launch; a normal web search is not a legal clearance search.
- If commercial distribution, donations, or a large user base are planned, obtain professional advice on licensing, trademarks, privacy, and software distribution.

## 7. Before every release

- Run the complete regression suite.
- Test install, upgrade, uninstall, and portable startup on a clean Windows account or virtual machine.
- Scan release files with current security software.
- Verify file versions and SHA-256 hashes.
- Confirm OCR language data and all required native DLLs are present.
- Confirm no personal screenshots, recordings, paths, tokens, logs, or configuration are packaged.
- Update the README version badge, changelog, release notes, and tag.
- Prefer a code-signing certificate before promoting the installer to a broad audience.
