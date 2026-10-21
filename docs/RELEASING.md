# Releasing SDE for Mac

Releases are built by GitHub Actions. Pushing a tag that starts with `v` runs `.github/workflows/release.yml`, which builds
the app for Apple Silicon and Intel Macs and attaches the zips to a **draft** GitHub release. Nothing is public until you
open the draft, check it and press Publish.

## Steps

1. Set `<Version>` in `src/Sde.App/Sde.App.csproj` (for example `2.0.0`) and commit. The workflow stops if the tag and this
   number disagree, because the zip names come from the project file.
2. Make sure CI (`.github/workflows/ci.yml`) is green on that commit.
3. Tag and push: `git tag v2.0.0 && git push origin v2.0.0`.
4. Wait for the Release workflow. It produces, on a draft release named "SDE for Mac 2.0.0":
   - `SDE-2.0.0-osx-arm64.zip` (Apple Silicon)
   - `SDE-2.0.0-osx-x64.zip` (Intel)
   - `SHA256SUMS`
5. Open the draft, read the generated notes, add anything the commit log does not say, and publish. A tag with a hyphen
   (`v2.1.0-beta1`) is marked as a pre-release.
6. Re-running the workflow for the same tag replaces the files on the existing release.

## What the workflow does

1. Builds both architectures with `bash tools/packaging/package_app.sh osx-arm64 osx-x64` (self-contained .NET publish,
   `SDE.app`, icon, `Info.plist`, ad-hoc signature, zip).
2. Runs `tools/packaging/build_mdbtools.sh osx-arm64 osx-x64`, which builds mdbtools' `mdb-export` and `mdb-tables`
   from the pinned 1.0.1 source and puts them in `SDE.app/Contents/MacOS/mdbtools/`.
3. Runs `tools/packaging/sign_app.sh`, which signs the bundle (Developer ID when the secrets below exist, ad hoc otherwise),
   notarizes it when the notary secrets exist, and writes the zips again so they contain the final signed app.
4. Writes `SHA256SUMS`, creates the draft release and keeps the zips as a workflow artifact too.

The `macos-latest` runner is an Apple Silicon Mac. It builds the Intel app by cross-publishing, so the Intel zip is not
launched on the runner. The mdbtools script cross-compiles the Intel programs with `clang -arch x86_64` and needs only
the Xcode command line tools: mdbtools is built with its own small GLib replacement and a static `libmdb`, so the two
programs link nothing but macOS's `libiconv` and `libSystem`. It runs them on the test database when the Mac can
(Intel builds need Rosetta on Apple Silicon).

To build a release on your own Mac without signing:

```
bash tools/packaging/package_app.sh osx-arm64 osx-x64
bash tools/packaging/build_mdbtools.sh osx-arm64 osx-x64
bash tools/packaging/sign_app.sh osx-arm64 osx-x64
```

## Signing and notarization (optional)

Without any secrets the release still builds, with an ad-hoc signature. Mac users then have to open the app with
right-click, Open (or `xattr -d com.apple.quarantine`) the first time, because Gatekeeper cannot verify the developer.
With the secrets below the app is signed with a Developer ID Application certificate using the hardened runtime
(`tools/packaging/entitlements.plist`), notarized by Apple and stapled, so it opens normally.

The release job runs in an environment named `release`. The safest place for these secrets is that environment
(Settings, Environments, `release`, with its deployment rule limited to tags matching `v*`), so that no other workflow,
branch or pull request can read them. Repository secrets (Settings, Secrets and variables, Actions) work too.

| Secret | What it is |
| --- | --- |
| `SDE_SIGN_IDENTITY` | The name of the certificate, for example `Developer ID Application: Your Name (TEAMID1234)`. `security find-identity -v -p codesigning` on your Mac lists it. |
| `SDE_SIGN_P12_BASE64` | The certificate and its private key exported from Keychain Access as a `.p12`, then base64-encoded: `base64 -i DeveloperID.p12 \| pbcopy`. |
| `SDE_SIGN_P12_PASSWORD` | The password you chose when exporting the `.p12`. |
| `SDE_NOTARY_APPLE_ID` | The Apple ID email used for the developer account. |
| `SDE_NOTARY_TEAM_ID` | The 10-character team ID (developer.apple.com, Membership details). |
| `SDE_NOTARY_PASSWORD` | An app-specific password for that Apple ID (appleid.apple.com, Sign-In and Security, App-Specific Passwords). Not the account password. |

The three `SDE_SIGN_*` secrets together turn on signing. The three `SDE_NOTARY_*` secrets turn on notarization, and only
take effect when signing is on. If any secret in a group is missing, that step is skipped rather than failing the release.

The workflow imports the `.p12` into a temporary keychain that exists only for the run and is deleted at the end. The
secrets are not printed in the log.

To sign and notarize on your own Mac instead, export the same variables (`SDE_SIGN_IDENTITY` and the three
`SDE_NOTARY_*`, using the certificate already in your keychain) and run:

```
bash tools/packaging/package_app.sh osx-arm64 osx-x64
bash tools/packaging/build_mdbtools.sh osx-arm64 osx-x64
bash tools/packaging/sign_app.sh osx-arm64 osx-x64
```

## Checking a release

- `shasum -a 256 -c SHA256SUMS` in the download folder.
- `codesign --verify --deep --strict --verbose=2 SDE.app` after unzipping; for a notarized build also
  `spctl --assess --type execute --verbose SDE.app` (it should say "accepted, source=Notarized Developer ID").
- Open the app and import a FEMA `.mdb` database to confirm the bundled mdbtools programs run from inside the app.
