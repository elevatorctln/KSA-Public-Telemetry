// KSATelemetryOverlay — OBS Browser Source Renderer
// Exact 1:1 Immediate-Mode Canvas 2D engine mirroring Gfx.cs, PanelHost.cs, and all panels.

(function () {
    'use strict';

    const canvas = document.getElementById('overlay-canvas');
    const ctx = canvas.getContext('2d');

    // Reference design resolution (1080p standard OBS canvas)
    const REF_WIDTH = 1920;
    const REF_HEIGHT = 1080;

    // Constants strictly mirrored from Tuning.cs & PanelHost.cs
    const BOX_HEIGHT = 177.0;
    const FADE_HEIGHT_FACTOR = 0.918;
    const DIAGONAL_SLOPE = 0.908;
    const BOX_CORNER_RADIUS = 50.0;
    const PANEL_MARGIN_X = 40.0;
    const PANEL_MARGIN_Y = 18.0;
    const PANEL_GAP = 8.0;
    const BOX_PADDING = 14.0;
    const SHELF_FLOOR_FRACTION = 0.269;
    const CAPSULE_SIZE = 150.0;
    const CAPSULE_GAP = 3.0;

    // Palette strictly mirrored from OverlayStyle.cs
    const COLOR_SHELF_BOX = 'rgba(0, 0, 0, 0.40)';
    const COLOR_GAUGE_PLATE = 'rgba(24, 20, 16, 0.40)';
    const COLOR_GAUGE_RIM = 'rgba(255, 255, 255, 0.45)';
    const COLOR_HAIRLINE = 'rgba(255, 255, 255, 0.35)';
    const COLOR_ARC_TRACK = 'rgba(255, 255, 255, 0.30)';
    const COLOR_ARC_FILL = 'rgba(255, 255, 255, 1.0)';
    const COLOR_TEXT_PRIMARY = 'rgba(255, 255, 255, 1.0)';
    const COLOR_TEXT_MUTED = 'rgba(204, 196, 185, 1.0)';
    const COLOR_TEXT_DIM = 'rgba(143, 134, 122, 1.0)';
    const COLOR_TIMELINE_PAST = 'rgba(255, 255, 255, 0.90)';
    const COLOR_TIMELINE_FUTURE = 'rgba(255, 255, 255, 0.35)';
    const COLOR_ENGINE_NOMINAL = 'rgba(255, 255, 255, 1.0)';
    const COLOR_ENGINE_INACTIVE = 'rgba(70, 74, 79, 0.90)';

    // Telemetry State
    let telemetry = {
        hasVehicle: false,
        hasLiftoff: false,
        countingDown: false,
        isFrozen: false,
        signal: 'Live',
        missionTime: 0,
        vehicleName: '',
        missionName: '',
        surfaceSpeed: 0,
        orbitalSpeed: 0,
        verticalSpeed: 0,
        speedRef: 'Surface',
        altitude: 0,
        radarAltitude: 0,
        terrainRelative: false,
        apoapsis: 0,
        periapsis: 0,
        gForce: 1.0,
        dynamicPressure: 0,
        maxDynamicPressure: 0,
        downrange: 0,
        thrust: 0,
        thrustToWeight: 0,
        propellantFraction: 1.0,
        propellantMass: 0,
        propellantCapacity: 0,
        burningEngines: 0,
        totalEngines: 0,
        engines: [],
        events: [],
        leftSlots: ['Speed', 'Altitude'],
        rightSlots: ['GForce'],
        showTelemetryBar: true,
        showEngineDiagram: true,
        showTimeline: true,
        showMissionClock: true,
        showPropellants: true,
        scale: 0.83,
        opacity: 1.0,
        smoothingSeconds: 0.12,
        timelineWindowSeconds: 240.0,
        speedArcFullScale: 7800.0,
        altitudeArcFullScaleKm: 100.0,
        gForceArcFullScale: 6.0,
        engineDiagramRotation: 0.0
    };

    // Smoothed values for frame rate-independent 60 FPS animation
    let smoothed = {
        speed: 0,
        altitude: 0,
        gForce: 1.0,
        verticalSpeed: 0,
        thrust: 0,
        thrustToWeight: 0,
        dynamicPressure: 0,
        apoapsis: 0,
        periapsis: 0,
        downrange: 0,
        propellant: 1.0
    };

    // Engine dot animation states
    let engineStates = []; // [{ shownBurning: bool, fade: float }]

    // Notification banner system
    let bannerQueue = [];
    let currentBanner = null; // { title: '', subtitle: '', startTime: 0, endTime: 0 }
    let knownEventTimes = new Set();

    // Signal notice fade
    let signalNoticeAlpha = 0;

    let initialised = false;
    let lastTime = performance.now();

    // High DPI Canvas Resizing
    function resizeCanvas() {
        const dpr = window.devicePixelRatio || 1;
        const displayWidth = window.innerWidth || REF_WIDTH;
        const displayHeight = window.innerHeight || REF_HEIGHT;

        if (canvas.width !== displayWidth * dpr || canvas.height !== displayHeight * dpr) {
            canvas.width = displayWidth * dpr;
            canvas.height = displayHeight * dpr;
        }
    }
    window.addEventListener('resize', resizeCanvas);
    resizeCanvas();

    // Arc helper: startAngle and endAngle where 0 is 12 o'clock, clockwise
    function drawArc(cx, cy, radius, startRad, endRad, color, lineWidth) {
        if (radius <= 0 || Math.abs(endRad - startRad) < 1e-4) return;
        ctx.save();
        ctx.strokeStyle = color;
        ctx.lineWidth = lineWidth;
        ctx.beginPath();
        const a0 = startRad - Math.PI * 0.5;
        const a1 = endRad - Math.PI * 0.5;
        ctx.arc(cx, cy, radius, a0, a1);
        ctx.stroke();
        ctx.restore();
    }

    // Bottom Vignette Fade (Gfx.BottomFade)
    function drawBottomFade(width, height, scale, opacity) {
        const fadeHeight = BOX_HEIGHT * FADE_HEIGHT_FACTOR * scale;
        const top = height - fadeHeight;

        const grad = ctx.createLinearGradient(0, top, 0, height);
        grad.addColorStop(0.0, 'rgba(0, 0, 0, 0.0)');
        grad.addColorStop(0.3, `rgba(0, 0, 0, ${0.15 * opacity})`);
        grad.addColorStop(0.7, `rgba(0, 0, 0, ${0.45 * opacity})`);
        grad.addColorStop(1.0, `rgba(0, 0, 0, ${0.69 * opacity})`);

        ctx.fillStyle = grad;
        ctx.fillRect(0, top, width, fadeHeight);
    }

    // Beveled Filleted Shelf Box (Gfx.ShelfBox)
    function drawShelfBox(width, height, flatWidth, onLeft, scale, opacity) {
        if (flatWidth <= 0) return;

        const boxH = BOX_HEIGHT * scale;
        const top = height - boxH;
        const bottom = height;
        const radius = Math.max(BOX_CORNER_RADIUS * scale, 0.01);
        const slope = DIAGONAL_SLOPE;

        function drop(past) {
            if (past <= 0) return 0;
            return past < radius
                ? slope * past * past / (2 * radius)
                : slope * (past - radius * 0.5);
        }

        const filletDrop = drop(radius);
        const runEnd = boxH <= filletDrop
            ? Math.sqrt(2 * radius * boxH / slope)
            : radius * 0.5 + boxH / slope;
        const filletEnd = Math.min(radius, runEnd);

        ctx.save();
        ctx.beginPath();

        const samples = 16;
        if (onLeft) {
            ctx.moveTo(0, bottom);
            ctx.lineTo(0, top);
            for (let i = 0; i <= samples; i++) {
                const past = filletEnd * i / samples;
                ctx.lineTo(flatWidth + past, Math.min(top + drop(past), bottom));
            }
            if (runEnd > filletEnd) {
                ctx.lineTo(flatWidth + runEnd, bottom);
            }
            ctx.lineTo(0, bottom);
        } else {
            ctx.moveTo(width, bottom);
            ctx.lineTo(width, top);
            for (let i = 0; i <= samples; i++) {
                const past = filletEnd * i / samples;
                ctx.lineTo(width - (flatWidth + past), Math.min(top + drop(past), bottom));
            }
            if (runEnd > filletEnd) {
                ctx.lineTo(width - (flatWidth + runEnd), bottom);
            }
            ctx.lineTo(width, bottom);
        }

        ctx.closePath();

        // Floor fade gradient (Gfx.ShelfBox floor fraction)
        const grad = ctx.createLinearGradient(0, top, 0, bottom);
        grad.addColorStop(0.0, `rgba(0, 0, 0, ${0.40 * opacity})`);
        grad.addColorStop(1.0, `rgba(0, 0, 0, ${0.40 * SHELF_FLOOR_FRACTION * opacity})`);
        ctx.fillStyle = grad;
        ctx.fill();
        ctx.restore();
    }

    // Number & Unit Formatter matching Format.cs & ReadoutPanel.cs
    function formatReadout(kind) {
        switch (kind) {
            case 'Speed': {
                const kmh = smoothed.speed * 3.6;
                const fullScale = (telemetry.speedArcFullScale || 7800.0) * 3.6;
                return {
                    label: 'SPEED',
                    value: Math.round(kmh).toLocaleString('en-US'),
                    unit: 'KM/H',
                    level: Math.min(kmh / Math.max(fullScale, 1.0), 1.0),
                    dim: false
                };
            }
            case 'Altitude': {
                const km = smoothed.altitude / 1000.0;
                const fullScaleKm = telemetry.altitudeArcFullScaleKm || 100.0;
                return {
                    label: 'ALTITUDE',
                    value: km.toLocaleString('en-US', { minimumFractionDigits: 1, maximumFractionDigits: 1 }),
                    unit: 'KM',
                    level: Math.min(km / Math.max(fullScaleKm, 1.0), 1.0),
                    dim: false
                };
            }
            case 'GForce': {
                const g = smoothed.gForce;
                const fullScaleG = telemetry.gForceArcFullScale || 6.0;
                return {
                    label: 'G-FORCE',
                    value: g.toLocaleString('en-US', { minimumFractionDigits: 1, maximumFractionDigits: 1 }),
                    unit: 'G',
                    level: Math.min(g / Math.max(fullScaleG, 1.0), 1.0),
                    dim: false
                };
            }
            case 'VerticalSpeed': {
                const vs = Math.round(smoothed.verticalSpeed);
                return {
                    label: 'V/S',
                    value: vs.toLocaleString('en-US'),
                    unit: 'M/S',
                    level: Math.min(Math.abs(vs) / 500.0, 1.0),
                    dim: false
                };
            }
            case 'Thrust': {
                const t = smoothed.thrust;
                if (t >= 1e6) {
                    return { label: 'THRUST', value: (t / 1e6).toFixed(2), unit: 'MN', level: 0, dim: false };
                } else if (t >= 1e3) {
                    return { label: 'THRUST', value: (t / 1e3).toFixed(1), unit: 'KN', level: 0, dim: false };
                }
                return { label: 'THRUST', value: Math.round(t).toLocaleString('en-US'), unit: 'N', level: 0, dim: false };
            }
            case 'ThrustToWeight': {
                return {
                    label: 'T/W',
                    value: smoothed.thrustToWeight.toFixed(2),
                    unit: '',
                    level: 0,
                    dim: false
                };
            }
            case 'DynamicPressure': {
                const q = smoothed.dynamicPressure;
                const dim = telemetry.maxDynamicPressure > 0 && q < telemetry.maxDynamicPressure * 0.98;
                if (q >= 1e6) {
                    return { label: 'DYN PRESS', value: (q / 1e6).toFixed(2), unit: 'MPA', level: 0, dim };
                } else if (q >= 1e3) {
                    return { label: 'DYN PRESS', value: (q / 1e3).toFixed(1), unit: 'KPA', level: 0, dim };
                }
                return { label: 'DYN PRESS', value: Math.round(q).toLocaleString('en-US'), unit: 'PA', level: 0, dim };
            }
            case 'Apoapsis': {
                const ap = smoothed.apoapsis / 1000.0;
                return {
                    label: 'APOAPSIS',
                    value: ap >= 1000.0 ? Math.round(ap).toLocaleString('en-US') : ap.toFixed(1),
                    unit: 'KM',
                    level: 0,
                    dim: false
                };
            }
            case 'Periapsis': {
                const pe = smoothed.periapsis / 1000.0;
                return {
                    label: 'PERIAPSIS',
                    value: pe >= 1000.0 ? Math.round(pe).toLocaleString('en-US') : pe.toFixed(1),
                    unit: 'KM',
                    level: 0,
                    dim: false
                };
            }
            case 'Downrange': {
                const dr = smoothed.downrange / 1000.0;
                return {
                    label: 'DOWNRANGE',
                    value: isNaN(dr) ? '--' : dr.toFixed(1),
                    unit: 'KM',
                    level: 0,
                    dim: false
                };
            }
            default:
                return { label: kind.toUpperCase(), value: '--', unit: '', level: 0, dim: false };
        }
    }

    // Readout Capsule (ReadoutPanel.cs & Gfx.cs)
    function drawCapsule(cx, cy, readout, scale, opacity) {
        const radius = CAPSULE_SIZE * scale * 0.42;

        // Circular Gauge Plate (Gfx.GaugePlate)
        ctx.save();
        ctx.fillStyle = COLOR_GAUGE_PLATE;
        ctx.beginPath();
        ctx.arc(cx, cy, radius, 0, Math.PI * 2);
        ctx.fill();
        ctx.restore();

        // Swept Arc Gauge (Gfx.Arc)
        const halfSweep = Math.PI * 0.75;
        const sweepRadius = radius + 3.0 * scale;
        const arcThickness = Math.max(1.5, 2.5 * scale);

        drawArc(cx, cy, sweepRadius, -halfSweep, halfSweep, COLOR_ARC_TRACK, arcThickness);
        if (readout.level > 0) {
            const currentEnd = -halfSweep + (halfSweep * 2.0 * Math.min(readout.level, 1.0));
            drawArc(cx, cy, sweepRadius, -halfSweep, currentEnd, COLOR_ARC_FILL, arcThickness);
        }

        // Hairline Brackets (Gfx.ReadoutBrackets)
        const bracketHalfSweep = Math.PI * 0.28;
        const bracketThickness = Math.max(1.0, 1.5 * scale);
        // Top bracket
        drawArc(cx, cy, radius, Math.PI - bracketHalfSweep, Math.PI + bracketHalfSweep, COLOR_HAIRLINE, bracketThickness);
        // Bottom bracket
        drawArc(cx, cy, radius, -bracketHalfSweep, bracketHalfSweep, COLOR_HAIRLINE, bracketThickness);

        // Text rendering (Gfx.ReadoutText)
        ctx.save();
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';

        const labelSize = Math.round(11.0 * scale);
        const valueSize = Math.round(26.0 * scale);
        const unitSize = Math.round(labelSize * 0.85);

        const totalH = labelSize + valueSize + unitSize + 8 * scale;
        const topY = cy - totalH * 0.5;

        // Label
        ctx.font = `700 ${labelSize}px Montserrat, sans-serif`;
        ctx.fillStyle = COLOR_TEXT_MUTED;
        ctx.fillText(readout.label, cx, topY + labelSize * 0.5);

        // Value
        ctx.font = `700 ${valueSize}px Montserrat, sans-serif`;
        ctx.fillStyle = readout.dim ? COLOR_TEXT_DIM : COLOR_TEXT_PRIMARY;
        ctx.fillText(readout.value, cx, topY + labelSize + valueSize * 0.5 + 2 * scale);

        // Unit
        if (readout.unit) {
            ctx.font = `700 ${unitSize}px Montserrat, sans-serif`;
            ctx.fillStyle = COLOR_TEXT_DIM;
            ctx.fillText(readout.unit, cx, topY + labelSize + valueSize + unitSize * 0.5 + 4 * scale);
        }

        ctx.restore();
    }

    // Engine Cluster Panel (EngineClusterPanel.cs)
    function drawEngineCluster(cx, cy, propellantFraction, engines, dt, scale, opacity) {
        const outerRadius = CAPSULE_SIZE * scale * 0.5;
        const arcRadius = outerRadius - 3.0 * scale;
        const arcThickness = Math.max(2.0, 3.0 * scale);
        const diagramRadius = arcRadius - arcThickness - 6.0 * scale;
        const plateRadius = diagramRadius + 4.0 * scale;

        // Gauge Plate & Rim
        ctx.save();
        ctx.fillStyle = COLOR_GAUGE_PLATE;
        ctx.beginPath();
        ctx.arc(cx, cy, plateRadius, 0, Math.PI * 2);
        ctx.fill();

        ctx.strokeStyle = COLOR_GAUGE_RIM;
        ctx.lineWidth = Math.max(1.0, scale);
        ctx.stroke();
        ctx.restore();

        // Propellant Arc (wrapping 3/4 around)
        if (telemetry.showPropellants) {
            const halfSweep = Math.PI * 0.75;
            drawArc(cx, cy, arcRadius, -halfSweep, halfSweep, COLOR_ARC_TRACK, arcThickness);
            if (propellantFraction > 0) {
                const fillEnd = -halfSweep + (halfSweep * 2.0 * Math.min(propellantFraction, 1.0));
                drawArc(cx, cy, arcRadius, -halfSweep, fillEnd, COLOR_ARC_FILL, arcThickness);
            }
        }

        // Engine dots
        ctx.save();
        const plotRadius = diagramRadius * 0.893;
        const dotRadius = Math.max(diagramRadius * 0.17, 3.7 * scale);

        const radRot = (telemetry.engineDiagramRotation || 0) * (Math.PI / 180.0);
        const cosR = Math.cos(radRot);
        const sinR = Math.sin(radRot);

        if (engines && engines.length > 0) {
            // Ensure state array length matches
            while (engineStates.length < engines.length) {
                engineStates.push({ shownBurning: false, fade: 1.0 });
            }

            for (let i = 0; i < engines.length; i++) {
                const eng = engines[i];
                const isBurning = eng.Burning || eng.Status === 'Nominal' || eng.Status === 'Throttled';

                // Smooth engine fade
                const state = engineStates[i];
                if (state.shownBurning !== isBurning) {
                    state.fade = Math.max(0, state.fade - dt / 0.18);
                    if (state.fade <= 0) {
                        state.shownBurning = isBurning;
                        state.fade = 1.0;
                    }
                }

                // Rotated diagram coords (+Y is upwards in diagram space, negate for screen)
                const ex = eng.X || 0;
                const ey = eng.Y || 0;
                const rx = ex * cosR - ey * sinR;
                const ry = ex * sinR + ey * cosR;

                const px = cx + rx * (plotRadius * 0.65);
                const py = cy - ry * (plotRadius * 0.65);

                const engineDotR = eng.Radius ? dotRadius * eng.Radius : dotRadius;
                ctx.fillStyle = state.shownBurning ? COLOR_ENGINE_NOMINAL : COLOR_ENGINE_INACTIVE;

                ctx.beginPath();
                ctx.arc(px, py, engineDotR, 0, Math.PI * 2);
                ctx.fill();
            }
        } else {
            // Default 9-engine Octaweb pattern if no engines supplied yet
            const octaweb = [{ x: 0, y: 0 }];
            for (let i = 0; i < 8; i++) {
                const ang = i * (Math.PI * 2.0 / 8.0);
                octaweb.push({ x: Math.cos(ang), y: Math.sin(ang) });
            }

            for (let i = 0; i < octaweb.length; i++) {
                const px = cx + octaweb[i].x * (plotRadius * 0.65);
                const py = cy - octaweb[i].y * (plotRadius * 0.65);

                ctx.fillStyle = COLOR_ENGINE_NOMINAL;
                ctx.beginPath();
                ctx.arc(px, py, dotRadius, 0, Math.PI * 2);
                ctx.fill();
            }
        }
        ctx.restore();
    }

    // Center Mission Timeline (TimelinePanel.cs)
    function drawTimeline(cx, y, missionTime, events, scale, opacity) {
        const width = 1000.0 * scale;
        const left = cx - width * 0.5;
        const right = cx + width * 0.5;
        const thickness = Math.max(1.0, 1.4 * scale);
        const halfWindow = (telemetry.timelineWindowSeconds || 240.0) * 0.5;

        ctx.save();
        ctx.lineWidth = thickness;

        // Faded track line with gradient fade at ends (Gfx.FadedTrack)
        const grad = ctx.createLinearGradient(left, y, right, y);
        grad.addColorStop(0.0, 'rgba(255, 255, 255, 0.0)');
        grad.addColorStop(0.16, `rgba(255, 255, 255, ${0.90 * opacity})`);
        grad.addColorStop(0.5, `rgba(255, 255, 255, ${0.90 * opacity})`);
        grad.addColorStop(0.84, `rgba(255, 255, 255, ${0.35 * opacity})`);
        grad.addColorStop(1.0, 'rgba(255, 255, 255, 0.0)');

        ctx.strokeStyle = grad;
        ctx.beginPath();
        ctx.moveTo(left, y);
        ctx.lineTo(right, y);
        ctx.stroke();

        // Center Indicator Dot at current elapsed time
        ctx.fillStyle = COLOR_TIMELINE_PAST;
        ctx.beginPath();
        ctx.arc(cx, y, Math.max(3.0, 3.5 * scale), 0, Math.PI * 2);
        ctx.fill();

        // Plot visible events within [missionTime - halfWindow, missionTime + halfWindow]
        let closestEvent = null;
        let minDiff = Infinity;

        if (events && events.length > 0) {
            for (let i = 0; i < events.length; i++) {
                const ev = events[i];
                const dt = ev.Time - missionTime;
                if (Math.abs(dt) <= halfWindow) {
                    const ex = cx + (dt / halfWindow) * (width * 0.5);

                    // Fade near track ends
                    const distFromEdge = Math.min(ex - left, right - ex);
                    const fadeMargin = width * 0.16;
                    const eventAlpha = Math.max(0, Math.min(distFromEdge / fadeMargin, 1.0));

                    if (eventAlpha > 0.05) {
                        const isPast = dt <= 0;
                        ctx.fillStyle = isPast ? COLOR_TIMELINE_PAST : COLOR_TIMELINE_FUTURE;

                        // Event tick / dot
                        ctx.beginPath();
                        ctx.arc(ex, y, Math.max(2.0, 2.5 * scale), 0, Math.PI * 2);
                        ctx.fill();

                        // Track closest event for callout label
                        if (Math.abs(dt) < minDiff) {
                            minDiff = Math.abs(dt);
                            closestEvent = ev;
                        }
                    }
                }
            }
        }

        // Event callout text directly above the timeline indicator
        const label = closestEvent && closestEvent.Label ? closestEvent.Label.toUpperCase() : 'ENGINE IGNITION';
        ctx.font = `700 ${Math.round(11.0 * scale)}px Montserrat, sans-serif`;
        ctx.fillStyle = COLOR_TEXT_PRIMARY;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'bottom';
        ctx.fillText(label, cx, y - 8.0 * scale);

        ctx.restore();
    }

    // Center Mission Clock (MissionClockPanel.cs)
    function drawMissionClock(cx, y, missionTime, vehicleName, countingDown, scale, opacity) {
        ctx.save();
        ctx.textAlign = 'center';

        const absSeconds = Math.abs(missionTime);
        const hours = Math.floor(absSeconds / 3600);
        const minutes = Math.floor((absSeconds % 3600) / 60);
        const seconds = Math.floor(absSeconds % 60);

        const pad = (n) => n.toString().padStart(2, '0');
        const timeString = `${pad(hours)}:${pad(minutes)}:${pad(seconds)}`;
        const prefix = missionTime >= 0 || !countingDown ? 'T+ ' : 'T- ';

        const clockSize = Math.round(30.0 * scale);
        const prefixSize = Math.round(18.0 * scale);

        ctx.font = `700 ${clockSize}px Montserrat, sans-serif`;
        const timeMetrics = ctx.measureText(timeString);

        ctx.font = `700 ${prefixSize}px Montserrat, sans-serif`;
        const prefixMetrics = ctx.measureText(prefix);

        const totalWidth = prefixMetrics.width + 4.0 * scale + timeMetrics.width;
        const startX = cx - totalWidth * 0.5;

        // Dim clock text if telemetry is frozen (breakup freeze / LOS)
        const timeColor = telemetry.isFrozen ? COLOR_TEXT_MUTED : COLOR_TEXT_PRIMARY;

        // Prefix
        ctx.textAlign = 'left';
        ctx.fillStyle = COLOR_TEXT_MUTED;
        ctx.fillText(prefix, startX, y);

        // Time digits
        ctx.font = `700 ${clockSize}px Montserrat, sans-serif`;
        ctx.fillStyle = timeColor;
        ctx.fillText(timeString, startX + prefixMetrics.width + 4.0 * scale, y);

        // Vehicle / Mission name below clock
        const name = (vehicleName || '9elaunch2').toUpperCase();
        const nameSize = Math.round(11.0 * scale);
        ctx.font = `700 ${nameSize}px Montserrat, sans-serif`;
        ctx.textAlign = 'center';
        ctx.fillStyle = COLOR_TEXT_MUTED;
        ctx.fillText(name, cx, y + 16.0 * scale);

        ctx.restore();
    }

    // Top-left Notification Banner Queue (NotificationPanel.cs)
    function updateBanners(now) {
        if (!currentBanner && bannerQueue.length > 0) {
            currentBanner = bannerQueue.shift();
            currentBanner.startTime = now;
            currentBanner.endTime = now + 5000; // 5 second banner display
        }

        if (currentBanner && now > currentBanner.endTime) {
            currentBanner = null;
        }
    }

    function drawNotificationBanner(scale, opacity) {
        if (!currentBanner) return;

        const now = performance.now();
        const elapsed = now - currentBanner.startTime;
        const remaining = currentBanner.endTime - now;

        // Smooth fade in (0.3s) and fade out (0.5s)
        let alpha = 1.0;
        if (elapsed < 300) {
            alpha = elapsed / 300.0;
        } else if (remaining < 500) {
            alpha = Math.max(0, remaining / 500.0);
        }

        alpha *= opacity;
        if (alpha <= 0.01) return;

        ctx.save();
        const x = PANEL_MARGIN_X * scale;
        const y = 30 * scale;
        const h = 42 * scale;
        const padX = 14 * scale;

        ctx.font = `700 ${Math.round(12 * scale)}px Montserrat, sans-serif`;
        const titleMetrics = ctx.measureText(currentBanner.title);

        const subSize = Math.round(10 * scale);
        ctx.font = `400 ${subSize}px Montserrat, sans-serif`;
        const subMetrics = ctx.measureText(currentBanner.subtitle || '');

        const maxTextW = Math.max(titleMetrics.width, subMetrics.width);
        const w = maxTextW + padX * 2 + 10 * scale;

        // Dark translucent banner box
        ctx.fillStyle = `rgba(10, 14, 18, ${0.88 * alpha})`;
        ctx.fillRect(x, y, w, h);

        // White accent notch on left border
        ctx.fillStyle = `rgba(255, 255, 255, ${alpha})`;
        ctx.fillRect(x, y, 4 * scale, h);

        // Title text
        ctx.font = `700 ${Math.round(12 * scale)}px Montserrat, sans-serif`;
        ctx.fillStyle = `rgba(255, 255, 255, ${alpha})`;
        ctx.textAlign = 'left';
        ctx.textBaseline = 'top';
        ctx.fillText(currentBanner.title, x + padX, y + 8 * scale);

        // Subtitle text (if present)
        if (currentBanner.subtitle) {
            ctx.font = `400 ${subSize}px Montserrat, sans-serif`;
            ctx.fillStyle = `rgba(185, 196, 204, ${alpha})`;
            ctx.fillText(currentBanner.subtitle, x + padX, y + 24 * scale);
        }

        ctx.restore();
    }

    // Signal Notice Banner (LOS / Telemetry Frozen)
    function drawSignalNotice(width, height, dt, scale, opacity) {
        const frozenOrLost = telemetry.isFrozen || telemetry.signal !== 'Live';
        const fadeSpeed = dt / 0.25; // 0.25s fade matching SignalNoticeFadeSeconds
        signalNoticeAlpha = Math.max(0, Math.min(1.0, signalNoticeAlpha + (frozenOrLost ? fadeSpeed : -fadeSpeed)));

        if (signalNoticeAlpha <= 0.01) return;

        ctx.save();
        const text = 'Please wait for acquisition of signal';
        const fontSize = Math.round(13.0 * scale);
        ctx.font = `700 ${fontSize}px Montserrat, sans-serif`;
        const metrics = ctx.measureText(text);

        const padX = 16.0 * scale;
        const padY = 8.0 * scale;
        const bannerW = metrics.width + padX * 2 + 16.0 * scale;
        const bannerH = fontSize + padY * 2;

        const bx = (width - bannerW) * 0.5;
        const by = height - (BOX_HEIGHT + 35.0) * scale;
        const alpha = signalNoticeAlpha * opacity;

        // Dark banner backdrop
        ctx.fillStyle = `rgba(16, 20, 26, ${0.88 * alpha})`;
        ctx.fillRect(bx, by, bannerW, bannerH);

        // Pulsing status dot
        const pulse = 0.5 + 0.5 * Math.sin(performance.now() * 0.005);
        ctx.fillStyle = `rgba(255, 100, 100, ${alpha * pulse})`;
        ctx.beginPath();
        ctx.arc(bx + 12.0 * scale, by + bannerH * 0.5, 3.5 * scale, 0, Math.PI * 2);
        ctx.fill();

        // Text
        ctx.fillStyle = `rgba(204, 196, 185, ${alpha})`;
        ctx.textAlign = 'left';
        ctx.textBaseline = 'middle';
        ctx.fillText(text, bx + 22.0 * scale, by + bannerH * 0.5);
        ctx.restore();
    }

    // Main 60 FPS Render Loop
    function render() {
        const now = performance.now();
        const dt = Math.min((now - lastTime) / 1000.0, 0.1);
        lastTime = now;

        const dpr = window.devicePixelRatio || 1;
        const width = canvas.width / dpr;
        const height = canvas.height / dpr;

        ctx.save();
        ctx.scale(dpr, dpr);
        ctx.clearRect(0, 0, width, height);

        // If no vehicle active, overlay stays clean
        if (!telemetry.hasVehicle) {
            ctx.restore();
            requestAnimationFrame(render);
            return;
        }

        // Overall scale based on canvas height vs reference 1080p
        const scale = (height / REF_HEIGHT) * (telemetry.scale || 1.0);
        const opacity = Math.max(0.1, Math.min(telemetry.opacity != null ? telemetry.opacity : 1.0, 1.0));

        // Exponential smoothing matching C# UpdateSmoothing
        const tau = Math.max(telemetry.smoothingSeconds || 0.12, 1e-4);
        const alpha = initialised ? 1.0 - Math.exp(-dt / tau) : 1.0;

        // Auto speed reference selection
        const speedTarget = telemetry.speedRef === 'Orbital'
            || (telemetry.speedRef === 'Auto' && telemetry.altitude > 70000)
            ? telemetry.orbitalSpeed
            : telemetry.surfaceSpeed;

        smoothed.speed += (speedTarget - smoothed.speed) * alpha;
        smoothed.altitude += ((telemetry.terrainRelative ? telemetry.radarAltitude : telemetry.altitude) - smoothed.altitude) * alpha;
        smoothed.gForce += (telemetry.gForce - smoothed.gForce) * alpha;
        smoothed.verticalSpeed += (telemetry.verticalSpeed - smoothed.verticalSpeed) * alpha;
        smoothed.thrust += (telemetry.thrust - smoothed.thrust) * alpha;
        smoothed.thrustToWeight += (telemetry.thrustToWeight - smoothed.thrustToWeight) * alpha;
        smoothed.dynamicPressure += (telemetry.dynamicPressure - smoothed.dynamicPressure) * alpha;
        smoothed.apoapsis += (telemetry.apoapsis - smoothed.apoapsis) * alpha;
        smoothed.periapsis += (telemetry.periapsis - smoothed.periapsis) * alpha;
        smoothed.downrange += (telemetry.downrange - smoothed.downrange) * alpha;
        smoothed.propellant += (telemetry.propellantFraction - smoothed.propellant) * alpha;
        initialised = true;

        updateBanners(now);

        // 1. Soft Bottom Vignette Fade
        drawBottomFade(width, height, scale, opacity);

        // 2. Left Shelf (Configurable Left Slots: Speed, Altitude, etc.)
        const leftSlots = telemetry.leftSlots && telemetry.leftSlots.length > 0
            ? telemetry.leftSlots
            : ['Speed', 'Altitude'];

        const leftCount = leftSlots.length;
        const groupWLeft = (leftCount * CAPSULE_SIZE + (leftCount - 1) * CAPSULE_GAP) * scale;
        const flatWLeft = (PANEL_MARGIN_X + BOX_PADDING) * scale + groupWLeft;
        drawShelfBox(width, height, flatWLeft, true, scale, opacity);

        const capsuleY = height - (PANEL_MARGIN_Y + CAPSULE_SIZE * 0.5) * scale;
        for (let i = 0; i < leftCount; i++) {
            const cx = (PANEL_MARGIN_X + CAPSULE_SIZE * 0.5) * scale + i * (CAPSULE_SIZE + CAPSULE_GAP) * scale;
            const readout = formatReadout(leftSlots[i]);
            drawCapsule(cx, capsuleY, readout, scale, opacity);
        }

        // 3. Right Shelf (Configurable Right Slots + Engine Cluster)
        const rightSlots = telemetry.rightSlots && telemetry.rightSlots.length > 0
            ? telemetry.rightSlots
            : ['GForce'];

        const rightCount = rightSlots.length;
        const groupWRight = ((rightCount + 1) * CAPSULE_SIZE + rightCount * PANEL_GAP) * scale;
        const flatWRight = (PANEL_MARGIN_X + BOX_PADDING) * scale + groupWRight;
        drawShelfBox(width, height, flatWRight, false, scale, opacity);

        // Engine pod sits on the far right
        const podX = width - (PANEL_MARGIN_X + CAPSULE_SIZE * 0.5) * scale;
        drawEngineCluster(podX, capsuleY, smoothed.propellant, telemetry.engines, dt, scale, opacity);

        // Secondary readouts sit to the left of the engine cluster
        for (let i = 0; i < rightCount; i++) {
            const cx = podX - (i + 1) * (CAPSULE_SIZE + PANEL_GAP) * scale;
            const readout = formatReadout(rightSlots[i]);
            drawCapsule(cx, capsuleY, readout, scale, opacity);
        }

        // 4. Center Timeline & Mission Clock
        if (telemetry.showTimeline) {
            const timelineY = height - 72.0 * scale;
            drawTimeline(width * 0.5, timelineY, telemetry.missionTime, telemetry.events, scale, opacity);
        }

        if (telemetry.showMissionClock) {
            const clockY = height - 44.0 * scale;
            drawMissionClock(
                width * 0.5, clockY,
                telemetry.missionTime,
                telemetry.missionName || telemetry.vehicleName,
                telemetry.countingDown,
                scale,
                opacity
            );
        }

        // 5. Notifications & Status Overlays
        drawNotificationBanner(scale, opacity);
        drawSignalNotice(width, height, dt, scale, opacity);

        ctx.restore();
        requestAnimationFrame(render);
    }

    // WebSocket Telemetry Connection
    function connect() {
        const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
        const wsUrl = `${protocol}//${location.host}/ws`;
        const ws = new WebSocket(wsUrl);

        ws.onopen = function () {
            console.log('[OBS Overlay] Connected to KSA telemetry feed');
        };

        ws.onmessage = function (event) {
            try {
                const packet = JSON.parse(event.data);

                // Check for newly announced events to queue into notification banners
                if (packet.Events && packet.Events.length > 0) {
                    for (let i = 0; i < packet.Events.length; i++) {
                        const ev = packet.Events[i];
                        if (!ev.IsPrediction && !knownEventTimes.has(ev.Time)) {
                            knownEventTimes.add(ev.Time);
                            bannerQueue.push({
                                title: ev.Label || ev.Kind || 'EVENT',
                                subtitle: ev.Explainer || ''
                            });
                        }
                    }
                }

                // Update full telemetry state
                telemetry = {
                    hasVehicle: packet.HasVehicle,
                    hasLiftoff: packet.HasLiftoff,
                    countingDown: packet.CountingDown,
                    isFrozen: packet.IsFrozen,
                    signal: packet.Signal || 'Live',
                    missionTime: packet.MissionTime || 0,
                    vehicleName: packet.VehicleName || '',
                    missionName: packet.MissionName || '',
                    surfaceSpeed: packet.SurfaceSpeed || 0,
                    orbitalSpeed: packet.OrbitalSpeed || 0,
                    verticalSpeed: packet.VerticalSpeed || 0,
                    speedRef: packet.SpeedRef || 'Surface',
                    altitude: packet.Altitude || 0,
                    radarAltitude: packet.RadarAltitude || 0,
                    terrainRelative: packet.TerrainRelative,
                    apoapsis: packet.Apoapsis || 0,
                    periapsis: packet.Periapsis || 0,
                    gForce: packet.GForce || 1.0,
                    dynamicPressure: packet.DynamicPressure || 0,
                    maxDynamicPressure: packet.MaxDynamicPressure || 0,
                    downrange: packet.Downrange || 0,
                    thrust: packet.Thrust || 0,
                    thrustToWeight: packet.ThrustToWeight || 0,
                    propellantFraction: packet.PropellantFraction != null ? packet.PropellantFraction : 1.0,
                    propellantMass: packet.PropellantMass || 0,
                    propellantCapacity: packet.PropellantCapacity || 0,
                    burningEngines: packet.BurningEngines || 0,
                    totalEngines: packet.TotalEngines || 0,
                    engines: packet.Engines || [],
                    events: packet.Events || [],
                    leftSlots: packet.LeftSlots || ['Speed', 'Altitude'],
                    rightSlots: packet.RightSlots || ['GForce'],
                    showTelemetryBar: packet.ShowTelemetryBar !== false,
                    showEngineDiagram: packet.ShowEngineDiagram !== false,
                    showTimeline: packet.ShowTimeline !== false,
                    showMissionClock: packet.ShowMissionClock !== false,
                    showPropellants: packet.ShowPropellants !== false,
                    scale: packet.Scale || 0.83,
                    opacity: packet.Opacity != null ? packet.Opacity : 1.0,
                    smoothingSeconds: packet.SmoothingSeconds || 0.12,
                    timelineWindowSeconds: packet.TimelineWindowSeconds || 240.0,
                    speedArcFullScale: packet.SpeedArcFullScale || 7800.0,
                    altitudeArcFullScaleKm: packet.AltitudeArcFullScaleKm || 100.0,
                    gForceArcFullScale: packet.GForceArcFullScale || 6.0,
                    engineDiagramRotation: packet.EngineDiagramRotation || 0.0
                };
            } catch (err) {
                console.error('[OBS Overlay] Error parsing telemetry packet:', err);
            }
        };

        ws.onclose = function () {
            setTimeout(connect, 1000);
        };

        ws.onerror = function () {
            ws.close();
        };
    }

    // Wait for Montserrat fonts to load before starting render loop
    if (document.fonts && document.fonts.load) {
        Promise.all([
            document.fonts.load('700 16px Montserrat'),
            document.fonts.load('400 16px Montserrat')
        ]).then(() => {
            requestAnimationFrame(render);
        }).catch(() => {
            requestAnimationFrame(render);
        });
    } else {
        requestAnimationFrame(render);
    }

    connect();
})();
