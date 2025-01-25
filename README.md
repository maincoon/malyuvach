# malyuvach

Telegram AI drawing bot in short.

## Requirements

* Requires dotnet 8.
* Requires [ollama API](https://ollama.com/)
* Requires [ComfyUI API](https://comfyui.com/)


## Installation

One must clone GIT repository, build project using dotnet CLI and publish it:

```bash
git clone https://github.com/maincoon/malyuvach.git
cd malyuvach
dotnet publish -c Release -o release Malyuvach.csproj
```

Copy `appsettings.json` and overwrite settings in `appsettings.Production.json` to match your environment. One may use **DOTNET_USE_POLLING_FILE_WATCHER** environment variable to fix issues with file watching on some systems.

```bash
$ cp appsettings.json release/
$ cd release
$ export DOTNET_USE_POLLING_FILE_WATCHER=1
$ ./Malyuvach
```

## Usage

One must have registered Telegram bot and its token. One can get it from [BotFather](https://core.telegram.org/bots#botfather).

One must create `appsettings.Production.json` file in the root of the project with all the necessary settings using template from `appsettings.json`.

For development purposes one can use `appsettings.Development.json` file.

## Choose the models

One can choose LLM and ComfyUI drawing workflow trough it's API depending on ones GPU and the desired quality of the drawing. To use new ComfyUI workflows one must update appsettings.json with the new workflow path and field ids in **Workflows** section.

Any ComfyUI workflow can be adapted as long as it contains nodes with fields that have the following purposes:

* **PositivePromptFieldId** - text field for positive prompt
* **NegativePromptFieldId** - text field for negative prompt
* **ImageWidthFieldId** - width of the image to generate
* **ImageHeightFieldId** - height of the image to generate
* **NoiseSeedFieldId** - seed for noise generation
* **StepsFieldId** - number of steps in diffusion process (very dependent on model)
* **OutputNodeId** - *Image preview* node id where output image is generated

Sample workflow JSON:

```json
"Workflow": {
    "ComfyUIWorkflowPath": "workflow/workflow_api-flux-schnell.json",
    "PositivePromptFieldId": [
        "6.inputs.text"
    ],
    "NegativePromptFieldId": [
        "7.inputs.text"
    ],
    "ImageWidthFieldId": [
        "5.inputs.width"
    ],
    "ImageHeightFieldId": [
        "5.inputs.height"
    ],
    "NoiseSeedFieldId": [
        "25.inputs.noise_seed"
    ],
    "StepsFieldId": [
        "17.inputs.steps"
    ],
    "OutputNodeId": "26"
}
```

Provided sample workflow with ```gemma2:latest``` and ```Pixelwave_flux1_schnell_Q6_K_M_03``` models requires about **24GB** VRAM.


## System prompts

System prompts were created with GPT assistance. One can use provided prompts or create new ones. Depending on the LLM quality JSON validator may be disabled.