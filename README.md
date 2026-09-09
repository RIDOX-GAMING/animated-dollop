# Road96FarsiAuto 2.0

Runtime Persian localization plugin for **Road 96 (Unity IL2CPP)**.

## What it does

This version is designed to work without extracting or rebuilding Unity assets:

- Hooks `TMP_Text.text` and `UnityEngine.UI.Text.text` at runtime.
- Scans likely Road 96 dialogue/localization assemblies for string-returning methods.
- Protects Unity tags, placeholders, bracket tokens, URLs, paths and common escape sequences before translation.
- Rejects translations when protected tokens were changed or lost.
- Uses asynchronous HTTP requests with bounded parallelism, retry, de-duplication and persistent cache.
- Applies the finished translation back on the Unity main thread.
- Converts common Arabic/Persian letters to consistent Persian forms.
- Includes a lightweight legacy-renderer Persian shaping + visual RTL pass.
- Attempts to create a Windows system-font fallback at runtime and add it to TextMeshPro fallback fonts; legacy UI text can also receive a system font fallback.

## Install

1. Install **BepInEx 6 IL2CPP** for your Road 96 build.
2. Build this repository with GitHub Actions.
3. Put `Road96FarsiAuto.dll` into:

`Road 96/BepInEx/plugins/`

4. Start the game.

No AssetRipper / AssetStudio step is required by the plugin.

## Configuration

After the first launch:

`Road 96/BepInEx/config/ridox.road96.farsiauto.cfg`

Important settings:

- `Translation/MaxParallel` - concurrent translation requests.
- `Translation/Endpoint` - Google Translate-compatible endpoint.
- `Persian/EnableRTL` - enable Persian shaping/visual ordering.
- `Persian/EnableFontFallback` - enable runtime font fallback.
- `Persian/FallbackFonts` - font names tried on Windows.
- `Hooks/TranslateLocalizationMethods` - scan likely Road 96 localization methods.

Cache:

`Road 96/BepInEx/config/Road96FarsiAuto.cache.json`

## Safety around game data

The plugin never rewrites Unity asset files. It changes strings only when they are exposed by runtime methods/UI components. It does not intentionally translate identifiers, URLs, placeholders or markup. When a protected token is modified by the translation service, that translation is rejected instead of being applied.

## Limits

No generic runtime translator can honestly guarantee that every internal string used by every Road 96 build is exposed through a hook. The plugin therefore combines UI hooks with a conservative localization-method scan. Exact game-specific dialogue methods can still be added later if a particular Road 96 build hides text behind a custom path.

The default translator endpoint is an unofficial/public Google Translate-compatible endpoint and can be rate-limited or unavailable. The cache is intended to make repeated lines effectively instant after the first successful translation.
