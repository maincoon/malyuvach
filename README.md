# malyuvach

Telegram AI drawing bot.

## Installation

As usual dotnet core application, so one can run it with `dotnet run` or build it with `dotnet build`.

## Usage

One must have registered Telegram bot and its token. One can get it from [BotFather](https://core.telegram.org/bots#botfather).

One must create `appsettings.Production.json` file in the root of the project with all the necessary settings using template from `appsettings.json`.

For development purposes one can use `appsettings.Development.json` file.

## Choose the model

One can choose LLM and ComfyUI drawing workflow trough it's API depending on ones GPU and the desired quality of the drawing.

## System prompts

System prompts were created with GPT assistance. One can use provided prompts or create new ones. Depending on the LLM quality JSON validator may be disabled.