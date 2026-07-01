# CLI Draft

Cost report images are rendered directly in-process and no longer require LibreOffice / Poppler / ImageMagick.

Bundled media tools are supported. The runtime will look for:

```text
tools/<os-arch>/ffmpeg/ffmpeg(.exe)
tools/<os-arch>/ffmpeg/ffprobe(.exe)
```

## Parse project info

```bash
shortdrama parse-info \
  --project-dir "/path/to/project"
```

## Rewrite project info

```bash
shortdrama rewrite project-info \
  --project-dir "/path/to/project" \
  --config-file "/path/to/config.txt" \
  --json-output
```

Optional:

```bash
--output-file "/path/to/短剧信息_改写.txt"
--overwrite
```

Behavior:

- reads `短剧信息.txt`
- calls the chat model configured in `config.txt`
- rewrites `推荐语` and `简介`
- writes `短剧信息_改写.txt` by default

## Rename poster

```bash
shortdrama poster-rename run \
  --project-dir "/path/to/project" \
  --json-output
```

Optional:

```bash
--input-file "/path/to/海报图片.jpg"
--output-file "/path/to/新剧名-海报.jpg"
--config-file "/path/to/config.txt"
--name-template "{name}-海报"
--use-ai
--overwrite
```

Behavior:

- defaults source file to `海报图片.*`
- defaults destination file name to `{title}-海报.<ext>`
- if `--use-ai --config-file` is set, the chat model generates the poster title first
- poster image replacement is AI-only and requires a valid image edit endpoint in `config.txt`
- by default the image edit request uses `ImageModelEndpoint + /images/edits`
- if the provider uses a different route, set `ImageEditEndpoint`, `ImageEditPath`, or `ImageEditModelId`

## Batch file rename

```bash
shortdrama batch-file-rename run \
  --project-dir "/path/to/project" \
  --config-file "/path/to/config.txt" \
  --json-output
```

Optional:

```bash
--input-dir "/path/to/videos"
--name-template "{name}-第{index}集"
--overwrite
```

Behavior:

- defaults input dir to `<project-dir>/videos`
- renames all supported video files in sorted order
- supports `{name}` and `{index}`
- if `--config-file` is set, `VideoNameTemplate` is read from `config.txt`

## Build cost report

```bash
shortdrama cost-report build \
  --project-dir "/path/to/project" \
  --template "/path/to/1111.docx" \
  --output-dir "/path/to/output" \
  --json-output
```

Optional:

```bash
--config-dir "/path/to/config"
```

If `--config-dir` is omitted, the CLI walks upward from the project directory and looks for:

- `config/sign.png`
- `config/seal.png`

## Batch transcode videos

```bash
shortdrama transcode batch \
  --project-dir "/path/to/project" \
  --config-file "/path/to/config.txt" \
  --json-output
```

Optional:

```bash
--input-dir "/path/to/input"
--output-dir "/path/to/output"
--config-file "/path/to/config.txt"
--overwrite
--crf 23
--preset medium
```

Defaults:

- input dir: `<project-dir>/videos`
- output dir: `<project-dir>/transcoded`
- output format: `mp4` with `H.264/AAC`
- if `--config-file` is set, bitrate / fps / resolution / hardware encoder settings are loaded from `config.txt`

## Generate project images

```bash
shortdrama project-image generate \
  --project-dir "/path/to/project" \
  --template-dir "/path/to/project-image-templates" \
  --config-file "/path/to/config.txt" \
  --json-output
```

`--template-dir` 现在默认必用；如果不显式传入，则必须在 `config.txt` 中配置 `ProjectImageTemplateDir`，并且目录下需要完整提供 `工程图_1.png` 到 `工程图_N.png`。

Optional:

```bash
--input-dir "/path/to/input"
--output-dir "/path/to/output"
--count 5
--overwrite
```

Defaults:

- input dir: `<project-dir>/videos`
- output dir: `<project-dir>`
- output files: `工程图_1.png`, `工程图_2.png`, ...
- template dir: `--template-dir` or `ProjectImageTemplateDir` in `config.txt` (required)
- if `--config-file` is set, `ProjectImageCount` is loaded from `config.txt`
