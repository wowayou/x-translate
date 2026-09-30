# XTranslate icon assets

The application mark is an original generated raster asset: two interlocking off-white speech-bubble/paper shapes form an X over a deep teal rounded-square tile. The mark uses the existing XTranslate palette (`#17695E`, warm off-white, and a restrained mint accent), has no lettering, and keeps the canvas transparent outside the tile so it works on Windows light and dark surfaces.

## Generation record

- Mode: built-in `image_gen` tool (not CLI/API fallback)
- Use case: `logo-brand`
- Generated source: `src/XTranslate.App/Assets/xtranslate-source.png`
- Generated output inspected: `C:\Users\xcg\.codex\generated_images\01a0bf03-cffd-7201-b813-3354d193adf5\exec-eab36d3a-8608-4063-987e-95f2b9f14450.png`
- Prompt:

  > Use case: logo-brand
  > Asset type: Windows desktop application icon, source raster
  > Primary request: original XTranslate app icon for a translation utility. A deep teal rounded-square tile with two interlocking white speech-bubble / paper-sheet shapes forming a clear geometric X in the center, with a very small amount of mint-teal dimensional layering.
  > Scene/backdrop: genuinely transparent background outside the rounded-square tile; no mockup, no device, no surrounding scene
  > Style/medium: polished flat vector-like raster mark, crisp simple shapes, native Windows productivity-tool feel, clean edges
  > Composition/framing: centered single icon, generous safe margin inside the 1024 canvas, strong silhouette that remains recognizable at 16px
  > Color palette: deep teal #17695E and warm off-white, subtle mint accent only; high contrast
  > Lighting/mood: flat even lighting, restrained professional
  > Materials/textures: smooth solid fills, no texture
  > Text (verbatim): none
  > Constraints: exactly one icon mark; preserve transparent alpha outside the rounded-square tile; no tiny details; no letters or wordmark; no translation symbols; no watermark
  > Avoid: existing translator app logos, flags, globes, language characters, gradients, photorealism, 3D mockup, multiple variants, contact sheet, border frame, background color outside the icon tile

## Derived assets

`tools/icon-builder/Build-Icon.ps1` (Windows/System.Drawing) performs only technical resizing and packaging; it does not alter the generated artwork. The generated outputs are checked by dimensions, PNG format, ICO frame directory, and alpha behavior. Different encoders or resampling implementations must not be expected to produce byte-for-byte identical files.

- `Assets/xtranslate.png`: 256px transparent PNG for the WPF header, window icon and taskbar.
- `Assets/xtranslate.ico`: PNG-compressed ICO frames at 16, 20, 24, 32, 40, 48, 64, 128, and 256px for the EXE and tray icon.

The derived `xtranslate.png` and `xtranslate.ico` are stored under `src/XTranslate.App/Assets/` and referenced by the project (`ApplicationIcon`, WPF `Resource`, embedded ICO). Regenerate them with the Windows builder after replacing the source artwork; use a separate output directory for verification so the shipping assets remain unchanged.

`AppIcon.CreateTrayIcon()` reads the embedded ICO and clones the 16px frame into an independent `System.Drawing.Icon`; callers own and dispose the returned icon.
