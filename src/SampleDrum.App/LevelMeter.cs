namespace SampleDrum.App;

/// <summary>Horizontal dBFS meter (-60..0) with peak hold and threshold / re-arm markers.</summary>
public sealed class LevelMeter : Control
{
    public const float MinDb = -60f;

    private float _levelDb = MinDb, _peakHoldDb = MinDb, _thresholdDb = -30f, _rearmDb = -40f;

    public LevelMeter()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        Height = 22;
    }

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float LevelDb { get => _levelDb; set { _levelDb = value; Invalidate(); } }
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float PeakHoldDb { get => _peakHoldDb; set { _peakHoldDb = value; Invalidate(); } }
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float ThresholdDb { get => _thresholdDb; set { _thresholdDb = value; Invalidate(); } }
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float RearmDb { get => _rearmDb; set { _rearmDb = value; Invalidate(); } }

    private int X(float db) => (int)Math.Round((Math.Clamp(db, MinDb, 0f) - MinDb) / -MinDb * (Width - 1));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.FromArgb(30, 30, 30));
        int x = X(_levelDb);
        var color = _levelDb >= -3f ? Color.OrangeRed : _levelDb >= _thresholdDb ? Color.Gold : Color.LimeGreen;
        using (var b = new SolidBrush(color)) g.FillRectangle(b, 0, 2, x, Height - 4);
        using (var p = new Pen(Color.White, 2)) { int px = X(_peakHoldDb); g.DrawLine(p, px, 0, px, Height); }
        using (var p = new Pen(Color.DeepSkyBlue, 2)) { int tx = X(_thresholdDb); g.DrawLine(p, tx, 0, tx, Height); }
        using (var p = new Pen(Color.MediumPurple, 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
        { int rx = X(_rearmDb); g.DrawLine(p, rx, 0, rx, Height); }
        using var tick = new Pen(Color.FromArgb(90, 90, 90));
        for (int db = -50; db < 0; db += 10) { int tx = X(db); g.DrawLine(tick, tx, Height - 5, tx, Height); }
    }
}
