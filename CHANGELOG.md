# Changelog

## [2.1.0] - 2026-07-22

- Long scrolling capture now sends wheel input to the selected underlying window, with a system-input fallback for controls that ignore posted messages
- Long capture now reports when the selected content did not scroll instead of silently returning the original viewport
- Redesigned the dashboard, History, Preferences, capture controls, and pinned-image surfaces with a warm neutral and charcoal palette
- Replaced the application icon with a flat charcoal capture-frame and anchor design that remains legible at taskbar and tray sizes
- Added a direct **Back to SnapAnchor** action in Capture History
- Added localized English, Simplified Chinese, and Traditional Chinese strings plus regression coverage for the new behavior and layout

## [2.0.5] - 2026-07-22

- Pinned-image editing now opens with no annotation tool selected, so the pin can be dragged immediately
- Clicking the currently active annotation tool again returns to pin-move mode
- The annotation surface now moves the original pinned window and keeps its toolbar attached
- Pinned toolbars now use the same measured-width placement calculation as capture toolbars
- Added localized move-mode guidance and regression coverage for placement, movement, toggle-off tools, and lossless editing

## [2.0.4] - 2026-07-22

- Replaced the Text toolbar icon with a clean, plain upright **T** matching the supplied reference
- Replaced the Pin toolbar icon with a rounded, tilted outline thumbtack
- Kept both icons aligned to the shared 18 px vector canvas and compact toolbar states
- Verified the refreshed artwork at compact size through the rendered toolbar regression preview

## [2.0.3] - 2026-07-22

- Pinned-image **Show toolbar** now keeps the original pin as the only visible image
- The annotation layer is transparent and anchored to the original pin instead of opening a replacement image surface
- The toolbar uses the capture-style grip and stays directly beneath the pinned image
- Added an explicit **Cancel** action that closes the toolbar without changing the pin
- Added regression coverage for transparent editing, Cancel visibility, and lossless output

## [2.0.2] - 2026-07-22

- Existing portable folders now remove the obsolete previous-name executable during in-place update or the next SnapAnchor startup
- Added regression coverage for cleaning unmanaged legacy apphosts left by version 2.0.0

## [2.0.1] - 2026-07-22

- Removed the obsolete previous-name executable from the portable ZIP and extracted folder
- Pinned-image OCR now shows an I-beam only over recognized selectable words
- Restored the normal arrow pointer over non-text areas and after leaving recognized text

## [2.0.0] - 2026-07-22

- Renamed the product, executable, installer, portable package, namespaces, assets, local storage, and update channel from SnapPin to SnapAnchor
- Added one-time migration for existing settings, capture history, pinned sessions, diagnostics, and startup registration
- Rebuilt the capture and annotation toolbar around a shared 18 px WPF vector icon system
- Reduced the compact toolbar to a light 34 px surface with restrained shadow, spacing, hover, active, and disabled states
- Retained a verified compatibility bootstrap so existing portable copies can update in place to the renamed executable

All notable user-facing changes should be recorded here. Versions follow semantic versioning where practical.

## [1.2.15] - 2026-07-21

### Added

- Added an explicit **Cancel** action beside Copy and All text for OCR text selections on pinned images
- Escape now cancels the active text selection before it can close the pinned image
- Clicking empty image space clears the current OCR text selection

### Changed

- Cancelling a selection keeps OCR text-selectable mode enabled so another selection can begin immediately

### Validation

- Added a pinned-image UI regression requiring the Cancel selection control
- Full x64 build, three-language localization audit, and smoke-test suite pass with zero warnings or errors

## [1.2.14] - 2026-07-21

### Fixed

- Taskbar-visible SnapAnchor windows now explicitly supply the high-resolution SnapAnchor logo to Windows Shell
- The dashboard, Preferences, History, custom capture, and portable updater no longer fall back to a blank or generic taskbar tile

### Validation

- Added regression checks requiring valid high-resolution window icons on the dashboard and Preferences dialog
- Full x64 build, localization audit, smoke-test suite, and isolated live Windows taskbar review pass

## [1.2.13] - 2026-07-21

### Added

- Added an independent **Check for updates every day** option beside the existing startup check
- Daily checks run at most once per local calendar day and continue while SnapAnchor remains open across midnight
- Manual update checks count toward the daily schedule to avoid an immediate duplicate check
- Added Simplified Chinese and Traditional Chinese translations for the new setting

### Validation

- Added date-boundary, same-day, future-timestamp, default-setting, and Preferences-control regression checks
- Full x64 build, three-language localization audit, rendered About-page review, and smoke-test suite pass with zero warnings or errors

## [1.2.12] - 2026-07-21

### Changed

- Reduced Preferences from 680 x 540 to a compact 620 x 470 logical-pixel layout for better use on scaled and smaller displays
- Reduced tab padding, typography, button padding, field heights, margins, toolbar list sizes, and label-column widths
- Added automatic vertical scrolling to every Preferences tab and horizontal overflow scrolling to the two-column Toolbar page
- Kept Cancel and Save changes fixed at the bottom while tab content scrolls independently

### Validation

- Added a regression requiring all ten Preferences tabs to expose automatic vertical scrolling
- Rendered and reviewed every tab at 620 x 470
- Full x64 build, localization audit, and smoke-test suite pass with zero warnings or errors

## [1.2.11] - 2026-07-21

### Changed

- The default annotation toolbar now follows Snipaste's compact order: Rectangle, Arrow, Pencil, Marker, Blur, Text, and Eraser
- The default capture actions are now Cancel, Pin, Save, and Copy; other supported actions remain available from Toolbar Preferences
- Toolbar Preferences now place Annotation settings on the left and Capture settings on the right
- Existing untouched toolbar defaults automatically migrate to the new layout while custom layouts are preserved

### Validation

- Added exact default-order and settings-column regression checks
- Full x64 build, rendered toolbar review, localization audit, and smoke-test suite pass with zero warnings or errors

## [1.2.10] - 2026-07-21

### Changed

- Capture History now follows SnapAnchor's cool light design instead of the legacy dark window
- Added a spacious header with distinct teal, sky-blue, and coral actions
- Consolidated searching, source/type/date filters, favourites, and recycle-bin controls into a rounded white panel
- Expanded history cards into a responsive three-column layout with larger previews, clearer metadata, rounded surfaces, and pastel action buttons

### Validation

- Added a rendered 1120 x 760 History layout regression with representative three-card content
- Full x64 build, localization audit, history behavior checks, and smoke-test suite pass with zero warnings or errors

## [1.2.9] - 2026-07-21

### Fixed

- Portable updates now retry with administrator permission when Windows unexpectedly denies replacement of an existing application file
- The elevated retry is offered only after the previous version was restored successfully, preventing unsafe retry loops
- Portable update failures now identify whether backup, replacement, or obsolete-file removal failed and name the affected package file

### Validation

- Added regression coverage for safe access-denied retry classification
- Full x64 build, localization audit, updater transaction tests, and smoke-test suite pass with zero warnings or errors

## [1.2.8] - 2026-07-20

### Added

- About page indicator showing whether the running copy is **Portable** or **Installed**
- One-click **Copy version information** action including SnapAnchor version, edition, and process architecture

### Validation

- Added About-page edition and copy-summary regression coverage
- Full x64 build, localization audit, and smoke-test suite pass with zero warnings or errors
- This intentionally small release provides an end-to-end update target for testing the redesigned 1.2.7 portable updater

## [1.2.7] - 2026-07-20

### Added

- Quiet startup update notification with a persistent tray action
- Release-details window showing version, edition, download size, notes, and GitHub link
- Downloaded-size, speed, and estimated-time progress
- Separate **Restart and update** / **Later** decision after verification and extraction
- Visible portable updater with replacement progress and post-update confirmation
- Managed portable-package manifest for safe removal of obsolete application files

### Changed

- Verified downloads and staged portable files are reused after choosing **Later**
- Portable replacement is performed by SnapAnchor's verified staged executable instead of a hidden PowerShell script
- Only the two newest portable rollback copies are retained

### Fixed

- Portable update failures now restore overwritten, deleted, and newly created managed files transactionally
- User-created files beside the portable application are excluded from managed-file cleanup
- Read-only folders trigger elevation before replacement, and insufficient disk space is detected before files change

### Validation

- Successful portable replacement and deliberately failed version validation both covered by real filesystem tests
- Complete x64 release build, localization audit, and smoke-test suite pass with zero warnings or errors

## [1.2.6] - 2026-07-20

### Added

- Cancellable update-download window with percentage, progress status, and a protected preparation phase
- Automated localization-resource, update-dialog, mixed-DPI, portrait-display, remote-session, audio-device, and long-capture tests
- Display/session compatibility details in copied diagnostics

### Changed

- English, Simplified Chinese, and Traditional Chinese text now lives in standard `.resx` resource files
- Capture, annotation, and pinned-image window logic is split into focused partial classes for safer maintenance
- Stale recording-device selections now fall back to the Windows default device

### Fixed

- Remaining rare error and recovery messages are localized
- Tiny or heavily scaled monitor work areas no longer inherit an oversized artificial minimum
- Update downloads can be cancelled safely before installation or portable replacement begins

### Validation

- 481 resource keys aligned across all three interface languages and 425 UI strings audited
- Full x64 release build and smoke-test suite pass with zero warnings or errors

## [1.2.5] - 2026-07-15

### Added

- Application-wide Simplified Chinese and Traditional Chinese interfaces
- Localized tray menus, pin context menus, capture/annotation tools, OCR, recording, history, and update messages
- A localized **Restart now** / **Later** dialog after changing the interface language

### Fixed

- Preferences no longer translates only its tabs while leaving the page content in English
- Traditional Chinese conversion no longer risks an extra character at the end of a window title
- Language changes restart through the single-instance handoff without racing the previous process

### Validation

- Every visible static XAML label is covered by the localization dictionary
- Simplified and Traditional Chinese control tests, visual dashboard/Preferences checks, release build, and full x64 smoke-test suite pass

## [1.2.4] - 2026-07-15

### Added

- True in-place updates for portable SnapAnchor copies
- Automatic portable-package selection, SHA-256 verification, rollback backup, and restart
- Friendly link to the SnapAnchor GitHub page in Preferences

### Changed

- Installed copies continue to update through the guided Windows installer
- The internal `release.json` address is no longer displayed or editable
- Existing custom or empty update-source settings migrate to the official GitHub release channel

### Validation

- Installed/portable mode routing and GitHub asset URL regression coverage
- Release build and complete x64 smoke-test suite pass

## [1.2.3] - 2026-07-15

### Fixed

- The capture toolbar now hides while any of the eight selection handles are dragged
- The toolbar reliably returns and re-anchors below the adjusted capture region when resizing ends
- Annotation properties hide and return with the main toolbar instead of floating separately during resizing

### Added

- Official GitHub update channel enabled by default for new and existing installations
- Manual **Check for updates…** command in the system-tray menu
- Clear GitHub update controls in Preferences, alongside automatic startup checks

### Validation

- Resize-toolbar lifecycle, anchoring, GitHub command, and update-feed regression coverage
- Release build and complete x64 smoke-test suite pass

## [1.2.2] - 2026-07-15

### Added

- Clear labelled Pause, Resume, and Stop controls during recording
- Visible Play/Pause control in the recording review panel
- Discard action with confirmation; discarded recordings are not added to history

### Changed

- Recording review now distinguishes Save trimmed, Keep full, and Discard
- Recording output path is shown before the final save decision

### Validation

- Release build and UI regression suite pass on x64 Windows
- Portable and installer SHA-256 values are emitted in `release.json`

## [1.2.1] - 2026-07-15

- Stabilized the pinned-image OCR selection toolbar during scrolling and double-click selection
- Improved toolbar sizing, visual consistency, and icon synchronization
- Expanded local-first capture, history, annotation, OCR, pin, and recording reliability
