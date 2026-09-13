using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSATelemetryOverlay.Config;

namespace KSATelemetryOverlay.Rendering;

public static class SettingsUi
{
    public static bool ModMenuActive { get; set; }
    private static OverlayConfig? _config;
    private static WindowHost? _windows;
    private static ImInputString? _missionName;
    private static bool _windowOpen;
    private enum KeySlot : byte { None, Toggle, Settings }
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

    public static void Bind(OverlayConfig config, WindowHost windows)
    {
        _config = config;
        _windows = windows;
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

    private static void DrawContent(OverlayConfig config, bool inMenu)
    {
        bool changed = false;

        ImGui.SeparatorText("Overlay"u8);
        changed |= ImGui.Checkbox("Enabled"u8, ref config.Enabled);
        changed |= ImGui.Checkbox("Hide the game flight HUD"u8, ref config.ReplaceFlightUi);
        changed |= ImGui.Checkbox("Stay visible when F2 hides the UI"u8, ref config.ShowWhenGameUiHidden);

        if (!HiddenUiPatch.Installed)
        {
            ImGui.TextDisabled("F2 will hide the overlay too:"u8);
            ImGui.TextDisabled(HiddenUiPatch.Failure ?? "the hook did not install");
        }
        changed |= ImGui.Checkbox("Backdrop band"u8, ref config.ShowBackdrop);
        changed |= ImGui.Checkbox("Hide while on rails"u8, ref config.HideOnRails);
        changed |= ImGui.Checkbox("Status when no vehicle"u8, ref config.ShowStatusWhenIdle);

        ImGui.SeparatorText("Hotkeys"u8);
        changed |= DrawKeyBinding("Toggle overlay"u8, ref config.ToggleKey, KeySlot.Toggle);
        changed |= DrawKeyBinding("Open settings"u8, ref config.SettingsKey, KeySlot.Settings);

        ImGui.SeparatorText("Elements"u8);
        changed |= ImGui.Checkbox("Readouts"u8, ref config.ShowTelemetryBar);
        changed |= ImGui.Checkbox("Engine diagram"u8, ref config.ShowEngineDiagram);
        changed |= ImGui.Checkbox("Propellant arc"u8, ref config.ShowPropellants);
        changed |= ImGui.Checkbox("Mission clock"u8, ref config.ShowMissionClock);
        changed |= ImGui.Checkbox("Timeline"u8, ref config.ShowTimeline);
        changed |= ImGui.Checkbox("Event Notifications"u8, ref config.ShowNotifications);
        changed |= ImGui.Checkbox("Terrain-relative altitude"u8, ref config.TerrainRelativeAltitude);

        ImGui.SeparatorText("Readout slots"u8);
        changed |= DrawSlotEditor("Left"u8, "left", ref config.LeftSlots, config);
        changed |= DrawSlotEditor("Right"u8, "right", ref config.RightSlots, config);

        ImGui.SeparatorText("Presentation"u8);
        ImGui.SetNextItemWidth(160f);
        changed |= ImGui.SliderFloat("Scale"u8, ref config.Scale,
            OverlayConfig.MinScale, OverlayConfig.MaxScale, "%.2f"u8);
        ImGui.SetNextItemWidth(160f);
        changed |= ImGui.SliderFloat("Opacity"u8, ref config.Opacity,
            OverlayConfig.MinOpacity, OverlayConfig.MaxOpacity, "%.2f"u8);
        ImGui.SetNextItemWidth(160f);
        changed |= ImGui.SliderFloat("Smoothing"u8, ref config.SmoothingSeconds,
            OverlayConfig.MinSmoothing, OverlayConfig.MaxSmoothing, "%.2f s"u8);
        ImGui.SetNextItemWidth(160f);
        changed |= ImGui.SliderFloat("Timeline window"u8, ref config.TimelineWindowSeconds,
            OverlayConfig.MinTimelineWindow, OverlayConfig.MaxTimelineWindow, "%.0f s"u8);

        ImGui.SeparatorText("Signal loss"u8);
        ImGui.SetNextItemWidth(160f);
        changed |= ImGui.SliderFloat("Freeze at load"u8, ref config.FreezeAtToleranceFraction,
            OverlayConfig.MinFreezeTolerance, OverlayConfig.MaxFreezeTolerance, "%.2f"u8);
        ImGui.TextDisabled("Freezes once g-load or dynamic pressure reaches this much of"u8);
        ImGui.TextDisabled("what the vehicle can take, so the last values are from before"u8);
        ImGui.TextDisabled("it came apart. 1.00 waits for the breakup itself."u8);

        ImGui.SeparatorText("Mission name"u8);
        if (_missionName is { } buffer)
        {
            ImGui.SetNextItemWidth(200f);
            if (ImGui.InputText("##missionName"u8, buffer))
            {
                string typed = buffer.ToString();
                config.MissionName = string.IsNullOrWhiteSpace(typed) ? null : typed;
                changed = true;
            }

            ImGui.TextDisabled("Blank uses the vehicle name."u8);
        }

        ImGui.SeparatorText("Standalone windows"u8);
        if (inMenu)
        {
            if (ImGui.BeginMenu("Open windows"u8))
            {
                DrawWindowToggles();
                ImGui.EndMenu();
            }
        }
        else
        {
            DrawWindowToggles();
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

        ImGui.SeparatorText("Developer"u8);
        bool tuning = TuningUi.IsOpen;
        if (ImGui.Checkbox("Visual tuning window"u8, ref tuning))
        {
            TuningUi.SetOpen(tuning);
        }

        ImGui.SeparatorText("Config"u8);
        if (ImGui.SmallButton("Save now"u8))
        {
            ConfigStore.SaveNow();
        }

        ImGui.TextDisabled(ConfigStore.FilePath);

        if (changed)
        {
            ConfigStore.MarkDirty();
        }
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
