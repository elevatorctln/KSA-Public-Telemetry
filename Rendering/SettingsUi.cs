using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSATelemetryOverlay.Config;
using KSATelemetryOverlay.Telemetry;
using KSATelemetryOverlay.Telemetry.Server;

namespace KSATelemetryOverlay.Rendering;

public static class SettingsUi
{
    public static bool ModMenuActive { get; set; }
    private static OverlayConfig? _config;
    private static WindowHost? _windows;
    private static TelemetryServer? _server;
    private static ImInputString? _missionName;
    private static bool _windowOpen;
    private enum KeySlot : byte { None, Toggle, Settings, Countdown }
    private static KeySlot _capturing;
    public static bool IsCapturingKey => _capturing != KeySlot.None;

    private static readonly ImGuiKey[] _modifierKeys =
    [
        ImGuiKey.LeftCtrl, ImGuiKey.LeftShift, ImGuiKey.LeftAlt, ImGuiKey.LeftSuper,
        ImGuiKey.RightCtrl, ImGuiKey.RightShift, ImGuiKey.RightAlt, ImGuiKey.RightSuper,
    ];

    private static readonly (string Label, string Field)[] _editableColors =
    [
        ("Highlight / arc fill", nameof(OverlayStyle.ArcFill)),
        ("Arc track",            nameof(OverlayStyle.ArcTrack)),
        ("Caution",              nameof(OverlayStyle.Caution)),
        ("Critical",             nameof(OverlayStyle.Critical)),
        ("Primary text",         nameof(OverlayStyle.TextPrimary)),
        ("Secondary text",       nameof(OverlayStyle.TextMuted)),
        ("Dim text",             nameof(OverlayStyle.TextDim)),
        ("Engine - nominal",     nameof(OverlayStyle.EngineNominal)),
        ("Engine - throttled",   nameof(OverlayStyle.EngineThrottled)),
        ("Engine - armed",       nameof(OverlayStyle.EngineArmed)),
        ("Engine - starved",     nameof(OverlayStyle.EngineStarved)),
        ("Engine - inactive",    nameof(OverlayStyle.EngineInactive)),
        ("Backdrop fade",        nameof(OverlayStyle.ShelfBottom)),
        ("Shelf plates",         nameof(OverlayStyle.ShelfBox)),
        ("Gauge plate",          nameof(OverlayStyle.GaugePlate)),
        ("Gauge rim",            nameof(OverlayStyle.GaugeRim)),
        ("Timeline - elapsed",   nameof(OverlayStyle.TimelinePast)),
        ("Timeline - upcoming",  nameof(OverlayStyle.TimelineFuture)),
    ];

    public static void Bind(OverlayConfig config, WindowHost windows, TelemetryServer? server = null)
    {
        _config = config;
        _windows = windows;
        _server = server;
        _missionName = new ImInputString(64, config.MissionName ?? string.Empty);
    }

    [ModMenuEntry("Telemetry Overlay", nameof(ModMenuActive))]
    public static void DrawSettingsMenu()
    {
        if (_config is null)
        {
            ImGui.TextDisabled("Overlay is still starting up."u8);
            return;
        }

        DrawContent(_config, inMenu: true);
    }

    public static void DrawFallbackWindow()
    {
        if (_config is null || !_windowOpen)
        {
            return;
        }

        ImGui.SetNextWindowSize(new float2(360f, 0f), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Telemetry Overlay"u8, ref _windowOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!ModMenuActive)
            {
                ImGui.TextDisabled("Install ModMenu to move these settings into a nice dedicated Mods menu."u8);
                ImGui.Separator();
            }

            DrawContent(_config, inMenu: false);
        }

        ImGui.End();
    }

    public static void ToggleWindow() => _windowOpen = !_windowOpen;

    private static bool Category(ImString label, bool inMenu)
        => inMenu ? ImGui.BeginMenu(label) : ImGui.CollapsingHeader(label);

    private static void EndCategory(bool inMenu)
    {
        if (inMenu)
        {
            ImGui.EndMenu();
        }
    }

    private static void DrawContent(OverlayConfig config, bool inMenu)
    {
        bool changed = false;

        if (_missionName is { } buffer)
        {
            ImGui.Text("Mission name"u8);
            ImGui.SetNextItemWidth(200f);

            if (ImGui.InputText("##missionName"u8, buffer))
            {
                string typed = buffer.ToString();
                config.MissionName = string.IsNullOrWhiteSpace(typed) ? null : typed;
                changed = true;
            }

            ImGui.TextDisabled("Blank uses the vehicle name."u8);
        }

        changed |= DrawCountdown(config);
        changed |= ImGui.Checkbox("Enabled"u8, ref config.Enabled);
        changed |= ImGui.Checkbox("Hide the game flight HUD"u8, ref config.ReplaceFlightUi);
        changed |= ImGui.Checkbox("Stay visible when F2 hides the UI"u8, ref config.ShowWhenGameUiHidden);

        if (!HiddenUiPatch.Installed)
        {
            ImGui.TextDisabled("F2 will hide the overlay too:"u8);
            ImGui.TextDisabled(HiddenUiPatch.Failure ?? "the hook did not install");
        }

        ImGui.Separator();

        if (Category("Elements"u8, inMenu))
        {
            changed |= ImGui.Checkbox("Readouts"u8, ref config.ShowTelemetryBar);
            changed |= ImGui.Checkbox("Engine diagram"u8, ref config.ShowEngineDiagram);
            ImGui.SetNextItemWidth(160f);
            changed |= ImGui.SliderFloat("Diagram rotation"u8, ref config.EngineDiagramRotation,
                OverlayConfig.MinEngineRotation, OverlayConfig.MaxEngineRotation, "%.0f deg"u8);
            changed |= ImGui.Checkbox("Propellant arc"u8, ref config.ShowPropellants);
            changed |= ImGui.Checkbox("Mission clock"u8, ref config.ShowMissionClock);
            changed |= ImGui.Checkbox("Timeline"u8, ref config.ShowTimeline);
            changed |= ImGui.Checkbox("Event Notifications"u8, ref config.ShowNotifications);
            changed |= ImGui.Checkbox("Backdrop band"u8, ref config.ShowBackdrop);
            EndCategory(inMenu);
        }

        if (Category("Readouts"u8, inMenu))
        {
            changed |= DrawSlotEditor("Left"u8, "left", ref config.LeftSlots, config);
            changed |= DrawSlotEditor("Right"u8, "right", ref config.RightSlots, config);
            changed |= DrawSpeedReference(config);
            changed |= ImGui.Checkbox("Terrain-relative altitude"u8, ref config.TerrainRelativeAltitude);

            ImGui.TextDisabled("Arc full scales"u8);
            ImGui.SetNextItemWidth(160f);
            changed |= ImGui.SliderFloat("Speed"u8, ref config.SpeedArcFullScale,
                OverlayConfig.MinSpeedArc, OverlayConfig.MaxSpeedArc, "%.0f m/s"u8);
            ImGui.SetNextItemWidth(160f);
            changed |= ImGui.SliderFloat("Altitude"u8, ref config.AltitudeArcFullScaleKm,
                OverlayConfig.MinAltitudeArc, OverlayConfig.MaxAltitudeArc, "%.0f km"u8);
            ImGui.SetNextItemWidth(160f);
            changed |= ImGui.SliderFloat("G-force"u8, ref config.GForceArcFullScale,
                OverlayConfig.MinGForceArc, OverlayConfig.MaxGForceArc, "%.1f g"u8);
            EndCategory(inMenu);
        }

        if (Category("Events"u8, inMenu))
        {
            ImGui.TextDisabled("Announce as a callout"u8);
            changed |= DrawNotificationToggles(config);
            ImGui.Separator();

            ImGui.SetNextItemWidth(160f);
            changed |= ImGui.SliderFloat("Timeline window"u8, ref config.TimelineWindowSeconds,
                OverlayConfig.MinTimelineWindow, OverlayConfig.MaxTimelineWindow, "%.0f s"u8);
            EndCategory(inMenu);
        }

        if (Category("Appearance"u8, inMenu))
        {
            ImGui.SetNextItemWidth(160f);
            changed |= ImGui.SliderFloat("Scale"u8, ref config.Scale,
                OverlayConfig.MinScale, OverlayConfig.MaxScale, "%.2f"u8);
            ImGui.SetNextItemWidth(160f);
            changed |= ImGui.SliderFloat("Opacity"u8, ref config.Opacity,
                OverlayConfig.MinOpacity, OverlayConfig.MaxOpacity, "%.2f"u8);
            ImGui.SetNextItemWidth(160f);
            changed |= ImGui.SliderFloat("Smoothing"u8, ref config.SmoothingSeconds,
                OverlayConfig.MinSmoothing, OverlayConfig.MaxSmoothing, "%.2f s"u8);

            changed |= ImGui.Checkbox("Intro animation"u8, ref config.PlayIntro);

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Sweeps the bar in when the overlay appears."u8);
            }

            changed |= ImGui.Checkbox("Outro animation"u8, ref config.PlayOutro);

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Plays the reveal backwards when hidden, instead of cutting away."u8);
            }

            if (inMenu)
            {
                if (ImGui.BeginMenu("Colours"u8))
                {
                    changed |= DrawColorEditors();
                    ImGui.EndMenu();
                }
            }
            else
            {
                ImGui.SeparatorText("Colours"u8);
                changed |= DrawColorEditors();
            }

            EndCategory(inMenu);
        }

        if (Category("Behaviour"u8, inMenu))
        {
            changed |= DrawKeyBinding("Toggle overlay"u8, ref config.ToggleKey, KeySlot.Toggle);
            changed |= DrawKeyBinding("Open settings"u8, ref config.SettingsKey, KeySlot.Settings);
            changed |= DrawKeyBinding("Start countdown"u8, ref config.CountdownKey, KeySlot.Countdown);
            changed |= ImGui.Checkbox("Hide while on rails"u8, ref config.HideOnRails);
            changed |= ImGui.Checkbox("Status when no vehicle"u8, ref config.ShowStatusWhenIdle);

            ImGui.SetNextItemWidth(160f);
            changed |= ImGui.SliderFloat("Freeze at load"u8, ref config.FreezeAtToleranceFraction,
                OverlayConfig.MinFreezeTolerance, OverlayConfig.MaxFreezeTolerance, "%.2f"u8);
            ImGui.TextDisabled("Freezes once g-load or dynamic pressure reaches this much of"u8);
            ImGui.TextDisabled("what the vehicle can take, so the last values are from before"u8);
            ImGui.TextDisabled("it came apart. 1.00 waits for the breakup itself."u8);
            EndCategory(inMenu);
        }

        if (Category("Windows"u8, inMenu))
        {
            DrawWindowToggles();
            EndCategory(inMenu);
        }

        if (Category("OBS / Webcast"u8, inMenu))
        {
            changed |= DrawObsSettings(config);
            EndCategory(inMenu);
        }

        if (Category("Advanced"u8, inMenu))
        {
            bool tuning = TuningUi.IsOpen;
            if (ImGui.Checkbox("Visual tuning window"u8, ref tuning))
            {
                TuningUi.SetOpen(tuning);
            }

            if (ImGui.SmallButton("Save now"u8))
            {
                ConfigStore.SaveNow();
            }

            ImGui.TextDisabled(ConfigStore.FilePath);
            EndCategory(inMenu);
        }

        if (changed)
        {
            ConfigStore.MarkDirty();
        }
    }

    private static bool DrawObsSettings(OverlayConfig config)
    {
        bool changed = false;

        changed |= ImGui.Checkbox("Enable OBS browser source server"u8, ref config.EnableObsServer);

        ImGui.Spacing();
        ImGui.TextDisabled("Display Mode"u8);

        bool inGameOnly = config.DisplayMode == OverlayDisplayMode.InGameOnly;
        if (ImGui.RadioButton("In-game only"u8, inGameOnly))
        {
            config.DisplayMode = OverlayDisplayMode.InGameOnly;
            changed = true;
        }
        ImGui.SameLine();
        bool obsOnly = config.DisplayMode == OverlayDisplayMode.ObsOnly;
        if (ImGui.RadioButton("OBS only"u8, obsOnly))
        {
            config.DisplayMode = OverlayDisplayMode.ObsOnly;
            changed = true;
        }
        ImGui.SameLine();
        bool both = config.DisplayMode == OverlayDisplayMode.Both;
        if (ImGui.RadioButton("Both"u8, both))
        {
            config.DisplayMode = OverlayDisplayMode.Both;
            changed = true;
        }

        if (config.DisplayMode == OverlayDisplayMode.ObsOnly)
        {
            ImGui.TextColored(new float4(0.4f, 0.8f, 1f, 1f), "In-game overlay hidden; stock KSA HUD stays on screen."u8);
        }

        ImGui.Spacing();

        ImGui.SetNextItemWidth(120f);
        int port = config.ObsServerPort;
        if (ImGui.InputInt("Server port"u8, ref port, 1, 100))
        {
            config.ObsServerPort = Math.Clamp(port, 1024, 65535);
            changed = true;
        }

        if (_server is not null)
        {
            ImGui.TextDisabled("Server status:"u8);
            ImGui.SameLine();
            if (_server.IsRunning)
            {
                ImGui.TextColored(new float4(0.2f, 1f, 0.4f, 1f), _server.StatusText);
            }
            else if (_server.StatusText.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            {
                ImGui.TextColored(new float4(1f, 0.4f, 0.4f, 1f), _server.StatusText);
            }
            else
            {
                ImGui.TextColored(new float4(0.7f, 0.7f, 0.7f, 1f), _server.StatusText);
            }

            ImGui.SameLine();
            if (ImGui.Button("Restart Server"u8))
            {
                _server.Restart();
            }
        }

        ImGui.TextDisabled("In OBS, add a Browser Source pointing to:"u8);
        ImGui.TextColored(new float4(1f, 0.9f, 0.3f, 1f), $"http://127.0.0.1:{config.ObsServerPort}/");
        ImGui.TextDisabled("Recommended size: 1920x1080 (transparent background)."u8);

        return changed;
    }

    private static bool DrawCountdown(OverlayConfig config)
    {
        bool changed = false;

        ImGui.SetNextItemWidth(100f);
        changed |= ImGui.SliderFloat("##countdownSeconds"u8, ref config.CountdownSeconds,
            OverlayConfig.MinCountdown, OverlayConfig.MaxCountdown, "T-%.0f s"u8);

        ImGui.SameLine();

        bool counting = TelemetrySampler.IsCountingDown;

        if (!TelemetrySampler.CanCountDown)
        {
            ImGui.BeginDisabled();
            ImGui.SmallButton("Start"u8);
            ImGui.EndDisabled();

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Needs a vehicle that has not lifted off yet."u8);
            }

            return changed;
        }

        if (ImGui.SmallButton(counting ? "Cancel"u8 : "Start"u8))
        {
            if (counting)
            {
                TelemetrySampler.CancelCountdown();
            }
            else
            {
                TelemetrySampler.StartCountdown(config.CountdownSeconds);
            }
        }

        return changed;
    }

    private static bool DrawSpeedReference(OverlayConfig config)
    {
        bool changed = false;

        ImGui.SetNextItemWidth(160f);

        if (!ImGui.BeginCombo("Speed reference"u8, SpeedReferenceLabel(config.SpeedReference)))
        {
            return false;
        }

        foreach (SpeedReference reference in Enum.GetValues<SpeedReference>())
        {
            if (ImGui.Selectable(SpeedReferenceLabel(reference), reference == config.SpeedReference))
            {
                config.SpeedReference = reference;
                changed = true;
            }
        }

        ImGui.EndCombo();
        return changed;
    }

    private static ReadOnlySpan<char> SpeedReferenceLabel(SpeedReference reference) => reference switch
    {
        SpeedReference.Orbital => "Orbital".AsSpan(),
        SpeedReference.Auto    => "Auto (orbital above atmosphere)".AsSpan(),
        _                      => "Surface".AsSpan(),
    };

    private static bool DrawNotificationToggles(OverlayConfig config)
    {
        bool changed = false;

        foreach (MissionEventKind kind in Enum.GetValues<MissionEventKind>())
        {
            if (kind == MissionEventKind.PlannedBurn)
            {
                continue;
            }

            MissionEvent sample = new(kind, 0.0);

            if (!sample.DeservesCallout)
            {
                continue;
            }

            bool announced = !config.MutedNotifications.Contains(kind);

            if (!ImGui.Checkbox(sample.Label, ref announced))
            {
                continue;
            }

            if (announced)
            {
                config.MutedNotifications.Remove(kind);
            }
            else if (!config.MutedNotifications.Contains(kind))
            {
                config.MutedNotifications.Add(kind);
            }

            changed = true;
        }

        return changed;
    }

    private static bool DrawKeyBinding(ImString label, ref ImGuiKey key, KeySlot slot)
    {
        bool changed = false;

        ImGui.PushID((int)slot);
        ImGui.SetNextItemWidth(160f);

        if (_capturing == slot)
        {
            ImGui.Button("Press a key... (Esc cancels)"u8, new float2(200f, 0f));

            if (!ImGui.GetIO().WantTextInput)
            {
                ImGuiKey pressed = PollPressedKey();

                if (pressed == ImGuiKey.Escape)
                {
                    _capturing = KeySlot.None;
                }
                else if (pressed != ImGuiKey.None)
                {
                    key = pressed;
                    _capturing = KeySlot.None;
                    changed = true;
                }
            }
        }
        else if (ImGui.Button(key.ToString(), new float2(200f, 0f)))
        {
            _capturing = slot;
        }

        ImGui.SameLine();
        ImGui.Text(label);
        ImGui.PopID();

        return changed;
    }

    private static ImGuiKey PollPressedKey()
    {
        for (ImGuiKey key = ImGuiKey.NamedKey_BEGIN; key <= ImGuiKey.Oem102; key++)
        {
            if (Array.IndexOf(_modifierKeys, key) >= 0)
            {
                continue;
            }

            if (ImGui.IsKeyPressed(key, repeat: false))
            {
                return key;
            }
        }

        return ImGuiKey.None;
    }

    private static void DrawWindowToggles()
    {
        if (_windows is null || !_windows.IsBuilt)
        {
            ImGui.TextDisabled("Available once the game UI is up."u8);
            return;
        }

        IReadOnlyList<TelemetryWindow> windows = _windows.Windows;

        for (int i = 0; i < windows.Count; i++)
        {
            TelemetryWindow window = windows[i];
            bool open = window.IsShown;

            if (ImGui.Checkbox(window.DisplayTitle, ref open))
            {
                window.SetShown(open);
                ConfigStore.MarkDirty();
            }
        }

        if (ImGui.SmallButton("Close all"u8))
        {
            _windows.HideAll();
            ConfigStore.MarkDirty();
        }
    }

    private static bool DrawColorEditors()
    {
        bool changed = false;

        for (int i = 0; i < _editableColors.Length; i++)
        {
            (string label, string field) = _editableColors[i];

            uint packed = OverlayPalette.Get(field);
            OverlayStyle.Unpack(packed, out float r, out float g, out float b, out _);

            float3 rgb = new(r, g, b);

            ImGui.SetNextItemWidth(140f);
            if (ImGui.ColorEdit3(label, ref rgb, ImGuiColorEditFlags.NoInputs))
            {
                OverlayPalette.Set(field, OverlayStyle.WithRgb(packed, rgb.X, rgb.Y, rgb.Z));
                changed = true;
            }
        }

        if (ImGui.SmallButton("Reset colours"u8))
        {
            OverlayPalette.ResetAll();
            changed = true;
        }

        return changed;
    }

    private static bool DrawSlotEditor(
        ImString heading, string idSuffix, ref ReadoutKind[] slots, OverlayConfig config)
    {
        bool changed = false;

        ImGui.Text(heading);

        int count = slots.Length;
        ImGui.SetNextItemWidth(120f);

        if (ImGui.SliderInt($"##count_{idSuffix}", ref count, 1, OverlayConfig.MaxSlotsPerSide))
        {
            ReadoutKind[] resized = new ReadoutKind[count];

            for (int i = 0; i < count; i++)
            {
                resized[i] = i < slots.Length ? slots[i] : ReadoutKind.Speed;
            }

            slots = resized;
            changed = true;
        }

        for (int i = 0; i < slots.Length; i++)
        {
            ImGui.SetNextItemWidth(160f);

            if (!ImGui.BeginCombo($"##slot_{idSuffix}_{i}", ReadoutPanel.LabelFor(slots[i])))
            {
                continue;
            }

            foreach (ReadoutKind kind in Enum.GetValues<ReadoutKind>())
            {
                if (ImGui.Selectable(ReadoutPanel.LabelFor(kind), kind == slots[i]))
                {
                    slots[i] = kind;
                    changed = true;
                }
            }

            ImGui.EndCombo();
        }

        if (changed)
        {
            config.MarkStructuralChange();
        }

        return changed;
    }
}
