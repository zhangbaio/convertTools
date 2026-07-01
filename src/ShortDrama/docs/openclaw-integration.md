# OpenClaw Integration

The MVP now supports two OpenClaw-friendly call modes:

- local CLI with JSON output
- local HTTP agent with JSON request/response bodies

## Start Local Agent

```bash
dotnet run --project src/ShortDrama.Agent/ShortDrama.Agent.csproj
```

Health check:

```bash
curl -sS http://localhost:5000/health | python3 -m json.tool
```

## HTTP: Cost Report

```text
POST /cost-report/build
```

Request:

```json
{
  "projectDir": "/abs/path/to/project",
  "templateDocxPath": "/abs/path/to/1111.docx",
  "configDir": "/abs/path/to/config",
  "outputDir": "/abs/path/to/output"
}
```

## HTTP: Batch Transcode

```text
POST /transcode/batch
```

Request:

```json
{
  "projectDir": "/abs/path/to/project",
  "inputDir": "/abs/path/to/input",
  "outputDir": "/abs/path/to/output",
  "configFile": "/abs/path/to/config.txt",
  "overwrite": false,
  "crf": 23,
  "preset": "medium"
}
```

Example:

```bash
curl -sS http://localhost:5000/transcode/batch \
  -H 'Content-Type: application/json' \
  -d '{
    "projectDir": "/Users/zhangbiao/Documents/编程/ai/codex/ai-vedio/demo-video/workflow/末世乐园之繁殖/末世禁区求生局",
    "inputDir": "/Users/zhangbiao/Documents/编程/ai/codex/ai-vedio/demo-video/workflow/末世乐园之繁殖/末世禁区求生局/videos",
    "outputDir": "/Users/zhangbiao/Documents/编程/ai/codex/ai-vedio/demo-video/workflow/末世乐园之繁殖/末世禁区求生局/transcoded",
    "configFile": "/Users/zhangbiao/Documents/编程/ai/vedio/convertTools/config.txt",
    "overwrite": false,
    "crf": 23,
    "preset": "medium"
  }' | python3 -m json.tool
```

## HTTP: Project Image Generate

```text
POST /project-image/generate
```

Request:

```json
{
  "projectDir": "/abs/path/to/project",
  "inputDir": "/abs/path/to/input",
  "outputDir": "/abs/path/to/output",
  "configFile": "/abs/path/to/config.txt",
  "count": 5,
  "overwrite": false
}
```

Example:

```bash
curl -sS http://localhost:5000/project-image/generate \
  -H 'Content-Type: application/json' \
  -d '{
    "projectDir": "/Users/zhangbiao/Documents/编程/ai/codex/ai-vedio/demo-video/workflow/末世乐园之繁殖/末世禁区求生局",
    "inputDir": "/Users/zhangbiao/Documents/编程/ai/codex/ai-vedio/demo-video/workflow/末世乐园之繁殖/末世禁区求生局/videos",
    "outputDir": "/Users/zhangbiao/Documents/编程/ai/codex/ai-vedio/demo-video/workflow/末世乐园之繁殖/末世禁区求生局",
    "configFile": "/Users/zhangbiao/Documents/编程/ai/vedio/convertTools/config.txt",
    "count": 5,
    "overwrite": true
  }' | python3 -m json.tool
```

## HTTP: Rewrite Project Info

```text
POST /rewrite/project-info
```

Request:

```json
{
  "projectDir": "/abs/path/to/project",
  "configFile": "/abs/path/to/config.txt",
  "outputFile": "/abs/path/to/短剧信息_改写.txt",
  "overwrite": true
}
```

Example:

```bash
curl -sS http://localhost:5000/rewrite/project-info \
  -H 'Content-Type: application/json' \
  -d '{
    "projectDir": "/Users/zhangbiao/Documents/编程/ai/codex/ai-vedio/demo-video/workflow/末世乐园之繁殖/末世禁区求生局",
    "configFile": "/Users/zhangbiao/Documents/编程/ai/vedio/convertTools/config.txt",
    "outputFile": "/Users/zhangbiao/Documents/编程/ai/codex/ai-vedio/demo-video/workflow/末世乐园之繁殖/末世禁区求生局/短剧信息_改写.txt",
    "overwrite": true
  }' | python3 -m json.tool
```

## HTTP: Poster Rename

```text
POST /poster-rename/run
```

Request:

```json
{
  "projectDir": "/abs/path/to/project",
  "inputFile": "/abs/path/to/海报图片.jpg",
  "outputFile": "/abs/path/to/新剧名-海报.jpg",
  "configFile": "/abs/path/to/config.txt",
  "nameTemplate": "{name}-海报",
  "useAi": true,
  "overwrite": true
}
```

Example:

```bash
curl -sS http://localhost:5000/poster-rename/run \
  -H 'Content-Type: application/json' \
  -d '{
    "projectDir": "/Users/zhangbiao/Documents/编程/ai/codex/ai-vedio/demo-video/workflow/末世乐园之繁殖/末世禁区求生局",
    "configFile": "/Users/zhangbiao/Documents/编程/ai/vedio/convertTools/config.txt",
    "nameTemplate": "{name}-海报",
    "useAi": true,
    "overwrite": true
  }' | python3 -m json.tool
```

## HTTP: Batch File Rename

```text
POST /batch-file-rename/run
```

Request:

```json
{
  "projectDir": "/abs/path/to/project",
  "inputDir": "/abs/path/to/videos",
  "configFile": "/abs/path/to/config.txt",
  "nameTemplate": "{name}-第{index}集",
  "overwrite": true
}
```

Example:

```bash
curl -sS http://localhost:5000/batch-file-rename/run \
  -H 'Content-Type: application/json' \
  -d '{
    "projectDir": "/Users/zhangbiao/Documents/编程/ai/codex/ai-vedio/demo-video/workflow/末世乐园之繁殖/末世禁区求生局",
    "inputDir": "/Users/zhangbiao/Documents/编程/ai/codex/ai-vedio/demo-video/workflow/末世乐园之繁殖/末世禁区求生局/videos",
    "configFile": "/Users/zhangbiao/Documents/编程/ai/vedio/convertTools/config.txt",
    "nameTemplate": "{name}-第{index}集",
    "overwrite": true
  }' | python3 -m json.tool
```

Example:

```bash
curl -sS http://localhost:5000/cost-report/build \
  -H 'Content-Type: application/json' \
  -d '{
    "projectDir": "/Users/zhangbiao/Documents/编程/ai/codex/ai-vedio/demo-video/workflow/末世乐园之繁殖/末世禁区求生局",
    "templateDocxPath": "/Users/zhangbiao/Library/Containers/com.tencent.xinWeChat/Data/Documents/xwechat_files/wxid_bvryp9i921je22_c5ec/msg/file/2026-03/1111.docx",
    "configDir": "/Users/zhangbiao/Desktop",
    "outputDir": "/Users/zhangbiao/Documents/编程/ai/codex/ai-vedio/demo-video/workflow/末世乐园之繁殖/末世禁区求生局"
  }' | python3 -m json.tool
```

Success response:

```json
{
  "ok": true,
  "outputs": {
    "docx": "/abs/output/成本报表.docx",
    "pdf": "/abs/output/成本报表.pdf",
    "png": "/abs/output/成本报表.png"
  },
  "project": {
    "title": "末世禁区求生局",
    "originalTitle": "末世乐园之繁殖",
    "episodes": 3,
    "minutes": 8,
    "costWan": 1,
    "company": "湖北云漫科技有限公司"
  }
}
```

## HTTP: Workflow

```text
POST /workflow/run
```

Request:

```json
{
  "definition": {
    "projectDir": "/abs/path/to/project",
    "configDir": "/abs/path/to/config",
    "steps": [
      {
        "type": "cost-report",
        "template": "/abs/path/to/1111.docx",
        "outputDir": "/abs/path/to/output",
        "retry": 0,
        "continueOnError": false
      }
    ]
  }
}
```

Example:

```bash
curl -sS http://localhost:5000/workflow/run \
  -H 'Content-Type: application/json' \
  -d '{
    "definition": {
      "projectDir": "/Users/zhangbiao/Documents/编程/ai/codex/ai-vedio/demo-video/workflow/末世乐园之繁殖/末世禁区求生局",
      "configDir": "/Users/zhangbiao/Desktop",
      "steps": [
        {
          "type": "transcode",
          "configFile": "/Users/zhangbiao/Documents/编程/ai/vedio/convertTools/config.txt",
          "inputDir": "/Users/zhangbiao/Documents/编程/ai/codex/ai-vedio/demo-video/workflow/末世乐园之繁殖/末世禁区求生局/videos",
          "outputDir": "/Users/zhangbiao/Documents/编程/ai/codex/ai-vedio/demo-video/workflow/末世乐园之繁殖/末世禁区求生局/transcoded"
        },
        {
          "type": "cost-report",
          "template": "/Users/zhangbiao/Library/Containers/com.tencent.xinWeChat/Data/Documents/xwechat_files/wxid_bvryp9i921je22_c5ec/msg/file/2026-03/1111.docx",
          "outputDir": "/Users/zhangbiao/Documents/编程/ai/codex/ai-vedio/demo-video/workflow/末世乐园之繁殖/末世禁区求生局"
        }
      ]
    }
  }' | python3 -m json.tool
```

Current status:

- The local HTTP agent is implemented.
- The workflow runtime supports `transcode` and `cost-report` steps end-to-end.
- Other step types still return `NOT_IMPLEMENTED`.

## CLI Fallback

If OpenClaw needs to shell out instead of calling HTTP:

```bash
shortdrama cost-report build \
  --project-dir "/abs/project-dir" \
  --template "/abs/templates/1111.docx" \
  --output-dir "/abs/output-dir" \
  --json-output
```
