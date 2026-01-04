# malyuvach

Telegram + Discord AI drawing & conversational bot. It combines:

* Ollama-hosted local LLM (prompt + dialogue management)
* ComfyUI workflow execution for image generation
* Optional Whisper.Net speech‑to‑text (Telegram voice / audio → text)
* Persistent per‑chat context with automatic trimming
* Serilog structured logging (console + rolling file)

> Name comes from Ukrainian "Малювач" ("one who draws").

## ✨ Features

* Group & private chat support (responds when mentioned or replied to in groups)
* Discord support (DMs + mention/reply triggers in channels)
* JSON based internal response contract (text + prompt + orientation)
* Configurable system prompts (main + optional JSON validator stage)
* Pluggable ComfyUI workflows (map node fields in config – no code changes)
* Deterministic seeds & orientation handling (portrait swaps dimensions automatically)
* Whisper.Net STT with OGG/Opus → WAV conversion (Concentus)
* Context persistence on disk (one JSON file per chat id)
* Safe retries & context rollback for malformed LLM answers

## 🧱 Requirements

* .NET 8 SDK (build & run)
* [Ollama](https://ollama.com/) running locally (default: 11434)
* [ComfyUI](https://github.com/comfyanonymous/ComfyUI) (default: 8188)
* (Optional) Whisper model `.bin` file for STT (Whisper.Net format)

### Hardware Notes

| Component | Minimal | Recommended |
|-----------|---------|------------|
| LLM (small, e.g. gemma2 / gpt-oss) | 4GB VRAM | >8GB VRAM or fast CPU |
| Image (example Qwen / Flux style workflow 1328x784 @ 8 steps) | ~12GB VRAM | 16–24GB VRAM for higher res / steps |
| STT (CPU) | 2 cores | 8+ threads for medium model |

You can reduce VRAM usage by lowering resolution or steps in workflow config.

## 🚀 Quick Start

```bash
git clone https://github.com/maincoon/malyuvach.git
cd malyuvach
dotnet publish -c Release -o out

# Copy or create config files
cp appsettings.json out/
cp appsettings.Development.json out/ 2>/dev/null || true
cp appsettings.Production.json out/ 2>/dev/null || true

cd out
# (Optional) choose environment: Development | Production (default depends on host)
export DOTNET_ENVIRONMENT=Production
./Malyuvach
```

> Use `DOTNET_ENVIRONMENT` (preferred for worker services) or `ASPNETCORE_ENVIRONMENT` to select the config overlay.

## 🧩 Configuration Overview

Config root section: `Malyuvach`. Environment specific files overlay the base `appsettings.json`.

### LLM Settings (`Malyuvach:LLM`)

| Key | Description | Default (base) |
|-----|-------------|----------------|
| `ModelName` | Ollama model tag (`ollama pull ...`) | `gemma2:latest` |
| `OllamaUIBaseUrl` | Ollama API base URL | `http://127.0.0.1:11434` |
| `MaxContextSize` | Tokens (NumCtx passed to Ollama) | `4096` |
| `MaxContextMsgs` | Max retained messages (system + N) | `10` |
| `MaxAnswerRetries` | Retries when JSON parse fails | `3` |
| `JSONValidatorTemperature` | Temperature for validator stage | `0.2` |
| `DialogTemperature` | Temperature for main dialogue | `1.0` |
| `UseJSONValidator` | If true, second pass validation chat | `false` |
| `JSONValidatorSystemPromptPath` | Path to validator system prompt | `workflow/system-prompt-json-validator.md` |
| `MainSystemPromptPath` | Path to main system prompt | `workflow/system-prompt.md` |
| `ContextsPath` | Directory for chat context files | `contexts` |

### Image Settings (`Malyuvach:Image`)

| Key | Description | Example |
|-----|-------------|---------|
| `ComfyUIBaseUrl` | ComfyUI REST base | `http://127.0.0.1:8188` |
| `ImageIterationSteps` | Diffusion steps (applied to mapped node) | `8` |
| `ImageDefaultXDimension` | Default width (landscape) | `1328` |
| `ImageDefaultYDimension` | Default height (landscape) | `784` |
| `Workflow.ComfyUIWorkflowPath` | Workflow JSON used for API call | `workflow/Qwen-image-lora-8step.json` |
| `Workflow.*FieldId` | List of JSON pointer‑like identifiers to update inside workflow | see below |
| `Workflow.OutputNodeId` | Node id producing final image | `84` |

Field id strings correspond to nested lookup path `<nodeId>.inputs.<property>` inside the ComfyUI workflow JSON.

### Telegram Settings (`Malyuvach:Telegram`)

| Key | Description |
|-----|-------------|
| `BotKey` | Telegram bot token (use secret storage!) |
| `BotNames` | Variants for mention detection in groups |
| `BotShowRoomChannel` | Optional channel id / @name for reposting images |
| `SkipUpdates` | If true, drains backlog once at start |

### Discord Settings (`Malyuvach:Discord`)

| Key | Description |
|-----|-------------|
| `Enabled` | Enables Discord gateway client |
| `BotKey` | Discord bot token (use secret storage!) |
| `BotShowRoomChannelId` | Optional channel id to repost generated images |

### STT Settings (`Malyuvach:STT`)

| Key | Description | Default (code) |
|-----|-------------|---------------|
| `ModelPath` | Path to Whisper `.bin` model | `models/ggml-base.bin` (overridden in sample configs) |
| `UseGPU` | Enable GPU (if Whisper.Net built with CUDA) | `false` |
| `Language` | Target language OR `auto` for detection | `auto` |
| `Threads` | Processing threads (0 = auto) | `0` |
| `Translate` | Force translate non‑English → English | `false` |

If STT model file is missing the bot silently ignores voice messages.

### Minimal Production Overlay Example

```jsonc
// appsettings.Production.json
{
  "Malyuvach": {
    "LLM": { "ModelName": "gpt-oss:latest" },
    "Image": {
      "ComfyUIBaseUrl": "http://127.0.0.1:8188",
      "ImageIterationSteps": 8,
      "ImageDefaultXDimension": 1328,
      "ImageDefaultYDimension": 784,
      "Workflow": {
        "ComfyUIWorkflowPath": "workflow/Qwen-image-lora-8step.json",
        "PositivePromptFieldId": ["6.inputs.text"],
        "NegativePromptFieldId": ["7.inputs.text"],
        "ImageWidthFieldId": ["58.inputs.width"],
        "ImageHeightFieldId": ["58.inputs.height"],
        "NoiseSeedFieldId": ["3.inputs.seed"],
        "StepsFieldId": ["3.inputs.steps"],
        "OutputNodeId": "84"
      }
    },
    "Telegram": {
      "BotNames": ["@malyuvach_bot", "malyuvach", "малювач"],
      "BotKey": "REPLACE_ME",
      "SkipUpdates": true
    },
    "STT": {
      "ModelPath": "workflow/ggml-medium.bin",
      "UseGPU": false,
      "Language": "auto",
      "Threads": 8,
      "Translate": false
    }
  }
}
```

### Environment Variable Overrides

All settings can be overridden via standard .NET double underscore syntax:

```bash
export MALYUVACH__TELEGRAM__BOTKEY=123:ABC
export MALYUVACH__DISCORD__ENABLED=true
export MALYUVACH__DISCORD__BOTKEY=YOUR_DISCORD_TOKEN
export MALYUVACH__LLM__MODELNAME=gemma2:latest
export MALYUVACH__IMAGE__IMAGEITERATIONSTEPS=6
```

### Security Note

Never commit real `BotKey`. Use:

* User secrets (during dev): `dotnet user-secrets set "Malyuvach:Telegram:BotKey" "123:ABC"`
* Environment variables / secret managers in production.

For Discord you can set:

* User secrets: `dotnet user-secrets set "Malyuvach:Discord:BotKey" "YOUR_DISCORD_TOKEN"`

## 🤖 Telegram Interaction Rules

Bot answers when:

1. Private chat (always)
2. Group message contains any `BotNames` variant
3. Your message replies to a bot message

Orientation is chosen from model output; `portrait` swaps dimensions automatically.

## 💬 Discord Interaction Rules

Bot answers when:

1. Direct messages (always)
2. Channel message mentions the bot
3. Your message replies to a bot message

### Discord Gateway Requirements

To read message text in guild channels, Discord requires the privileged **Message Content Intent**.

1. Discord Developer Portal → Application → Bot → enable **MESSAGE CONTENT INTENT**
2. Invite the bot with permissions to read/write in the target channels (at least: View Channel + Send Messages)

If Message Content Intent is not enabled, the gateway may close the connection with `4014: Disallowed intent(s)`.

## 🧠 System Prompts

* `workflow/system-prompt.md` – main behavior & JSON output contract
* `workflow/system-prompt-json-validator.md` – optional second-pass normalizer (enable with `UseJSONValidator=true`)

## 🗂 Context Persistence

Each chat id → `contexts/<chatId>.json`. Context trimmed to `MaxContextMsgs` (system prompt retained). Delete a file to reset conversation.

## 🖼 ComfyUI Workflow Mapping

Provide a workflow JSON exported from ComfyUI. Identify nodes & fields to patch (text, width, height, seed, steps). Those paths are updated before POSTing to `/prompt`. The service polls `/history/{promptId}` until an image appears under the configured `OutputNodeId` then fetches via `/view`.

## 🎙 Speech To Text (Optional)

1. Download a Whisper model (e.g. `ggml-medium.bin`).
2. Place it at the configured `ModelPath`.
3. Set `STT` section (or leave default to disable gracefully).

Audio voice messages (Opus OGG) are converted to WAV in‑memory and streamed into Whisper.Net.

Discord note: Discord voice messages arrive as audio attachments (commonly `audio/ogg`). When a message has no text content, the bot will try to transcribe `.ogg/.opus` attachments and then process the transcription as if it was typed text.

## 🪵 Logging

Serilog writes to:

* Console (Debug+)
* `logs/log-<date>.txt` (rolling daily)

Adjust in `Serilog` section if needed.

## 🔄 LLM Answer Flow

1. Append user message
2. Stream tokens → aggregate answer
3. (Optional) validator pass
4. Parse JSON → if fail, rollback added messages & retry (up to `MaxAnswerRetries`)
5. Persist trimmed context

## 🧪 Testing Your Setup

* Pull model: `ollama pull gemma2:latest` (or your chosen model)
* Start ComfyUI with your workflow loaded & verify node IDs
* Run the bot & send: `@malyuvach_bot draw a serene cyberpunk river at dusk`

If only `text` is returned (no `prompt`), the bot replies with conversation text only.

## 🚧 Roadmap / Ideas

* Multi-image / variation requests
* Negative prompt generation heuristics
* Image to prompt (reverse) support
* Optional external storage for contexts (SQLite)
* Image editing (inpainting) support

---
Made with local AI tooling & ❤️. PRs welcome.
