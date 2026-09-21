# Changelog

All notable changes to HueReka! are listed here.

## [1.1.0] - 2026-09-21

### Added
- **Favorite light:** click the star next to a light's name to make it your favorite. HueReka selects it every time it opens, and the list marks it with a star. Click the star again to remove it. The favorite is saved with your bridge pairing and kept when you use **Pair again**.

## [1.0.1] - 2026-09-15

### Changed
- **Pairing is impossible to miss.** Pairing used to block the app while the only prompt was a small line of status text. Now a large card covers the whole window, with the app blurred and dimmed behind it. It shows a drawing of the bridge with a pulsing button, a countdown ring, the seconds left and the bridge's address.
- The card stays up until the bridge pairs. A short "Paired!" confirmation then shows before your lights appear.
- **Cancel** or Esc stops waiting at any time.
- If time runs out, the card offers **Try again** (Enter) or **Not now** instead of just disappearing.

### Fixed
- The light count now reads "1 light" instead of "1 lights".
- Window tests now run inside a real message loop, like the app does. Before, their code resumed on background threads after each `await`, which could hide interface bugs. The app itself was not affected.
- Tests can no longer overwrite your real saved bridge pairing.

## [1.0] - 2026-09-15

First public release.

### Added
- Windows desktop app and `huereka` command-line tool for Philips Hue lights on the local network. No Hue account or cloud login is needed.
- **Pairing that waits for you:** press the bridge's link button within 90 seconds; there's no need to click Pair again. The `huereka pair` command waits too.
- **Automatic connection:** a saved bridge reconnects at launch. On first launch the app finds your bridge and starts pairing if there is exactly one.
- **Follows a moved bridge:** if the router gives the bridge a new IP address, HueReka finds it again. The pinned certificate confirms it's the same bridge.
- **Control several lights at once:** Ctrl+click, Shift+click or Ctrl+A. Every control applies to all selected lights that support it.
- Brightness applies when you release the slider.
- Keyboard and mouse shortcuts: Space or double-click switches lights on or off, and F5 refreshes. Controls have tooltips.
- Light states refresh every 15 seconds and whenever you return to the window.
- Glass-style interface, with keyboard and screen reader support for its custom buttons.
- Encrypted per-user credential storage (Windows DPAPI) and HTTPS with certificate pinning.

[1.0.1]: https://github.com/LieutenantBob/HueReka/releases/tag/v1.0.1
[1.0]: https://github.com/LieutenantBob/HueReka/releases/tag/v1.0
