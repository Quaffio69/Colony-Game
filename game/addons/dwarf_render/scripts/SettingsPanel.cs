using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;

namespace DwarfRender;

/// <summary>
/// Runtime tuning panel (F10). Built automatically from every [Export] property of
/// DwarfRenderSettings, grouped by [ExportGroup], so new settings appear without UI work.
/// Includes the three preset menus and Save / Load (user://dwarf_render_settings.tres).
/// Plain Godot controls; restyle with your Glyph UI theme if you like.
/// </summary>
public partial class SettingsPanel : CanvasLayer
{
	public DwarfRenderer Renderer { get; set; }
	public const string SavePath = "user://dwarf_render_settings.tres";
	public Key ToggleKey { get; set; } = Key.F10;
	/// <summary>Restyle the panel (e.g. with the game's UI theme).</summary>
	public Theme PanelTheme { set { if (_panel != null) _panel.Theme = value; } }

	PanelContainer _panel;
	VBoxContainer _list;
	readonly List<Action> _refreshers = new();

	public override void _Ready()
	{
		Layer = 100;
		_panel = new PanelContainer { Visible = false };
		_panel.SetAnchorsPreset(Control.LayoutPreset.RightWide);
		_panel.OffsetLeft = -340; _panel.OffsetTop = 8; _panel.OffsetBottom = -8; _panel.OffsetRight = -8;
		AddChild(_panel);
		var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		_panel.AddChild(scroll);
		_list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		scroll.AddChild(_list);
		Build();
		if (FileAccess.FileExists(SavePath)) Load();
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (e is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == ToggleKey) { _panel.Visible = !_panel.Visible; GetViewport().SetInputAsHandled(); }
	}

	DwarfRenderSettings S => Renderer.Settings;

	void Changed() { Renderer.ApplySettings(); }
	void RefreshAll() { foreach (var r in _refreshers) r(); }

	void Build()
	{
		foreach (var c in _list.GetChildren()) c.QueueFree();
		_refreshers.Clear();

		_list.AddChild(new Label { Text = "Dwarf Render  (F10)" });
		AddPresetMenu("Style preset", DwarfRenderSettings.StylePresets, n => S.ApplyStylePreset(n));
		AddPresetMenu("Foliage preset", DwarfRenderSettings.FoliagePresets, n => S.ApplyFoliagePreset(n));
		AddPresetMenu("ASCII look", DwarfRenderSettings.AsciiPresets, n => S.ApplyAsciiPreset(n));
		var row = new HBoxContainer();
		var save = new Button { Text = "Save" }; save.Pressed += () => ResourceSaver.Save(S, SavePath);
		var load = new Button { Text = "Load" }; load.Pressed += Load;
		var reset = new Button { Text = "Defaults" }; reset.Pressed += () => { Renderer.Settings = new DwarfRenderSettings(); Changed(); Build(); };
		row.AddChild(save); row.AddChild(load); row.AddChild(reset);
		_list.AddChild(row);

		var props = typeof(DwarfRenderSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Where(p => p.GetCustomAttribute<ExportAttribute>() != null && p.CanWrite)
			.OrderBy(p => p.MetadataToken);
		foreach (var p in props)
		{
			var grp = p.GetCustomAttribute<ExportGroupAttribute>();
			if (grp != null) _list.AddChild(new Label { Text = "— " + grp.Name.ToUpperInvariant() + " —" });
			AddControl(p, p.GetCustomAttribute<ExportAttribute>());
		}
	}

	void Load()
	{
		var loaded = ResourceLoader.Load<DwarfRenderSettings>(SavePath, null, ResourceLoader.CacheMode.Ignore);
		if (loaded == null) return;
		Renderer.Settings = loaded; Changed(); Build();
	}

	void AddPresetMenu(string label, string[] names, Action<string> apply)
	{
		var h = new HBoxContainer();
		h.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(120, 0) });
		var ob = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		ob.AddItem("(choose)");
		foreach (var n in names) ob.AddItem(n);
		ob.ItemSelected += idx => { if (idx > 0) { apply(names[idx - 1]); Changed(); RefreshAll(); } };
		h.AddChild(ob);
		_list.AddChild(h);
	}

	void AddControl(PropertyInfo p, ExportAttribute ex)
	{
		var h = new HBoxContainer();
		h.AddChild(new Label { Text = Nice(p.Name), CustomMinimumSize = new Vector2(150, 0), ClipText = true });
		var t = p.PropertyType;
		if (t == typeof(bool))
		{
			var cb = new CheckBox();
			cb.Toggled += on => { p.SetValue(S, on); Changed(); };
			_refreshers.Add(() => cb.SetPressedNoSignal((bool)p.GetValue(S)));
			h.AddChild(cb);
		}
		else if (t == typeof(float) || t == typeof(int))
		{
			double min = 0, max = 1, step = 0.01;
			if (ex.Hint == PropertyHint.Range)
			{
				var parts = ex.HintString.Split(',');
				min = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
				max = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
				if (parts.Length > 2) step = double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
			}
			else { min = 0; max = t == typeof(int) ? 100 : 500; step = t == typeof(int) ? 1 : 0.1; }
			var sl = new HSlider { MinValue = min, MaxValue = max, Step = step, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(110, 0) };
			var val = new Label { CustomMinimumSize = new Vector2(44, 0) };
			sl.ValueChanged += v =>
			{
				if (t == typeof(int)) p.SetValue(S, (int)Math.Round(v)); else p.SetValue(S, (float)v);
				val.Text = t == typeof(int) ? ((int)Math.Round(v)).ToString() : v.ToString("0.##");
				Changed();
			};
			_refreshers.Add(() => { var v = Convert.ToDouble(p.GetValue(S)); sl.SetValueNoSignal(v); val.Text = t == typeof(int) ? ((int)v).ToString() : v.ToString("0.##"); });
			h.AddChild(sl); h.AddChild(val);
		}
		else if (t.IsEnum)
		{
			var ob = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			var values = Enum.GetValues(t).Cast<object>().ToArray();
			foreach (var v in values) ob.AddItem(Nice(v.ToString()));
			ob.ItemSelected += idx => { p.SetValue(S, values[idx]); Changed(); };
			_refreshers.Add(() => ob.Select(Array.IndexOf(values, p.GetValue(S))));
			h.AddChild(ob);
		}
		else if (t == typeof(Color))
		{
			var cp = new ColorPickerButton { CustomMinimumSize = new Vector2(60, 24), EditAlpha = false };
			cp.ColorChanged += c => { p.SetValue(S, c); Changed(); };
			_refreshers.Add(() => cp.Color = (Color)p.GetValue(S));
			h.AddChild(cp);
		}
		else return;
		_list.AddChild(h);
		_refreshers[^1]();
	}

	static string Nice(string name) => string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]) ? " " + c : c.ToString()));
}
