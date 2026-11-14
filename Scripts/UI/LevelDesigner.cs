using System.Linq;
using BridgeArchitect3D.Core;
using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.UI
{
	/// <summary>
	/// Level design mode (main menu). Uses the same editor as the in-game level settings (<see cref="LevelEditorPanel"/>);
	/// on top of it: start a new level or copy a built-in one, save / save and play, and the list of saved levels
	/// (edit, play, remove = move to the deleted folder). Levels are saved by name to user://custom_levels.
	/// Playing a level and opening its in-game settings edits the same file, so changes made there show up here.
	/// </summary>
	public class LevelDesigner
	{
		private GameManager _gm;
		private Control _overlay;
		private LevelEditorPanel _panel;
		private VBoxContainer _savedList;
		/// <summary>File id of the saved level being edited (null = not saved yet).</summary>
		private string _editingId;

		public static LevelDesigner Create(Control parent, GameManager gm)
		{
			var d = new LevelDesigner { _gm = gm };
			var (overlay, content) = UiTheme.MakeDialog(parent, Loc.T("关卡设计"), 1060);
			d._overlay = overlay;
			d._panel = LevelEditorPanel.Create(overlay, content, gm.Db);
			var a = d._panel.Actions;
			a.AddChild(UiTheme.MakeButton(Loc.T("新建"), d.New, 70));
			var save = UiTheme.MakeButton(Loc.T("保存关卡"), () => d.Save(false), 100);
			a.AddChild(save);
			var play = UiTheme.MakeButton(Loc.T("保存并试玩"), () => d.Save(true), 110);
			UiTheme.Primary(play); a.AddChild(play);
			a.AddChild(UiTheme.MakeButton(Loc.T("关闭"), () => overlay.Visible = false, 70));

			var x = d._panel.Extra;
			var tpl = new HBoxContainer(); tpl.AddThemeConstantOverride("separation", 6);
			tpl.AddChild(UiTheme.MakeLabel(Loc.T("以内置关卡为模板"), 13, UiTheme.Muted));
			var levels = gm.Db.Levels.ToList();
			var pick = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true };
			foreach (var l in levels) pick.AddItem(l.Name);
			tpl.AddChild(pick);
			tpl.AddChild(UiTheme.MakeButton(Loc.T("复制"), () =>
			{
				if (pick.Selected < 0) return;
				var c = LevelEditing.AsCustom(levels[pick.Selected]);
				c.Name = Loc.F("{0}（副本）", (c.Name)); c.Id = CustomLevelBuilder.IdFor(c.Name);
				d._editingId = null; d._panel.Load(c);
				d._panel.Status(Loc.T("已复制内置关卡，修改后保存为自定义关卡（内置关卡不变）"), true);
			}, 70));
			x.AddChild(tpl);
			x.AddChild(UiTheme.MakeLabel(Loc.T("已保存的关卡"), 14, UiTheme.Muted));
			var listScroll = new ScrollContainer { CustomMinimumSize = new Vector2(400, 150) };
			d._savedList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			listScroll.AddChild(d._savedList); x.AddChild(listScroll);
			d.New();
			return d;
		}

		public void Open() { RefreshSaved(); _overlay.Visible = true; }

		internal void PreviewLift()
        {
            Open();
            if(CustomLiftLevels.Generate(_panel.Level,_gm.Db,"tunnel_immersed",true,out var level,out var error))_panel.Load(level);
            else _panel.Status(error);
        }
        private void New()
		{
			_editingId = null;
			_panel.Load(CustomLevelBuilder.Build(new CustomLevelSpec(), _gm.Db));
			_panel.Status("");
		}

		private void Save(bool play)
		{
			var errs = _panel.Problems();
			if (errs.Count > 0) { _panel.ShowErrors(Loc.T("无法保存：\n"), errs); return; }
			var lv = LevelEditing.AsCustom(_panel.Level);
			if (!SaveManager.SaveCustomLevel(lv, out var err)) { _panel.Status(Loc.T("保存失败：") + err); return; }
			// Renamed: the level now lives under its new name; the old file goes to the deleted folder
			if (_editingId != null && _editingId != lv.Id) SaveManager.DeleteCustomLevel(_editingId);
			SaveManager.RemoveLevelOverride(lv.Id);
			_editingId = lv.Id; _panel.Level.Id = lv.Id;
			_panel.Status(Loc.F("已保存关卡“{0}”", (lv.Name)), true);
			RefreshSaved();
			if (play) _gm.StartLevel(lv.Id, SaveManager.HasSave(lv.Id));
		}

		private void RefreshSaved()
		{
			foreach (var c in _savedList.GetChildren()) c.QueueFree();
			var list = SaveManager.ListCustomLevels(_gm.Db);
			if (list.Count == 0) _savedList.AddChild(UiTheme.MakeLabel(Loc.T("还没有自定义关卡"), 13, UiTheme.Muted));
			foreach (var saved in list)
			{
				// Settings saved in game by older builds live in an override: show (and edit) what is actually played
				var s = _gm.ResolveLevel(saved.Id) ?? saved;
				var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 6);
				row.AddChild(new Label { Text = Loc.F("{0}（河宽 {1:0.#} m · ${2:N0}）", (s.Name), (s.Terrain.RiverWidth), (s.Budget)), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true });
				var lv = s;
				row.AddChild(UiTheme.MakeButton(Loc.T("编辑"), () => { _editingId = lv.Id; _panel.Load(lv); _panel.Status(Loc.F("正在编辑“{0}”", (lv.Name))); }, 60));
				row.AddChild(UiTheme.MakeButton(Loc.T("游玩"), () => _gm.StartLevel(lv.Id, SaveManager.HasSave(lv.Id)), 60));
				Button del = null;
				del = UiTheme.MakeButton(Loc.T("删除"), () =>
				{
					// Two clicks: the file is moved to custom_levels/deleted, not destroyed
					if (del.Text != Loc.T("确认删除")) { del.Text = Loc.T("确认删除"); return; }
					SaveManager.DeleteCustomLevel(lv.Id);
					if (_editingId == lv.Id) _editingId = null;
					RefreshSaved();
				}, 80);
				row.AddChild(del);
				_savedList.AddChild(row);
			}
		}
	}
}
