<img src="packaging/logo-512.png" alt="SnapCull logo" width="96" align="right">

# SnapCull

**Fast, free photo culling for Windows.** Open a folder straight off your memory card, flip through hundreds of RAW files instantly, and pick the keepers with one key per photo. SnapCull flags blurry shots for you, puts similar frames side by side, and saves your ratings as `.xmp` sidecars that Lightroom, Capture One, Bridge and darktable pick up automatically.

## Why it's fast

Every RAW file has a full-size JPEG preview hidden inside it, put there by the camera. SnapCull reads that preview directly instead of processing the raw sensor data, so photos appear as quickly as you can press the arrow keys. Photo Mechanic works the same way. It also:

- decodes photos at screen size, and only decodes full resolution when you zoom to 100%;
- decodes the next and previous photos ahead of time;
- loads filmstrip thumbnails in the background, starting with the photos around the one you're looking at;
- saves ratings on a background thread, so pressing a key never waits for the disk.

## Features

- **Works with almost any camera.** See [supported cameras](#supported-cameras). There are no external libraries: SnapCull has its own small TIFF, EXIF, CR3, CRW, MRW and HEIF readers.
- **RAW + JPEG or RAW + HEIF pairs** are treated as one photo.
- **Pick / reject / 1–5 stars / colour labels**, all from the keyboard.
- **Auto-advance:** rate a photo and the next one appears. Hold Shift to do the opposite.
- **Focus check:** every photo gets a sharpness score in the background. Soft and blurry shots get an orange **SOFT** or red **BLUR** badge, and the *Blurry or soft* filter shows them all at once so you can reject them quickly.
- **Compare mode (C):** two photos side by side. Zoom to 100% and both sides zoom to the same spot; pan one and the other follows, so you can see which frame is sharper.
- **100% zoom:** click where you want to look, then drag to pan.
- **Histogram (H)** with the percentage of clipped highlights and shadows.
- **Camera info overlay:** body, lens, focal length, aperture, shutter speed, ISO, date and focus score.
- **Sort by capture time**, so a shoot from two cameras stays in the order it happened.
- **Subfolders:** cull a whole shoot spread over several card folders in one go.
- **Filters:** picks, rejects, not yet flagged, ★ and up, blurry or soft, sharp.
- **Copy or move your picks** (with their paired JPEG/HEIF and sidecars) to another folder. Existing files are never overwritten.
- **Right-click a photo** to open it in your editor, show it in File Explorer, or copy its path.
- **Undo** with Ctrl+Z.
- Dark Windows 11 style UI, full-screen mode, and correct sizing on high-DPI screens.

## Keyboard shortcuts

| Key | Action |
|---|---|
| ← → | Previous / next photo |
| P / X / U | Pick / reject / clear flag |
| 1–5, 0 | Star rating / clear stars |
| 6 7 8 9 | Red / yellow / green / blue label |
| Shift + rating key | Rate, doing the opposite of auto-advance |
| A | Auto-advance on/off |
| Z, Space or click | Zoom to 100% |
| C | Compare mode |
| Tab | Compare mode: switch sides. Otherwise: filmstrip on/off |
| H | Histogram |
| I | Camera info and focus score |
| F or F11 | Full screen |
| Ctrl+Z / Ctrl+O / Ctrl+E | Undo / open folder / copy or move picks |
| ? or F1 | Show all shortcuts |

## Supported cameras

SnapCull is tested against real sample files from [raw.pixls.us](https://raw.pixls.us): **72 of 73 cameras work**. The ones marked † store no usable preview, so SnapCull asks Windows' free [Raw Image Extension](https://apps.microsoft.com/detail/9NCTDW2W1BH8) to render the raw data instead. This is slower (1–7 s per photo), but it works. That extension comes with most Windows 11 PCs.

| Maker | Tested models | Formats |
|---|---|---|
| Canon | EOS R5 Mark II, R6, R10, M200, 1D X Mark III, 90D, 5D Mark IV, 700D, 300D, 10D | CR3, CR2, CRW |
| Nikon | Z 9, Z f, Z 6II, D850, D3500, D70, 1 J5, Coolpix P1000 | NEF, NRW |
| Sony | α1, α7R V, α7 IV, α7 III, α6400, NEX-5N, RX100 VII, DSLR-A100† | ARW |
| Fujifilm | X-T5, X-T3, X100VI, X-A5, FinePix S5 Pro | RAF |
| OM System / Olympus | E-M1 II, E-M10 IV, TG-6, E-1 | ORF |
| Panasonic / Leica | G9, S5, GH4, LX100, FZ1000, D-LUX (Typ 109) | RW2, RWL |
| Pentax / Ricoh | K-1, K-3 III, K-70, K10D, Q7, GR III | PEF, DNG |
| Leica | M10, Q (Typ 116), M8† | DNG |
| Phones | iPhone 12 Pro, Pixel 7 Pro, Galaxy S23 Ultra, Xiaomi Mi 8, Huawei P20 lite†, OnePlus 6† | DNG |
| Drones | DJI Phantom 4 Pro, DJI Mavic Air 2 | DNG |
| Medium format and others | Mamiya ZD, Leaf Aptus 22, Kodak P880, Kodak DCS Pro 14nx†, Minolta Dynax 7D†, Minolta DiMAGE A2†, Samsung NX1, NX300, EX2F, Sigma DP1 Merrill, SD15, fp†, Epson R-D1† | MEF, MOS, KDC, DCR, MRW, SRW, X3F, DNG, ERF |

**Not supported yet:** GoPro GPR (its VC-5 compression can't be decoded by Windows either).

Finished images are supported too: **JPEG, HEIC/HEIF/HIF, AVIF, PNG, TIFF and WebP**. HEIC and AVIF use Windows' own codecs. If they don't open, install Microsoft's free *HEIF Image Extensions* or *AV1 Video Extension* from the Microsoft Store.

Is your camera missing, or does it fail? Run the probe tool on a folder of its files (see below) and open an issue with the output.

## How ratings are stored

For `IMG_0001.CR3`, SnapCull writes `IMG_0001.xmp` next to it. This is the same convention Lightroom, Capture One and Bridge use.

| SnapCull | XMP field | Seen by other apps as |
|---|---|---|
| Stars | `xmp:Rating` 0–5 | Star rating |
| Reject | `xmp:Rating = -1` | Rejected (Lightroom, darktable) |
| Colour label | `xmp:Label` (`Red`, `Yellow`, …) | Colour label |
| Pick | `snapcull:Flag = pick` | Not supported by other apps (see below) |

If a sidecar already exists (for example from Lightroom), SnapCull only changes these fields. Develop settings, keywords and everything else are left alone.

> **Tip for Lightroom users:** Lightroom doesn't read pick flags from XMP files. Use stars as well, or copy your picks into their own folder with **Copy / move picks** before importing.

## How the focus check works

Each photo is shrunk to about 1000 pixels and split into an 8 × 8 grid. For every tile, SnapCull measures how much fine detail there is (the variance of the Laplacian), compared with how much contrast the tile has. Only the sharpest few tiles count, because in a good photo only the subject needs to be sharp; a portrait with a blurred background still counts as sharp. Dividing by contrast stops dark or flat photos from being mistaken for blurry ones.

Scores run from 0 to 100. From 45 up a photo looks sharp, and below 33 it is probably blurry. These thresholds were calibrated on the 72 sample photos above and on blurred copies of them. The real photos had a median score of 84, while even slightly blurred copies scored 32 or less. Run `SnapCull.Probe --focus` to see the numbers. The check catches missed focus and camera shake that you'd see at screen size. Very dark, very flat, or deliberately dreamy shots can be marked SOFT, so always zoom to 100% before deleting anything.

## Download and run

- **Microsoft Store** (coming soon): [SnapCull on the Microsoft Store](https://apps.microsoft.com/detail/9PLWMRJ8BVTS). Installs and updates automatically, with no security warnings.
- **GitHub:** download `SnapCull.exe` from the [Releases](https://github.com/neel-gupta-dev/SnapCull/releases) page and run it. There's nothing to install. If Windows SmartScreen warns you, click **More info → Run anyway**, because this copy isn't code-signed.

You can also pass a folder or a photo on the command line: `SnapCull.exe "D:\Photos\2026-11-14 Wedding"`.

## Build from source

You'll need the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows 10 or 11.

```bash
git clone https://github.com/neel-gupta-dev/SnapCull.git
cd SnapCull
dotnet run --project src/SnapCull
```

Run the tests:

```bash
dotnet test
```

The tests build tiny synthetic CR2, CR3, RAF, MRW, HEIC, PNG, WebP and JPEG files, so no camera files are needed.

### Checking real camera files

The probe tool runs SnapCull's extractor over every photo in a folder and prints the preview size, camera, lens and settings it finds:

```bash
dotnet run --project tools/SnapCull.Probe -c Release -- "D:\path\to\photos"
```

Add `--focus` to also print sharpness scores for each photo and for blurred copies of it. To run the same check in the test suite, point the `SNAPCULL_SAMPLES` environment variable at the folder.

### Single-file build

Build a single self-contained `.exe` that doesn't need .NET installed:

```bash
dotnet publish src/SnapCull -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

### Microsoft Store package (MSIX)

```bash
powershell -File packaging/build-msix.ps1
```

This builds `packaging/out/SnapCull_<version>_x64.msix`, ready to upload in Partner Center. The Store signs it, so no certificate is needed. On first run the script downloads Microsoft's `Microsoft.Windows.SDK.BuildTools` package from nuget.org to get MakeAppx and MakePri, so you don't need a Windows SDK install. The package identity in [`packaging/AppxManifest.xml`](packaging/AppxManifest.xml) belongs to the official Store listing; forks should use their own. The icon and tiles are drawn by [`packaging/make-assets.ps1`](packaging/make-assets.ps1).

## Project layout

```
src/SnapCull.Core/    No UI. RAW preview extraction, EXIF, XMP sidecars, focus and histogram analysis, folder scanning, copy/move
src/SnapCull/         The WPF app: viewers, compare mode, filmstrip, keyboard handling
tools/SnapCull.Probe/ Command-line checker for camera files
tests/                xUnit tests for SnapCull.Core
packaging/            Microsoft Store package: manifest, icons, build script
```

The most interesting file is [`PreviewExtractor.cs`](src/SnapCull.Core/PreviewExtractor.cs), which finds the embedded preview in each RAW format.

## Limitations

- What you see is the camera's embedded JPEG, not a rendering of the raw data. This is the point of a culler, but it means some cameras show a smaller image at 100% zoom. Many Sony bodies embed only a 1616×1080 preview, Panasonic about 1920 px, and DJI drones and some phones under 1000 px. For those small ones SnapCull uses the Windows RAW codec when it's installed.
- The focus check judges the photo at about 1000 px wide, so it won't notice a slight miss that only shows at 100%.
- Sigma X3F and Leaf MOS files show their embedded preview, but no camera info.

## Roadmap

- [x] Focus check (blur detection)
- [x] Compare two shots side by side
- [ ] Detect closed eyes
- [ ] Group burst shots and pick the sharpest automatically
- [ ] Ingest from memory card, with verified backup to two drives
- [ ] Optional darktable-style sidecar names (`IMG_0001.CR3.xmp`)

Contributions are welcome. If SnapCull can't read a preview from your camera's files, please open an issue with the probe tool's output.

## License

SnapCull is open source under the [MIT License](LICENSE), © 2026 Neel Gupta. You can use it, change it and share it, including in your own projects, as long as you keep the copyright notice.
