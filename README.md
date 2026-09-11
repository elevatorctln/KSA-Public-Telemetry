KSA Public Telemetry

---

This is a work-in-progress mod for Kitten Space Agency that creates a SpaceX-style launch broadcast telemetry overlay in-game. The overlay is populated with live data from the active vehicle and the mod includes an engine diagram generated automatically using the actual position of the engines on your stage relative to each other.

Toggle the overlay on and off using the numpad period key (I know). This can be configured in the config file.

## Features

- A broadcast-style bottom bar: speed and altitude on the left, T+ clock and mission timeline in the center, g-force and the engine diagram on the right.
- An engine diagram built from the real nozzle positions on your active stage, with a propellant arc gauge around it.
- Event notifications for certain in-flight events. I'm not sure if this will stick around since it's difficult to make it reliable, open to feedback.
- A mission timeline bar showing detected events behind the marker and any planned maneuvers ahead of it. Same deal as the event notifications.
- Freeze-on-signal-loss: if the vehicle stops being controllable, the overlay freezes at the last recieved values.
- Optionally hides the flight HUD while the overlay is up, and restores it afterwards.
- Every element is also available as its own movable, closable window.
- Fairly in-depth configuration avalible in the settlings menu.

## Installation

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

**Numpad period** toggles the overlay.

Settings are saved to `Documents/My Games/Kitten Space Agency/KSATelemetryOverlay/config.json`, which is safe to hand-edit.
