using System.IO;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSATelemetryOverlay.Config;
using KSATelemetryOverlay.Recording;
using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Rendering;

public static class SettingsUi
{
    private static OverlayConfig? _config;
    private static WindowHost? _windows;
    private static ImInputString? _missionName;
    private static bool _windowOpen;
    private enum KeySlot : byte { None, Toggle, Settings, Countdown, Record }
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

    public static void DrawWindow()
    {
        if (_config is null || !_windowOpen)
        {
            return;
        }

        ImGui.SetNextWindowSize(new float2(360f, 0f), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Telemetry Overlay"u8, ref _windowOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            DrawContent(_config);
        }

        ImGui.End();
    }

    public static void ToggleWindow() => _windowOpen = !_windowOpen;

    public static void OpenWindow() => _windowOpen = true;

    private static void DrawContent(OverlayConfig config)
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

        if (ImGui.CollapsingHeader("Elements"u8))
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
        }

        if (ImGui.CollapsingHeader("Readouts"u8))
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
        }

        if (ImGui.CollapsingHeader("Events"u8))
        {
            ImGui.TextDisabled("Announce as a callout"u8);
            changed |= DrawNotificationToggles(config);
            ImGui.Separator();

            ImGui.SetNextItemWidth(160f);
            changed |= ImGui.SliderFloat("Timeline window"u8, ref config.TimelineWindowSeconds,
                OverlayConfig.MinTimelineWindow, OverlayConfig.MaxTimelineWindow, "%.0f s"u8);
        }

        if (ImGui.CollapsingHeader("Appearance"u8))
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

            ImGui.SeparatorText("Colours"u8);
            changed |= DrawColorEditors();
        }

        if (ImGui.CollapsingHeader("Behaviour"u8))
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
        }

        if (ImGui.CollapsingHeader("Windows"u8))
        {
            DrawWindowToggles();
        }

        if (ImGui.CollapsingHeader("Flight Recorder"u8))
        {
            changed |= DrawFlightRecorderSettings(config);
        }

        if (TelemetryOverlayApi.HasObsModule && ImGui.CollapsingHeader("OBS / Webcast"u8))
        {
            changed |= TelemetryOverlayApi.DrawObsSettings(config);
        }

        if (ImGui.CollapsingHeader("Advanced"u8))
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
        }

        if (changed)
        {
            ConfigStore.MarkDirty();
        }
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

    private static bool DrawFlightRecorderSettings(OverlayConfig config)
    {
        bool changed = false;

        ImGui.TextColored(new float4(0.4f, 0.8f, 1f, 1f), "Flight Recorder"u8);
        ImGui.TextDisabled("Records the clean game scene with no game HUD, retroactively composites"u8);
        ImGui.TextDisabled("the telemetry overlay and then saves the replay to a .mp4 file."u8);

        // Turning the recorder off mid-recording would pull the capture hook out from under it
        if (FlightRecorder.Instance.State != RecordingState.Idle)
        {
            ImGui.BeginDisabled();
            ImGui.Checkbox("Enable Flight Recorder"u8, ref config.EnableFlightRecorder);
            ImGui.EndDisabled();
        }
        else
        {
            changed |= ImGui.Checkbox("Enable Flight Recorder"u8, ref config.EnableFlightRecorder);
        }

        if (config.EnableFlightRecorder)
        {
            ImGui.Indent();

            // Status and Manual Controls
            if (FlightRecorder.Instance.IsFinalizing || FlightRecorder.Instance.IsSaving)
            {
                ImGui.TextColored(new float4(0.3f, 0.7f, 1f, 1f), $"⚙ FINALIZING: {FlightRecorder.Instance.StatusMessage}");
                ImGui.TextDisabled("This finalizing step can take a few minutes to complete."u8);
                ImGui.TextDisabled("If the game closes first, the video is kept without the overlay."u8);
            }
            else if (FlightRecorder.Instance.IsRecording)
            {
                float pulse = 0.6f + 0.4f * MathF.Sin((float)FlightRecorder.Instance.ElapsedSeconds * 5f);
                ImGui.TextColored(new float4(1f, 0.2f, 0.2f, pulse), $"● REC: {FlightRecorder.Instance.StatusMessage}");
                ImGui.SameLine();
                if (ImGui.Button("Stop Recording##FlightRec"u8))
                {
                    FlightRecorder.Instance.StopRecording();
                }
            }
            else
            {
                string? ffmpegPath = FfmpegEncoder.FindFfmpegCached(config.FfmpegPath, TelemetryOverlayMod.ModDirectory);
                if (ffmpegPath != null)
                {
                    // The probe launches FFmpeg several times; never run it on the UI thread
                    if (FfmpegEncoder.TryGetCachedCodec(ffmpegPath, out string bestCodec))
                    {
                        ImGui.TextColored(new float4(0.3f, 0.9f, 0.5f, 1f), $"FFmpeg Ready: {FfmpegEncoder.CodecDisplayName(bestCodec)}");
                    }
                    else
                    {
                        FfmpegEncoder.WarmUpCodecDetection(ffmpegPath);
                        ImGui.TextColored(new float4(0.9f, 0.8f, 0.3f, 1f), "FFmpeg found, detecting hardware encoder..."u8);
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Start Recording Now##FlightRec"u8))
                    {
                        if (TelemetryOverlayApi.CurrentConfig is { } curCfg && GameCaptureHook.CurrentSnapshot is { } curSnap)
                        {
                            FlightRecorder.Instance.StartRecording(curCfg, curSnap, TelemetryOverlayMod.ModDirectory);
                        }
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Re-detect hardware##FlightRec"u8))
                    {
                        FfmpegEncoder.InvalidateCodecCache();
                        FfmpegEncoder.WarmUpCodecDetection(ffmpegPath);
                    }
                }
                else
                {
                    ImGui.TextColored(new float4(1f, 0.4f, 0.4f, 1f), "FFmpeg not detected on PATH or mod folder."u8);
                }
            }

            ImGui.Spacing();
            changed |= DrawKeyBinding("Record video hotkey"u8, ref config.RecordKey, KeySlot.Record);
            ImGui.TextDisabled("(Press in flight to start/stop)"u8);

            // Resolution selection
            ImGui.TextDisabled("Resolution:"u8);
            ImGui.SameLine();
            if (ImGui.RadioButton("720p##Rec"u8, config.RecordMaxHeight == 720))
            {
                config.RecordMaxHeight = 720;
                changed = true;
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("1080p (Default)##Rec"u8, config.RecordMaxHeight == 1080 || (config.RecordMaxHeight != 720 && config.RecordMaxHeight != 1440)))
            {
                config.RecordMaxHeight = 1080;
                changed = true;
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("1440p##Rec"u8, config.RecordMaxHeight == 1440))
            {
                config.RecordMaxHeight = 1440;
                changed = true;
            }

            // Framerate selection
            ImGui.TextDisabled("Framerate:"u8);
            ImGui.SameLine();
            if (ImGui.RadioButton("30 FPS (Default)##Rec"u8, config.RecordFps == 30 || config.RecordFps != 60))
            {
                config.RecordFps = 30;
                changed = true;
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("60 FPS##Rec"u8, config.RecordFps == 60))
            {
                config.RecordFps = 60;
                changed = true;
            }

            // Game audio recording toggle
            changed |= ImGui.Checkbox("Record game audio"u8, ref config.RecordAudio);
            if (!config.RecordAudio)
            {
                ImGui.TextDisabled("Disabled: Video will be exported without audio track."u8);
            }

            // Overlay inclusion toggle
            changed |= ImGui.Checkbox("Include telemetry overlay in video"u8, ref config.RecordOverlay);
            if (config.RecordOverlay)
            {
                ImGui.Indent();

                // Intro lead: its own setting, separate from the in-game countdown length
                ImGui.SetNextItemWidth(120f);
                float lead = config.ReplayIntroLeadSeconds;
                if (ImGui.SliderFloat("Replay countdown (seconds)"u8, ref lead, OverlayConfig.MinIntroLead, OverlayConfig.MaxIntroLead, "T-%.0f s"u8))
                {
                    config.ReplayIntroLeadSeconds = MathF.Round(lead);
                    changed = true;
                }
                ImGui.TextDisabled("How long before liftoff the overlay appears in the replay (0 = at liftoff)."u8);

                // Fine-tune only: frames are already paired with the telemetry captured with them
                ImGui.SetNextItemWidth(120f);
                float offset = config.ReplayOverlayOffset;
                if (ImGui.SliderFloat("Overlay timing offset"u8, ref offset, OverlayConfig.MinReplayOffset, OverlayConfig.MaxReplayOffset, "%+.2f s"u8))
                {
                    config.ReplayOverlayOffset = MathF.Round(offset * 100f) / 100f;
                    changed = true;
                }
                ImGui.TextDisabled("Positive values delay the overlay relative to the video. Leave at 0 unless it looks off."u8);

                ImGui.SetNextItemWidth(120f);
                if (ImGui.SliderInt("Finalize speed"u8, ref config.ReplayFinalizeSpeed, OverlayConfig.MinFinalizeSpeed, OverlayConfig.MaxFinalizeSpeed, "%dx"u8))
                {
                    changed = true;
                }
                ImGui.TextDisabled("Overlay frames rendered per game frame after the flight. Higher is faster but heavier."u8);

                ImGui.Unindent();
            }
            else
            {
                ImGui.TextDisabled("Disabled: Clean video is saved directly upon stopping (no overlay compositing)."u8);
            }

            // Auto-triggers
            changed |= ImGui.Checkbox("Auto-start recording on Liftoff / Engine ignition"u8, ref config.AutoRecordOnLiftoff);
            changed |= ImGui.Checkbox("Auto-stop recording after Landing / Mission End"u8, ref config.AutoStopOnMissionEnd);

            // Open Folder button
            if (ImGui.Button("Open Recordings Folder"u8))
            {
                FlightRecorder.OpenRecordingsFolder(config.RecordingsFolder);
            }
            ImGui.SameLine();
            ImGui.TextDisabled(FlightRecorder.GetDefaultRecordingsDirectory());

            if (!string.IsNullOrEmpty(FlightRecorder.Instance.LastSavedPath))
            {
                ImGui.TextColored(new float4(0.3f, 0.9f, 0.5f, 1f), $"Last Saved: {Path.GetFileName(FlightRecorder.Instance.LastSavedPath)} ({FlightRecorder.Instance.LastSavedSizeMb:F1} MB)");
            }

            ImGui.Unindent();
        }

        return changed;
    }
}
