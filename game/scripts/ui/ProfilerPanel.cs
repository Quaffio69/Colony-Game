using System;
using Godot;

namespace ColonyGame.UI;

/// <summary>
/// The profiler (Graphics → Performance → Show profiler; the diorama's): frame rate and worst frame of the last second,
/// sim cost per frame, GPU time of the pixel-art world image, draw calls, primitives, memory, the art resolution and
/// the zoom / ASCII state. A small glass panel under the HUD, refreshed four times a second.
/// </summary>
public partial class ProfilerPanel : GlyphPanel
{
    private readonly GameUi _ui;
    private readonly Label _text = new();
    private double _refreshIn, _secondIn, _worst, _worstShown;
    private Rid _measured;

    public ProfilerPanel(GameUi ui)
    {
        _ui = ui;
        MouseFilter = MouseFilterEnum.Ignore;
        _text.MouseFilter = MouseFilterEnum.Ignore;
        _text.ThemeTypeVariation = "Dim";
        AddChild(_text);
        Visible = false;
    }
    public ProfilerPanel() : this(null!) { }

    public override void _Process(double delta)
    {
        if (_ui == null) return;
        bool on = _ui.Main.Look?.ShowProfiler == true;
        Visible = on;
        if (!on) return;
        _worst = Math.Max(_worst, delta * 1000);
        if ((_refreshIn -= delta) > 0) return;
        _refreshIn = 0.25;
        if ((_secondIn -= 0.25) <= 0) { _secondIn = 1; _worstShown = _worst; _worst = 0; } // worst frame of the last second

        string gpu = "";
        if (_ui.Main.Renderer?.ArtViewport is { } art)
        {
            var rid = art.GetViewportRid();
            if (rid != _measured) { RenderingServer.ViewportSetMeasureRenderTime(rid, true); _measured = rid; }
            gpu = $" · world GPU {RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid):0.0} ms";
        }
        double fps = Engine.GetFramesPerSecond();
        long draws = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame);
        long prims = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame);
        long vram = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed);
        string lines =
            $"FPS {fps:0} · frame {1000 / Math.Max(1, fps):0.0} ms · worst {_worstShown:0.0} ms\n" +
            $"sim {_ui.Main.SimMs:0.00} ms/frame{gpu}\n" +
            $"draws {draws} · {prims / 1000.0:0}k prims · VRAM {vram / 1048576} MB · RAM {(long)(OS.GetStaticMemoryUsage() / 1048576) + GC.GetTotalMemory(false) / 1048576} MB";
        if (_ui.Main.Renderer is { } r)
        {
            var a = r.ArtSize;
            lines += $"\nart {a.X}×{a.Y} px · zoom {r.Zoom:0.00}× · ASCII {r.AsciiAmount * 100:0}%{(r.FarViewIsText ? " (all symbols)" : "")}";
        }
        _text.Text = lines;
        float cw = GlyphTheme.Cw, ch = GlyphTheme.Ch;
        int cols = 0, rows = 0;
        foreach (var l in lines.Split('\n')) { cols = Math.Max(cols, l.Length); rows++; }
        Size = new Vector2((cols + 3) * cw, (rows + 1) * ch);
        Position = new Vector2(cw * 1.5f, ch * 2.4f);
        _text.Position = new Vector2(cw * 1.5f, ch * 0.5f);
        _text.Size = new Vector2(cols * cw, rows * ch);
    }
}
