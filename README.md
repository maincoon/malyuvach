# malyuvach

Telegram AI drawing bot that combines LLM for prompt generation with ComfyUI for image creation. The bot uses LLM to understand user requests and generate appropriate prompts for image generation.

## Requirements

* .NET 8 SDK
* [Ollama](https://ollama.com/) for LLM functionality (default port: 11434)
* [ComfyUI](https://github.com/comfyanonymous/ComfyUI) for image generation (default port: 8188)

### Hardware Requirements

* For Gemma 2B model: ~4GB VRAM
* For Pixelwave Flux workflow: ~24GB VRAM (for default 1200x800 resolution)

## Installation

Clone the repository and build the project:

```bash
git clone https://github.com/maincoon/malyuvach.git
cd malyuvach
dotnet build
dotnet publish -c Release -o release
```

Copy configuration files and start the application:

```bash
cp appsettings.json release/
cd release
export DOTNET_USE_POLLING_FILE_WATCHER=1
./Malyuvach
```

## Configuration

The application uses the following configuration hierarchy:

1. `appsettings.json` - base configuration template
2. `appsettings.Development.json` - for development environment
3. `appsettings.Production.json` - for production environment

### Required Configuration

Create `appsettings.Production.json` with the following essential settings:

```json
{
  "Malyuvach": {
    "LLM": {
      "OllamaUIBaseUrl": "http://127.0.0.1:11434"
    },
    "Image": {
      "ComfyUIBaseUrl": "http://127.0.0.1:8188"
    },
    "Telegram": {
      "BotKey": "YOUR_BOT_TOKEN",
      "BotNames": ["YOUR_BOT_NAME"],
      "BotShowRoomChannel": "OPTIONAL_CHANNEL_ID"
    }
  }
}
```

### Telegram Bot Setup

1. Create a new bot through [BotFather](https://core.telegram.org/bots#botfather)
2. Get the bot token and add it to your configuration
3. Optionally set up a showcase channel where the bot will post generated images

## System Prompts

The bot uses two types of system prompts located in the `workflow` directory:

* `system-prompt.md` - main prompt for user interaction and image prompt generation
* `system-prompt-json-validator.md` - optional JSON validator prompt (can be disabled in settings)

User conversation contexts are stored in the `contexts` directory (configurable through settings).

## ComfyUI Workflow Configuration

The bot supports custom ComfyUI workflows. Configure workflow node IDs in the settings:

```json
"Workflow": {
    "ComfyUIWorkflowPath": "workflow/workflow_api-flux-schnell.json",
    "PositivePromptFieldId": ["6.inputs.text"],
    "NegativePromptFieldId": ["7.inputs.text"],
    "ImageWidthFieldId": ["5.inputs.width"],
    "ImageHeightFieldId": ["5.inputs.height"],
    "NoiseSeedFieldId": ["25.inputs.noise_seed"],
    "StepsFieldId": ["17.inputs.steps"],
    "OutputNodeId": "26"
}
```

Any ComfyUI workflow can be used as long as it provides nodes with these field purposes:

* Positive prompt input
* Negative prompt input
* Image dimensions (width/height)
* Noise seed for randomization
* Steps count for the diffusion process
* Output node for the final image

The default configuration uses Gemma 2B for LLM and Pixelwave Flux workflow for image generation, requiring approximately 24GB VRAM for default image resolution (1200x800).

## Architecture

The application consists of three main services:

* LLM Service - handles communication with Ollama API for text generation
* Image Service - manages ComfyUI workflow execution and image generation
* Telegram Service - provides bot interface and user interaction

Each service has its own configuration section in appsettings.json for easy customization.
