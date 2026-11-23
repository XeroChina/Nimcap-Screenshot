# NimCap Screenshot · Offline AI for More Than Screenshots

[简体中文](README.md) | **English**

[GitHub](https://github.com/XeroChina/nimcap-screenshot) · [Gitee](https://gitee.com/lilongjiang/SnapSharp) · [Website](https://jietu.nimcap.com/) · [Releases](https://github.com/XeroChina/nimcap-screenshot/releases)

NimCap Screenshot is an open-source Windows desktop tool combining **screenshots, annotations, desktop pins, scrolling capture, and recording** with **local OCR, translation, table and formula recognition, background removal, watermark removal, and image repair**. Capture an image and share it, or keep working: extract text, export a table, generate LaTeX, remove a background, or pin reference material to your desktop.

**Offline AI is central to the application. Local recognition, translation, and image processing run on your computer without uploading your images or text to the cloud.** Once the application, required models, and runtime dependencies are prepared, these features work without internet access, including on isolated internal networks. Use them for internal documents, tutorials, foreign-language reading, and everyday office work. Basic capture, annotations, pins, color picking, measurement, and barcode decoding need no AI downloads.

The application is part of `nimcap` and was formerly called SnapSharp. Its current repository is `XeroChina/nimcap-screenshot`; existing user data directories remain compatible. Built with C#, WPF, and .NET 10 for Windows x64, it is accessed through the notification area and global shortcuts.

## What offline AI can do

| Task | Local processing | Result |
|---|---|---|
| Turn screenshots into text | PaddleOCR, or Windows OCR / Tesseract | Editable text, clipboard copy, TXT export |
| Extract document fields | Local OCR and field rules | Editable fields, clipboard copy, TXT export |
| Turn table images into data | SLANet structure recognition with OCR text | Correct rows, columns, and merged cells; export CSV/XLSX |
| Turn formula images into source | Local LaTeX-OCR | Editable LaTeX, clipboard copy, `.tex` export |
| Read foreign-language material | Qwen3.5 GGUF + llama.cpp | Translate OCR results or entered text locally |
| Remove image backgrounds | U²-Net and BiRefNet models | Original dimensions and transparency; PNG/WebP output |
| Remove watermarks and repair images | MI-GAN, LaMa, Moebius; SLBR for automatic detection | Compare with the original, undo, copy, save, or pin |
| Blur sensitive text before sharing | Local OCR and rules; optional local Qwen assistance for names and addresses | Blur the image regions containing sensitive text |

### Preparing for offline use

Install models as needed; taking screenshots does not require downloading every model. Configure OCR, tables, formulas, background removal, and local translation in Settings. Download or import repair models from the watermark-removal page. Built-in model setup provides source selection, progress, file verification, and configuration. Normal use requires neither Python nor CUDA.

For a computer without internet access, prepare the application, models, and runtimes on a connected computer first. Deploy them with the local configuration, then verify model paths and device settings on the target computer. Image AI uses ONNX Runtime on CPU or DirectML; local translation uses a separate llama.cpp runtime on CPU or Vulkan. Larger models generally require more memory, video memory, and processing time.

**Local processing and online features have separate controls.** Online translation and image search are disabled by default and require confirmation for every request after being enabled. Model downloads and update checks also use the network. Offline AI means inference can run without the internet; it does not mean the application never makes network requests. History images are stored locally without encryption. See [configuration and dependencies](docs/配置与依赖.md) for storage, model management, and data migration.

[Software tour](#software) · [Get started](#start) · [First-time setup](#settings) · [Documentation and development](#development)

<a id="software"></a>

## Software tour

NimCap Screenshot runs in the notification area. Use shortcuts or its tray menu to capture, annotate, blur sensitive text, copy, save, pin, or recognize an image. To process an existing image, choose **Open image...** in the tray menu, or drop the file onto the `NimCap.Screenshot.exe` launcher (the command `NimCap.Screenshot.exe picture.png` also works), then use **Image Tools** to cut out, remove watermarks, or recognize. You can also copy the image, press `Ctrl+Shift+V` to create a pin, then right-click the pin and choose **Image Tools**.

The illustrations below reuse repository assets. Some show the former branding or rendered WPF components, so button layouts may differ from the current version. Follow the entry points described here and in the current application. See [image provenance](Screenshot/Blog/README.md) for how the illustrations were made.

<a id="selection"></a>

### Capture and annotate

Press `Ctrl+Shift+A` to capture a region. Drag a selection or use element mode to snap to controls. Full-screen, active-window, and delayed capture are supported, alongside rectangle, ellipse, freehand lasso, and polygon selections. Multiple monitors and mixed DPI use a shared selection workflow. Element capture covers currently visible content.

Add arrows, text, numbered steps, highlights, pixelation, or blur. Move, resize, rotate, and reorder annotations, undo/redo edits, or save annotation templates for reuse. Use these tools to explain a workflow, illustrate a tutorial, or report an interface issue.

![Screenshot selection and annotation toolbar](Screenshot/1.png)

### Blur sensitive text before sharing

Capture and pin editing toolbars provide sensitive-text blurring. Local OCR and rules locate phone numbers, identity and bank-card numbers, email addresses, license plates, name fields, and addresses, then blur their image regions. Optional local Qwen assistance helps identify names and addresses. Configure OCR first and review the image for missed or incorrectly selected text before sharing.

![Example after sensitive-text blurring](Screenshot/一键脱敏-脱敏后.png)

<a id="recognition"></a>

### Text, document fields, and barcodes

Choose text OCR in **Image Tools** to extract editable text, copy it, or export it. Recognition boxes and text results support navigation in both directions. PaddleOCR, Windows OCR, and Tesseract are local engines requiring their respective models, Windows language packs, or trained data.

Document mode reuses local OCR to extract current-page fields from Chinese ID cards, including front/back and combined images, driving and vehicle licenses, household registers, passports, bank cards, and business licenses. Edit the fields before copying or exporting TXT. QR codes and one-dimensional barcodes decode offline without AI models. Document extraction uses OCR and rules; it does not verify authenticity.

![Example Chinese text recognized by local PaddleOCR](Screenshot/Blog/14-ai-ocr-chinese.png)

### Tables and formulas

Table mode uses local SLANet to recognize structure, then fills cells using OCR. Correct rows, columns, text, and merged cells before exporting CSV or XLSX. Select a single table for this workflow.

![Local table recognition and cell correction example](Screenshot/Blog/16-ai-table-recognition.png)

Formula mode converts an individual formula image into editable LaTeX for copying or `.tex` export. Tables and formulas require their own matching models. Compare complex structures and recognition results with the original before reuse.

![Local formula recognition producing LaTeX](Screenshot/Blog/17-ai-formula-latex.png)

### Local translation and hover OCR

Recognize image text, then translate the full result or a selected passage; entered text can also be translated. Local translation runs Qwen3.5 through llama.cpp on CPU or Vulkan without a cloud API. Choose a model that fits your hardware and complete setup before offline use.

Enable screen text capture from the tray for the current session to run local OCR near a stationary pointer. Interface language, OCR language, and translation source/target languages are configured independently.

![Detail from an existing local translation result](Screenshot/Blog/20-ai-local-translation.png)

*This image is cropped from an existing translation result. The model was not rerun for this documentation update.*

<a id="cutout"></a>

### Background removal

Select a U²-Net or BiRefNet preset for your hardware and image, run local background removal, then inspect the edges against the original. Results retain the source dimensions and transparency and can be copied, saved, or pinned. Use PNG or transparency-capable WebP to preserve the transparent background. CPU mode offers U²-Net and General Lite; other built-in presets are available with DirectML.

![Original and actual General Lite background-removal result](Screenshot/Blog/19-ai-cutout-comparison.png)

### Watermark removal and image repair

**Image Tools → Remove watermark** offers three workflows: paint a repair mask manually, detect within a selected region, or detect across the full image in smart mode. Region and smart modes use SLBR detection followed by the selected repair model. Manual painting does not require SLBR.

| Model | Role |
|---|---|
| MI-GAN | Fast repair |
| LaMa | Default repair model for new configurations |
| Moebius | Optional detail candidate with greater resource demands; CPU processing can be slow |
| SLBR | Detection model used with a repair model in region/smart modes |

When a selected model is not installed, the application offers a download confirmation with source and size, then verifies and configures it. Original and result appear side by side with synchronized zoom and pan. Edit masks, undo, copy, save, or pin the output. Models infer hidden content and cannot guarantee accurate reconstruction of text, numbers, or faces. See [watermark removal and image repair](docs/去水印与图像修复.md).

![Current Image Tools window: original and LaMa CPU watermark-removal result](Screenshot/去水印.png)

<a id="pins"></a>

### Desktop pins

Copy image, text, or color content, then press `Ctrl+Shift+V` or choose **Pins** from the tray to keep it on top. For image pins, use the wheel to zoom and `Ctrl+wheel` to adjust opacity. The context menu provides Image Tools, editing, rotation, click-through, and grouping. Hide/restore all pins or restore the last closed pin.

![Illustrated workspace with image, text, and color pins](Screenshot/Blog/12-pin-workspace.png)

<a id="scrolling"></a>

### Scrolling capture

Select a region through the tray scrolling-capture menu. Scroll manually to collect frames or use automatic wheel input. Review the stitching, adjust seams, and trim edges before adding annotations or exporting a long image or paginated PDF. Save an `.ssscroll` project to retain source frames and adjustments.

The workflow primarily targets vertical content. Dynamic pages, fixed overlays, and repeated textures can affect stitching; inspect the result.

![Scrolling preview, edge trimming, and seam adjustment example](Screenshot/Blog/02-scrolling-preview.png)

<a id="recording"></a>

### MP4/GIF recording

Click the video icon in a rectangular selection or open the recording toolbar from the tray. Record a region within one monitor with countdown, pause/resume, timed stop, cursor display, and click highlighting.

MP4 uses H.264 with optional system audio, microphone, camera, and AAC audio. GIF is silent, with a maximum of 15 fps and 60 seconds. Audio and camera start disabled; later recordings remember selections. Actual frame rates depend on hardware and encoding performance.

![MP4 recording configuration example](Screenshot/Blog/06-recording-mp4.png)

*This illustration shows configuration, not measured recording performance.*

<a id="output"></a>

### Color picking, measurement, export, and history

Sample colors with the capture magnifier. The standalone ruler measures lines, width/height, distances between two points, and angles, with guides and pixel readings.

![Screen ruler and measurement tools](Screenshot/屏幕尺子.png)

Save PNG, JPEG, WebP, TIFF, BMP, or A4-paginated PDF. Preview resizing and encoding options before saving. Automatic saving supports date folders and filename templates. History has a separate switch and stores composite PNG images, thumbnails, and an index for reuse; it does not retain editable annotation objects.

![Screenshot history and image review](Screenshot/截图历史.png)

<a id="start"></a>

## Download and get started

1. Download the Windows x64 package from [GitHub Releases](https://github.com/XeroChina/nimcap-screenshot/releases), extract the entire directory, and run `NimCap.Screenshot.exe`.
2. Find its icon in the notification area. There is no persistent main window on startup.
3. Press `Ctrl+Shift+A`, select a region, annotate, then copy, save, or pin. Press `Esc` to cancel.
4. For an existing image, choose **Open image...** in the tray menu, or drop the file onto the `NimCap.Screenshot.exe` launcher, then pick recognition, cutout, or watermark removal in **Image Tools**. You can also copy it, press `Ctrl+Shift+V`, then right-click the pin and choose **Image Tools**.
5. Configure only the models and runtimes required for your AI features. Once prepared, the corresponding local features can run offline.

| Default shortcut | Action |
|---|---|
| `Ctrl+Shift+A` | Region screenshot |
| `Ctrl+Shift+F` | Full-screen screenshot |
| `Ctrl+Shift+W` | Active-window screenshot |
| `Ctrl+Shift+V` | Create a pin from the clipboard |
| `Ctrl+Shift+H` | Hide/show all pins |

Change bindings and assign ruler or pin-restoration shortcuts under **Settings → Shortcuts**. Language/region packs include Chinese, English, Japanese, Korean, German, French, and others. Select and save a language under **Settings → General → Interface Language**. Some translations are machine-generated drafts.

<a id="settings"></a>

## First-time setup and requirements

| Feature | Setup and requirements |
|---|---|
| Text OCR | Settings → OCR and Recognition: recommended ONNX OCR setup, or select Windows OCR / Tesseract |
| Tables and formulas | Settings → OCR and Recognition → Local Tables and Formulas: configure each model; tables also need OCR |
| Background removal | Settings → Background Removal: choose a device, download a model, set the default |
| Local translation | Settings → Translation, APIs, and Privacy: Qwen3.5 GGUF and matching llama.cpp, CPU/Vulkan |
| Watermark removal | Image Tools → Remove watermark: choose or import a repair model; region/smart modes also need SLBR |
| Recording | Settings → Recording: check and configure the recording environment |

Windows 10 version 2004 or later, or Windows 11, x64 is required. Self-contained releases include .NET; framework-dependent builds require the matching .NET 10 Desktop Runtime. Recording may require VC++ x64 and Windows media components.

ONNX image inference runs in a separate worker process. GPU initialization/inference failures or insufficient video-memory budget can fall back to CPU when conditions allow. Vulkan failures in local translation do not automatically switch to CPU. See [configuration and dependencies](docs/配置与依赖.md) for model/device limits, and the [user guide](docs/UserGuide.en.md#faq) for troubleshooting.

<a id="development"></a>

## Documentation and development

The WPF application orchestrates user workflows. Libraries handle capture, imaging, editing, recognition, recording, infrastructure, and plugin contracts/hosting. This README describes connected functionality in the current code; planned features and candidate components are listed separately in the roadmap.

- [Documentation index](docs/README.md) · [Feature-to-code reference](docs/功能与代码对照.md)
- [English user guide](docs/UserGuide.en.md) · [Chinese user guide](docs/使用指南.md)
- [Configuration and dependencies](docs/配置与依赖.md) · [Watermark removal and image repair](docs/去水印与图像修复.md)
- [Architecture](docs/ARCHITECTURE.md) · [Build and tests](docs/构建与测试.md)
- [Inference isolation](docs/异常与推理隔离.md) · [Mixed DPI and multiple monitors](docs/混合DPI与多屏坐标.md) · [Localization](docs/本地化.md)
- [Feature specifications and roadmap](<docs/NimCap Screenshot 功能规格与路线图.md>) · [Image provenance](Screenshot/Blog/README.md)

Technical references are primarily in Chinese; an English user guide is linked above.

Copyright 2026 leelongjiang and NimCap Screenshot contributors. Licensed under [Apache License 2.0](LICENSE). Component and model licenses are listed separately in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
