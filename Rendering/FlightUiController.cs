using KSA;

namespace KSATelemetryOverlay.Rendering;

public sealed class FlightUiController
{
    private readonly List<GaugeCanvas> _suppressed = [];
    private bool _hidden;
    private int _seenCanvasCount = -1;

    public bool IsHiding => _hidden;
    public int SuppressedCount => _suppressed.Count;

    public void SetHidden(bool hidden)
    {
        if (hidden)
        {
            if (!_hidden)
            {
                _hidden = true;
                _seenCanvasCount = -1;
            }

            Maintain();
            return;
        }

        if (_hidden)
        {
            Restore();
        }
    }

    private void Maintain()
    {
        // Only touch the HUD in flight
        if (Program.ControlledVehicle is null)
        {
            return;
        }

        for (int i = 0; i < _suppressed.Count; i++)
        {
            if (_suppressed[i].Enabled)
            {
                _suppressed[i].SetEnabled(false);
            }
        }

        IReadOnlyList<GaugeCanvas> canvases = GaugeCanvas.AllCanvases;

        if (canvases.Count == _seenCanvasCount)
        {
            return;
        }

        _seenCanvasCount = canvases.Count;

        for (int i = 0; i < canvases.Count; i++)
        {
            GaugeCanvas canvas = canvases[i];

            if (!canvas.Enabled || canvas.AlwaysEnabled || _suppressed.Contains(canvas))
            {
                continue;
            }

            canvas.SetEnabled(false);
            _suppressed.Add(canvas);
        }
    }

    public void Restore()
    {
        _hidden = false;
        _seenCanvasCount = -1;

        if (_suppressed.Count == 0)
        {
            return;
        }

        IReadOnlyList<GaugeCanvas> canvases = GaugeCanvas.AllCanvases;

        for (int i = 0; i < _suppressed.Count; i++)
        {
            GaugeCanvas canvas = _suppressed[i];

            if (canvases.Contains(canvas))
            {
                canvas.SetEnabled(true);
            }
        }

        _suppressed.Clear();
    }
}
