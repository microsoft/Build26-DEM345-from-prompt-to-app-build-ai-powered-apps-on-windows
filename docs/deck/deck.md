---
title: "From Prompt to App"
author: "Microsoft Build 2026"
plainText: true
---

+++ layout = "center"

# From Prompt to App

## Build AI-powered apps on Windows

+++

# From Prompt to App

@column

![lei](images/lei.jpg)
> Lei Xu, 
> Program Manager


@column

![nikola](images/nikola.jpg)
> Nikola Metulev, 
> Software Engineer

+++ layout = "center", notes = "One line. Set the frame. The whole talk is about building and iterating on real Windows apps with agents."

# Agenda

@click
## Act 1 - Build with Agents

@click
## Act 2 - Make it real with AI platform integrations

+++ layout = "center", notes = "DEMO — 3 min. Walk through Contoso Studio. Joke: 'look for it in a Microsoft Store near you.' Just walk the scenario — import media, run pipeline, show artifacts."

# Demo: Contoso Studio

> Coming to a Microsoft Store near you 😉

+++ spacing = "1", notes = "2 min. We used the skills to build this and iterate — we didn't really know what it was going to be when we started. Iteration with agents let us change our mind cheaply. (HTML/screenshot slide showing iterations.)"

# We didn't know what it would be

## We built it with skills — and changed our minds a lot

@click
- Started as "a video tool"
@click
- Became a node-based pipeline
@click
- Then a creator workflow with AI effects
@click
@color(cyan)
> Agents make it cheap to be wrong.
@click
@color(cyan)
- Let's take a look at the journey @link(contoso-studio-journey/journey.html, "Open the journey")

+++ spacing = "1", notes = "1 min. Recap what makes this possible. Don't dwell — just name the pieces and tell people to try it."

# What makes this possible

@click
- **WinUI agent plugin** — Copilot skills for WinUI app development
@click
- **WinApp CLI** — packaging, identity, run, debug, UI automation
@click
- **dotnet new winui templates** — start from a real WinUI project
@click
- **winui-search, winmd-cli, Roslyn analyzers** — the small tools that fill the gaps

+++ layout = "columns", spacing = "1", notes = "1 min. Each tool maps to something a human developer already does. Agents need the same affordances we do."

# Agents need what humans need

@column

@color(gray)
### How humans build apps

- File → New
- F5 to run
- copy sample code
- IntelliSense
- stack traces, debug output
- lint

@column

@color(cyan)
### What agents use

- `dotnet new winui`
- `winapp run`
- `winui-search`
- `winmd-cli`
- `winapp run --debug-output`
- Roslyn analyzers

@click
@color(white)
> Same affordances. Just exposed to the terminal.

+++ layout = "center", notes = "DEMO — 2 min. Manually: dotnet new winui, winapp run --debug-output, winui-search for a sample. Show how an agent would use the same tools."

# Demo: how agents use these tools

> dotnet new winui
> winapp run --debug-output
> winui-search

+++ spacing = "1", notes = "1 min. The APIs are what make this real. Name the platform pieces actually powering Contoso Studio."

# What makes the app real

## The platform underneath Contoso Studio

@click
- **Windows App SDK** and **WinUI** — platform integrations and native Windows UI
@click
- **Windows ML** — local models (Whisper, silence detection, classifiers)
@click
- **Windows AI APIs** — text intelligence, image, summarization
@click
- **Foundry Local** — local LLMs on the same box

@click
@color(cyan)
> Local-first. Hardware-aware. Native.

+++ layout = "center", notes = "DEMO — 5 min. Pick an effect that finishes in <5 min and uses the winml CLI. Kick off the prompt, then switch to slides while it runs."

# Demo: add a new effect

> Prompt the agent to add it. Let it cook.

+++ spacing = "1", notes = "While the prompt runs — walk the WinML CLI flow. This is the technical credibility moment."

# WinML CLI

## Optimize a model for this machine

- @link(winml/winml-cli.html, "Open the WinML CLI overview")

@click
- Pick a model
@click
- Optimize for CPU / GPU / NPU
@click
- Register it as a capability the app can use

@click
@color(cyan)
> Model  →  optimized  →  pipeline effect.

+++ layout = "center", notes = "DEMO — 2 min. Back to Contoso Studio. Hopefully the effect is done. Have a backup recording if not. Show what the agent actually changed."

# Demo: the new effect, live

> See what the agent did. Drop it into the pipeline.

+++ spacing = "1"

# Recap

@click
- **Agents** make iterating on Windows apps cheap
> POC in minutes, not days
> Change your mind cheaply
> "What if…" becomes a prompt, not a sprint
> Make it real quickly when you find the idea

@click
- **WinApp CLI + skills + templates + tools** give agents the same affordances humans have

@click
- **Windows ML + Windows AI + Foundry Local** make the app real

@click
@color(cyan)
> Windows shortens the path from prompt to product.

+++ layout = "center", notes = "ONE MORE THING. We're on a device that can run big local LLMs. Use them with the skills to do things completely offline. Demo: UI-test the app fully offline."

# One more thing

## This machine can run a big local LLM ...

+++ spacing = "1", notes = "Recap the value of iterating with agents. Quick, punchy.", hidden = "true"

# Why iterate with agents

+++ spacing = "1"

# Thank you

Please complete the session evaluation:

TODO: update from build slide templates

## aka.ms/winui-skills
## aka.ms/todo-other links
