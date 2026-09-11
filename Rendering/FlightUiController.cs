using KSA;

namespace KSATelemetryOverlay.Rendering;

public sealed class FlightUiController
{
    private readonly List<GaugeCanvas> _suppressed = [];
    private bool _hidden;
    public bool IsHiding => _hidden;
    public int SuppressedCount => _suppressed.Count;

    public void SetHidden(bool hidden)
    {
        if (hidden == _hidden)
        {
            if (hidden)
            {
                Suppress();
            }

            return;
        }

        _hidden = hidden;

        if (hidden)
        {
            Suppress();
        }
        else
        {
            Restore();
        }
    }

    private void Suppress()
    {
        if (Program.ControlledVehicle is null)
        {
            return;
        }

        IReadOnlyList<GaugeCanvas> canvases = GaugeCanvas.AllCanvases;

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
        if (_suppressed.Count == 0)
        {
            _hidden = false;
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
        _hidden = false;
    }
}
