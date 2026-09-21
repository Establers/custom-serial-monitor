# Update UI

Update controls belong to **About**, next to the installed version. **Settings**
continues to start with serial settings. A newer stable release adds a small
button to the right of the footer; it never opens a popup, steals focus, changes
the selected tab, or covers serial logs. Clicking it opens About, including when
the inspector is collapsed. Footer height stays fixed as the button appears or
disappears.

About provides a manual check button, current status, automatic-check checkbox,
and last successful check time. When an update is available, the primary action
opens its release page. Skipping a version is a secondary action in the `…` menu.
Skipped releases remain accessible in About; a manual check can reveal the footer
button for this session. The next automatic check still honors the saved skip.
Closing About needs no separate “Later” action.

## Visual preview

From the repository folder:

```powershell
.\scripts\preview_updates.ps1 -Scenario available
```

Close the preview before rebuilding. Other scenarios are `current`, `offline`,
and `checking` (30-second delay). These fixtures are Debug-only and use in-memory
update settings, without GitHub requests or changing the installed version.
The title identifies the preview. The available release is fictional v1.4.0;
use the normal build to test opening a real published release page.

Check:

- Settings opens directly on Serial, with no update section above it.
- A small inspector can show the About controls without a tall settings stack;
  still smaller panels scroll rather than clip actions.
- Manual check disables its button while pending and shows progress.
- Available: footer link opens About; release action and overflow menu fit.
- Skip: footer link disappears without shifting the log viewport; About still
  offers the release page. Manual check reveals the footer link again.
- Current: no footer badge or download actions.
- Offline: inline retry guidance, no modal dialog, and no fake success timestamp.
- Cached updates and skipped versions survive a restart of the normal build.
- Validate dark/light themes, narrow windows, and keyboard focus navigation.

The service and view-model tests cover numeric version ordering, stable releases,
daily throttling, preferences, cached results, skipping, failed refreshes, and
shutdown. Layout and interaction checks require the running app.

## Concurrent windows and responsiveness

Preferences are saved as field-specific changes, merged with the latest file
while holding an exclusive OS file handle on `updates.json.lock`. The lock file
is retained; the handle, not the file's existence, owns the lock. A crashed
process releases ownership automatically. Each transaction writes a unique
temporary file in the same directory before replacing the destination.
Background cache writes cannot overwrite another window's automatic-check or
skip choices, and older request results cannot replace a newer cached result.

Storage work runs off the UI thread, with a three-second cancellation deadline
and asynchronous lock retries. HTTP requests have a ten-second timeout, bounded
response buffering, and shutdown cancellation. Rapid checkbox changes coalesce
into one active write and one latest pending value. One update check remains
active through both network access and result persistence. No update work is
awaited by the serial pipeline or window-close path.

`UpdateConcurrencyTests` starts separate .NET worker processes to test competing
writes, lock timeout/cancellation, and a crashed lock owner. The worker links the
production storage implementation and is only a test dependency, not part of the
distributed app. View-model tests also exercise stale windows, rapid toggles,
pending HTTP requests, and pending result saves.

## Static fallback manifest

The app checks the GitHub releases API first. HTTP 429, or HTTP 403 with a rate-limit
header/message, falls back once to
`https://establers.github.io/custom-serial-monitor/updates.json`.
Ordinary 403, network failures and invalid successful API responses do not trigger
fallback. Both requests share a ten-second cancellation deadline. Responses use the
same bounded HTTP buffer and stable numeric version validation. Release links are
constructed within this repository; remote JSON cannot select an arbitrary URL.
The service remembers the API reset/retry time for its lifetime (at least one minute)
and uses Pages directly during that interval. Closing the window cancels either request.
If Pages also fails, the existing error state and last successful cache are retained.

`.github/workflows/update-manifest.yml` generates `artifacts/update-site/updates.json`
and publishes only that directory to GitHub Pages. The file contains `tag_name`,
`draft: false`, `prerelease: false` and `published_at`. It is generated rather than
manually maintained in the source tree. `scripts/build_update_manifest.ps1` queries
the currently published latest stable release using the workflow's read-only token;
no token is shipped in the desktop app or public file.

Pages must use the GitHub Actions publishing source. The workflow runs on release
publication/edit/removal, on changes to the workflow/generator on main, and via
manual dispatch. Publish the release with its download assets before making it
public. A commit or tag alone does not advertise an unreleased app version.
If another workflow creates releases using `GITHUB_TOKEN`, explicitly dispatch this
workflow as well, since those release events do not start another workflow.
For recovery, run `gh workflow run update-manifest.yml --ref main`, then confirm the
public JSON matches the latest published stable release. Deployment failures leave
the previously published file in place; Pages/CDN propagation may briefly lag.

Existing v1.4.0 binaries use only the API and must be replaced with a build containing
this fallback. Company networks must allow the Pages address too; API quota fallback
does not bypass network access policy.
