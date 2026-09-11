## KSA Broadcast Telemetry Overlay

---

This is a work-in-progress mod for Kitten Space Agency that creates a SpaceX-style launch broadcast telemetry overlay in-game. The overlay is populated with live data from the active vehicle and the mod includes an engine diagram generated automatically using the actual position of the engines on your stage relative to each other.

I am completely open to contributions and suggestions and certainly issue reports. I'll get to that stuff as quick as I can, but I can't promise being super fast.

## Features

- A broadcast-style bottom bar: speed and altitude on the left, T+ clock and mission timeline in the center, g-force and the engine diagram on the right. Styled to look like the SpaceX webcast overlay slick animations and all.
- An engine diagram built from the real nozzle positions on your active stage, with a propellant arc gauge around it.
- Event notifications for certain in-flight events. Be warned that this is not totally reliable, maybe I'll make it a toggle or something.
- A mission timeline bar showing detected events behind the marker and any planned maneuvers ahead of it. Same deal as the event notifications.
- Freeze-on-signal-loss: if the vehicle stops being controllable, the overlay freezes at the last recieved values.
- Optionally and by default hides the flight UI while the overlay is up, and restores it afterwards.
- Every element is also available as its own movable imgui window that behaves like any other.
- Fairly in-depth configuration avalible in the settlings menu.

## Installation

Download from Releases or from [https://spacedock.info/mod/4552/Webcast%20Telemetry%20Overlay](Spacedock).

Extract into `Documents/My Games/Kitten Space Agency/mods/` and add to `manifest.toml`:

```toml
[[mods]]
id = "KSATelemetryOverlay"
enabled = true
```

[StarMap](https://github.com/StarMapLoader/StarMap) is required for this mod. Follow the instructions for installing it and launch the game through StarMap 
when using this mod.

## Maintainance

This feels important for me to be clear about: KSA is in pre-alpha and this mod uses unofficial code mod loading using undocumented methods. This mod will break very regularly while the game is still in this phase of development and I cannot promise to fix it for every single build. When it breaks I will try to get it working again when I have the time, I just cannot promise when that will be at any given time. Your patience is greatly appreciated!

## Settings

If [ModMenu](https://github.com/MrJeranimo/ModMenu) is installed, settings appear under **Mods > Telemetry Overlay**. ModMenu is optional. Without it, press **numpad slash** for the same settings in a standalone window.

Toggle the overlay on and off using the **numpad period** key (I know). This can be configured in the config file.

Settings are saved to `Documents/My Games/Kitten Space Agency/KSATelemetryOverlay/config.json`, which is safe to hand-edit.

## Me

I'm not sure if this is weird to do in a readme, but I figured it makes sense to put here where you can find me elsewhere! I have a [linktree](https://linktr.ee/caitlynlh) which seems like the most convienient way.
