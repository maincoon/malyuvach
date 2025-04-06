#!/usr/bin/env python3

import argparse
from faster_whisper import WhisperModel

# Use a smaller model for better performance and less tendency to translate
model_name = "medium"  # Options: tiny, base, small, medium, large


def transcribe_audio(filename, text_only=False, language="auto"):
    # Use a smaller compute_type for better performance
    model = WhisperModel(model_name, device="cpu", compute_type="int8")

    # If language is set to auto, let the model detect it
    lang_param = None if language == "auto" else language

    segments, info = model.transcribe(
        filename,
        beam_size=5,
        task="transcribe",  # Ensure we're transcribing, not translating
        language=lang_param
    )

    if not text_only:
        print(f"Transcribing: {filename}")
        print(
            f"Detected language: {info.language} with probability {info.language_probability:.2f}")

    for segment in segments:
        if text_only:
            print(segment.text)
        else:
            print("[%.2fs -> %.2fs] %s" %
                  (segment.start, segment.end, segment.text))


def main():
    parser = argparse.ArgumentParser(
        description="Transcribe audio files to text in the original language")
    parser.add_argument(
        "filename", help="Path to the audio file to transcribe")
    parser.add_argument("-t", "--text-only", action="store_true",
                        help="Output only the transcribed text without timing information")
    parser.add_argument("-l", "--language", default="auto",
                        help="Specify the language code (e.g., 'ru', 'en') or 'auto' for auto-detection")
    args = parser.parse_args()

    transcribe_audio(args.filename, args.text_only, args.language)


if __name__ == "__main__":
    main()
