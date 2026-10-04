using System;
using System.Reflection;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「图鉴/商店 快捷按钮」Mod 的托管运行时入口。
///
/// ── 需求（用户）────────────────────────────────────────────────
/// 横版 UI（`MobilePreset`）下，**选卡界面里不显示图鉴和商店按钮**
/// （游戏在 `TowerDefenseBattleFeaturePacketBank._UpdateGuiTopButtonVisibility()` 里
///   `if (mobileMode || !_packetBankEntered) { 两个按钮 Visible = false; }` 直接藏掉）。
/// 要求：**在选卡界面右上角补两个按钮**（左「商店」、右「查看图鉴」，横向并排），
/// 外观与游戏自带按钮一致；并且**只在选卡阶段显示**（出选卡页面前、选卡完成开打后都不显示）。
///
/// ── 做法（全部来自解包源码，不是猜的）───────────────────────────
/// 1. 目标面板 = `TowerDefenseInGamePacketBank`（选卡界面本体，`Control`）。
///    它根节点 offset_top/bottom 都是 600（**高度为 0**，内容靠 `Translate`
///    子节点从 y=600 滑上来）⇒ **不能用锚点定位**，必须按视口尺寸算坐标。
/// 2. 游戏自带的那两个按钮就在这个面板下：
///    `TowerDefenseInGamePacketBank/ShopButton`（`text = "商店"`）
///    `TowerDefenseInGamePacketBank/AlmanacButton`（`text = "查看图鉴"`），
///    但它们被 `_HidePacketBankLegacyButtons()` 永久 `Visible = false`。
///    ⇒ **直接 Duplicate() 它们当模板**：外观/字体/9-patch 贴图/字号全部原样继承，
///      不用自己拼 UI（`NinePatchButtonBase : MarginContainer`）。
/// 3. 回调：面板脚本上就有公开方法 `ShopButtonPressed()` / `AlmanacButtonPressed()`
///    （内部走 `DialogManager.Instance.DialogCreate("Shop"/"Almanac")`），
///    用反射调它们——比直接调 DialogCreate 更稳（跟着游戏自己的判断走，
///    例如商店还要过 `GlobalFeatureManager.IsUnlocked("Shop")`）。
///
/// ── 铁律 ──────────────────────────────────────────────────────
/// Initialize / OnAllModsLoaded / Shutdown **一律不许抛**：抛出去 → 整包无条件回滚。
/// 三个回调全部 try/catch；每帧逻辑走 `SceneTree.process_frame` 信号
/// （手写 csproj 没有 Godot 源码生成器 ⇒ 自定义 Node 子类的 _Process 不会被调用）。
/// </summary>
public sealed class UIExtraButtonsEntry : IXWModRuntimeEntry
{
	private const string LogPrefix = "[UIExtraButtons] ";

	/// <summary>承载两个按钮的容器节点名（幂等判断用）。</summary>
	private const string HostName = "ModExtraButtonsHost";

	/// <summary>我这两个按钮的节点名（用来区分游戏自带的那两个模板）。</summary>
	private const string MyShopName = "ModExtraShopButton";
	private const string MyAlmanacName = "ModExtraAlmanacButton";

	/// <summary>诊断日志总开关（排查时置 true）。</summary>
	private static readonly bool EnableInfoLog = false;

	/// <summary>
	/// ★ 关键节点缺失只报一次（**始终输出**，不受 <see cref="EnableInfoLog"/> 门控）。
	///
	/// 为什么必须有它：本 Mod 每帧找 `TowerDefenseInGamePacketBank` 面板、再找它的
	/// 两个模板按钮，全靠"类名/节点名"字符串匹配。名字对不上时**什么都不会发生**
	/// ——按钮不出现、日志一片安静（三个入口回调都是 try/catch 吞异常的）。
	/// ⇒ 把"面板没找到 / 模板没找到"这两件事各打一次，能立刻区分
	///   "没进选卡界面"和"进了但节点名变了"。
	/// </summary>
	private const bool ReportMissingOnce = true;

	/// <summary>
	/// ★ **诊断直出总开关**（2026-10-01 用户要求"关掉 log"后新增）。
	/// `false` ⇒ 自检 / 已就位 / 判据探针 / 翻转 / 模板告警 全部静默；
	/// `true`  ⇒ 全部输出，用于排查。
	/// ⚠️ `Warn()`（`GD.PrintErr`）**不受**此开关影响 —— 真正的异常报告应当保留。
	/// </summary>
	private static readonly bool EnableDiagLog = false;

	/// <summary>诊断直出（受 <see cref="EnableDiagLog"/> 门控，异常全吞）。</summary>
	private void Diag(string msg)
	{
		if (!EnableDiagLog)
		{
			return;
		}
		// ⚠️⚠️ 这里**必须**是 GD.Print，不能是 Diag —— 批量替换
		//   `GD.Print(LogPrefix + …)` → `Diag(…)` 时若把这行也换掉，
		//   `Diag` 就自己调自己 ⇒ **无限递归 ⇒ StackOverflowException**（catch 抓不住）。
		try { GD.Print(LogPrefix + msg); } catch { }
	}

	private bool _reportedNoTemplate;

	/// <summary>"解析不到 PacketBank 功能对象"只报一次（v1.0.3）。</summary>
	private bool _noFeatureReported;

	/// <summary>`GameSaveManager` 类型与 `GetConfigValue`（读横版开关 `MobilePreset` 用）。</summary>
	private static Type _tSaveMgr;
	private static MethodInfo _mGetConfig;

	/// <summary>启动后是否**曾经**看到过选卡面板（用来区分"还没进战斗"与"节点名变了"）。</summary>
	private bool _sawPanel;

	/// <summary>显示判据探针只打一次（按面板 instanceId）。</summary>
	private readonly System.Collections.Generic.HashSet<ulong> _gateReported
		= new System.Collections.Generic.HashSet<ulong>();

	/// <summary>上一次的判据结果（用来只在翻转时打一条）。</summary>
	private bool _lastGateShow;
	private bool _gateFlipLogged;

	/// <summary>启动时刻，用于"从来没找到面板"的超时告警。</summary>
	private ulong _startMs;
	private bool _reportedNeverSawPanel;

	/// <summary>
	/// 按钮与屏幕**右边缘 / 上边缘**的间距（像素，视口坐标）。
	/// 右上角是「菜单」按钮的地盘（`GUITop/ButtonPause`，约 105×77），
	/// 右边距要留得比它宽，否则会压住菜单。
	/// </summary>
	private const float MarginRight = 132f;
	private const float MarginTop = 26f;
	private const float Gap = 10f;

	/// <summary>兜底按钮尺寸（模板取不到尺寸时用；模板实测 157×31）。</summary>
	private static readonly Vector2 FallbackSize = new Vector2(157f, 31f);

	private XWModRuntimeContext _context;
	private SceneTree _tree;
	private Callable _tick;
	private bool _connected;
	private bool _faultReported;

	/// <summary>已经接管的面板实例 id（面板重建时会换实例 ⇒ 重新建按钮）。</summary>
	private ulong _panelId;
	private Control _host;
	private Control _shop;
	private Control _almanac;

	/// <summary>模板只取一次即可（取自游戏自带按钮），缓存下来给后续重建用。</summary>
	private Control _shopTemplate;
	private Control _almanacTemplate;
	private bool _templateReported;

	// ================================================================ 入口三回调

	public void Initialize(XWModRuntimeContext context)
	{
		try
		{
			_context = context;
			_faultReported = false;
			string root = (context == null) ? "<null>" : context.PackageRoot;
			Info("初始化完成；PackageRoot=" + root
				+ "。将在选卡界面右上角补「商店 / 查看图鉴」两个按钮（只在选卡阶段显示）。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(LogPrefix + "Initialize 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	public void OnAllModsLoaded()
	{
		try
		{
			_tree = Engine.GetMainLoop() as SceneTree;
			if (_tree == null)
			{
				Warn("拿不到 SceneTree，功能不可用。");
				return;
			}
			// ★ 手写 csproj 没有 Godot 源码生成器 ⇒ 自定义 Node 的 _Process 不会被调用，
			//   每帧逻辑只能走信号通道；process_frame 在暂停时也照样发。
			_tick = Callable.From(new Action(OnProcessFrame));
			_tree.Connect("process_frame", _tick);
			_connected = true;
			try { _startMs = Time.GetTicksMsec(); } catch { }

			// 反射准备：读取横版开关 MobilePreset（用来决定"该不该由我补按钮"）。
			// 直接用 typeof —— csproj 已 Reference 游戏程序集，编译期解析最可靠
			// （此前用 asm.GetType("GameSaveManager") 找不到，是踩过的坑）。
			try
			{
				_tSaveMgr = typeof(GameSaveManager);
				_mGetConfig = _tSaveMgr.GetMethod("GetConfigValue",
					BindingFlags.Public | BindingFlags.Instance, null,
					new Type[] { typeof(string) }, null);
			}
			catch { }
			// 启动自检（始终输出一次）：确认信号通道真的挂上了 —— 手写 csproj 没有
			// Godot 源码生成器，自定义 Node 的 _Process 不会被调用，全靠这条信号。
			try
			{
				Diag("自检：SceneTree=" + (_tree != null)
					+ " process_frame 已连接=" + _tree.IsConnected("process_frame", _tick)
					+ " → 每帧通道就绪；按钮将在进入选卡界面时创建。");
			}
			catch { }
			Info("已挂载 process_frame 回调。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(LogPrefix + "OnAllModsLoaded 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	public void Shutdown()
	{
		try
		{
			if (_connected && _tree != null && GodotObject.IsInstanceValid(_tree)
				&& _tree.IsConnected("process_frame", _tick))
			{
				_tree.Disconnect("process_frame", _tick);
			}
			_connected = false;

			// 清掉自建 UI，避免下次加载重复
			foreach (Control c in new Control[] { _host, _shop, _almanac })
			{
				if (c != null && GodotObject.IsInstanceValid(c))
				{
					c.QueueFree();
				}
			}
			_host = null; _shop = null; _almanac = null;
			_panelId = 0UL;
			Info("已卸载。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(LogPrefix + "Shutdown 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	// ================================================================ 每帧

	private void OnProcessFrame()
	{
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree) || _tree.Root == null)
			{
				return;
			}
			Maintain();
		}
		catch (Exception ex)
		{
			if (!_faultReported)
			{
				_faultReported = true;
				Warn("每帧维护异常（只报一次）：" + ex.Message);
			}
		}
	}

	private void Maintain()
	{
		// 只在有选卡面板时工作（主菜单/关卡选择没有这个节点）
		Control panel = FindNodeByClassName(_tree.Root, "TowerDefenseInGamePacketBank") as Control;
		if (panel == null || !GodotObject.IsInstanceValid(panel))
		{
			// 面板没了 ⇒ 清引用（下次重建）
			if (_host != null && !GodotObject.IsInstanceValid(_host))
			{
				_host = null; _shop = null; _almanac = null; _panelId = 0UL;
			}
			// 启动 45 秒后**从来没**找到过面板 ⇒ 报一次（区分"还没进战斗"与"类名变了"）
			if (ReportMissingOnce && !_sawPanel && !_reportedNeverSawPanel && _startMs > 0)
			{
				if (Time.GetTicksMsec() - _startMs > 45000UL)
				{
					_reportedNeverSawPanel = true;
					try
					{
						Diag("提示：启动 45 秒内没见过选卡面板"
							+ "（TowerDefenseInGamePacketBank）。若你已进过战斗的选卡界面，"
							+ "说明节点类名变了，本 Mod 的按钮不会出现。");
					}
					catch { }
				}
			}
			return;
		}

		// 第一次发现面板 = 进了选卡界面 ⇒ 打一条"已就位"（始终输出一次，便于确认 Mod 活了）
		if (!_sawPanel)
		{
			_sawPanel = true;
			try
			{
				Diag("已就位：找到选卡面板 " + panel.GetType().Name
					+ "（instanceId=" + panel.GetInstanceId() + "），开始补右上角按钮。");
			}
			catch { }
		}

		ulong id = panel.GetInstanceId();
		bool hostOk = _host != null && GodotObject.IsInstanceValid(_host)
			&& _shop != null && GodotObject.IsInstanceValid(_shop)
			&& _almanac != null && GodotObject.IsInstanceValid(_almanac)
			&& _host.IsInsideTree();

		if (!hostOk || id != _panelId)
		{
			BuildButtons(panel, id);
		}
		if (_host == null || !GodotObject.IsInstanceValid(_host))
		{
			return;
		}

		// ★★ 可见性：**只在"选卡阶段"显示**。
		//
		// 用户实测反馈（2026-10-01）：按钮在"出来选卡页面前"以及"选卡完成、开始游戏后"
		// 都还挂在屏幕上，要求这两种情况都不显示。
		// 判据见 `ShouldShow()`（面板可见 + Translate 已滑入 + 游戏正牌状态位 _packetBankEntered），
		// 探针日志见 `ReportGateOnce()` —— 跑一次游戏就能看出卡在哪个条件上。
		bool show = ShouldShow(panel);
		if (_shop != null && GodotObject.IsInstanceValid(_shop))
		{
			_shop.Visible = show;
		}
		if (_almanac != null && GodotObject.IsInstanceValid(_almanac))
		{
			_almanac.Visible = show;
		}
		if (_host != null && GodotObject.IsInstanceValid(_host) && _host.Visible != show)
		{
			_host.Visible = show;
		}
		if (!show)
		{
			// 隐藏期间不必每帧算位置
			return;
		}

		// 显示时每帧压住位置：面板的入场/出场动画会把子节点搬来搬去。
		Layout();
	}

	/// <summary>
	/// 现在该不该显示这两个按钮 = 是否处于「选卡阶段」**且游戏自己的按钮确实是藏着的**。
	///
	/// 判据（任一不满足即隐藏）：
	///   ① 面板自身可见（`TowerDefenseInGamePacketBank` 是 `Control`，选卡面板的整体开关）；
	///   ② 面板的 `Translate` 子节点已滑入屏幕（`Position.Y &lt; 500`，
	///      照抄游戏 `_OnUISwitched()` 的口径）—— 入场前 / 出场后都在屏幕外；
	///   ③ **游戏原生的那一对按钮没在显示** —— 见 <see cref="NativeButtonsHidden"/>；
	///   ④ **正牌状态位** `TowerDefenseBattleFeaturePacketBank._packetBankEntered`
	///      （由 `PacketBankAnimationPlayer` 的 Enter/MobileEnter → true、
	///        Exit/MobileExit → false 驱动）—— 拿到就以它为准。
	///
	/// ★ 为什么要 ③（2026-10-01 用户实测反馈：「你这图鉴按钮为什么切换到非横版 UI 的情况下还显示？」）：
	///   游戏只在**横版**（`MobilePreset = true`）时才把自带那对按钮藏起来
	///   （`_UpdateGuiTopButtonVisibility()` 里 `if (mobileMode || !_packetBankEntered) → Visible=false`）。
	///   ⇒ 切成竖版后，**游戏自带的「商店/查看图鉴」自己就出来了**，
	///     我这个补充按钮就变成**重复的一对**（用户截图里上下两排同款按钮就是这个）。
	///   ⇒ 所以"只在横版显示"：这本来就是本 Mod 的立项初衷（横版下没按钮），
	///     竖版交给游戏原生即可。
	///
	/// 三个判据**任意一个不成立就隐藏**，宁可多藏不要漏出来；只有"全取不到"时才回落成显示。
	/// </summary>
	private bool ShouldShow(Control panel)
	{
		try
		{
			bool panelVisible = panel.IsVisibleInTree();
			bool onScreen = true;
			Node tr = panel.GetNodeOrNull("Translate");
			if (tr is Control tc)
			{
				onScreen = tc.Position.Y < 500f;
			}
			bool nativeHidden = NativeButtonsHidden(panel);
			object enteredObj = PacketBankEntered();
			bool entered = (enteredObj is bool eb) ? eb : true;

			bool show = panelVisible && onScreen && nativeHidden && entered;
			ReportGateOnce(panel, panelVisible, onScreen, enteredObj, show);
			return show;
		}
		catch
		{
			return true;
		}
	}

	/// <summary>
	/// 游戏原生的那对按钮现在是不是**藏着**的。只有藏着才需要我们补。
	///
	/// 双重判据，任一"说它在显示"就返回 false（= 不需要我）：
	///   ① **横版开关**：`GameSaveManager.GetConfigValue("MobilePreset").AsBool()`。
	///      游戏 `_UpdateGuiTopButtonVisibility()` 就是拿它决定藏不藏的：
	///      `if (mobileMode || !_packetBankEntered) { 两个都 false }`。
	///   ② **实测可见性**：`GUITop/ShopButton`、`GUITop/AlmanacButton`（游戏自己的那份）
	///      是否 `IsVisibleInTree()`。这条是兜底 —— 将来游戏改了判定逻辑，这条仍然准。
	///
	/// 拿不到横版开关时（罕见）返回 true，让 ② 单独定夺。
	/// </summary>
	private bool NativeButtonsHidden(Control panel)
	{
		try
		{
			// ① 横版开关
			object mobile = ReadMobilePreset();
			if (mobile is bool mp && !mp)
			{
				return false;   // 竖版 ⇒ 游戏自带按钮会显示 ⇒ 不需要我
			}

			// ② 游戏自带按钮的实际可见性（挂在 GUITop 下，不在选卡面板里，所以从 Root 找）
			Node top = FindNodeByName(_tree.Root, "GUITop");
			if (top != null && GodotObject.IsInstanceValid(top))
			{
				Control gShop = top.GetNodeOrNull<Control>("ShopButton");
				Control gAlm = top.GetNodeOrNull<Control>("AlmanacButton");
				bool anyVisible =
					(gShop != null && GodotObject.IsInstanceValid(gShop) && gShop.IsVisibleInTree())
					|| (gAlm != null && GodotObject.IsInstanceValid(gAlm) && gAlm.IsVisibleInTree());
				if (anyVisible)
				{
					return false;   // 游戏自己已经在显示了 ⇒ 不要重复
				}
			}
		}
		catch { }
		return true;
	}

	/// <summary>
	/// 读 `GameSaveManager.GetConfigValue("MobilePreset").AsBool()`（横版 UI 开关）。
	/// 拿不到返回 **true**（按横版处理 —— 那正是本 Mod 的目标场景，宁可显示）。
	///
	/// ⚠️ `GameSaveManager.Instance` 是**字段**不是属性（游戏里 `Instance` 两种形态混用，
	///   见 `GameSaveManager.cs:102` vs `Global.cs:25`）—— 这里字段/属性都试。
	/// </summary>
	private object ReadMobilePreset()
	{
		try
		{
			if (_mGetConfig == null || _tSaveMgr == null)
			{
				return null;
			}
			object mgr = null;
			const BindingFlags SF = BindingFlags.Public | BindingFlags.NonPublic
				| BindingFlags.Static | BindingFlags.DeclaredOnly;
			for (Type cur = _tSaveMgr; cur != null && mgr == null; cur = cur.BaseType)
			{
				FieldInfo fi = cur.GetField("Instance", SF);
				if (fi != null)
				{
					mgr = fi.GetValue(null);
				}
			}
			if (mgr == null)
			{
				PropertyInfo pi = _tSaveMgr.GetProperty("Instance",
					BindingFlags.Public | BindingFlags.Static);
				if (pi != null)
				{
					mgr = pi.GetValue(null);
				}
			}
			if (mgr == null)
			{
				return null;
			}
			object v = _mGetConfig.Invoke(mgr, new object[] { "MobilePreset" });
			if (v is Variant va)
			{
				return va.AsBool();
			}
			if (v is bool b)
			{
				return b;
			}
		}
		catch { }
		return null;
	}

	/// <summary>
	/// 三个判据各打一次探针（只在**第一次判定**与**状态翻转**时输出），
	/// 这样用户跑一次游戏就能看出"到底卡在哪个条件上"。
	/// 始终输出，不受 <see cref="EnableInfoLog"/> 门控。
	/// </summary>
	private void ReportGateOnce(Control panel, bool panelVisible, bool onScreen,
		object enteredObj, bool show)
	{
		try
		{
			if (_gateReported.Add(panel.GetInstanceId()))
			{
				Diag("显示判据探针：面板可见=" + panelVisible
					+ " Translate已滑入=" + onScreen
					+ " _packetBankEntered=" + (enteredObj == null ? "<读不到>" : enteredObj.ToString())
					+ " ⇒ 当前=" + (show ? "显示" : "隐藏"));
			}
			if (_lastGateShow != show)
			{
				_lastGateShow = show;
				if (_gateFlipLogged)
				{
					Diag("显示状态翻转 ⇒ " + (show ? "显示" : "隐藏")
						+ "（面板可见=" + panelVisible + " 已滑入=" + onScreen
						+ " entered=" + (enteredObj == null ? "<读不到>" : enteredObj.ToString()) + "）");
				}
				_gateFlipLogged = true;
			}
		}
		catch { }
	}

	/// <summary>
	/// 读 `TowerDefenseBattleFeaturePacketBank._packetBankEntered`
	/// —— 游戏自己"进没进卡片界面"的正牌状态位（进入 Enter 动画完成 → true，
	/// Exit 动画完成 → false；进入时还会被 `_OnUISwitched` 重算一次）。
	///
	/// ★ 拿到 feature 的正确通道 = `TowerDefenseManager.GetPacketBankFeature()`
	///   （`Core/TowerDefenseManager/TowerDefenseManager.cs:2330`）。
	///   **不要**从面板节点沿父链瞎找 —— 面板是节点（`Control`），feature 是普通对象，
	///   两者不在同一棵树里；feature 侧只有 `packetBank.packetBankFeature = this;`
	///   这一个方向的引用（`TowerDefenseBattleFeaturePacketBank.cs:58`）。
	///
	/// 该字段是 `private bool`，只能反射读。全失败返回 null（调用方按"拿不到"处理）。
	/// </summary>
	private object PacketBankEntered()
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return null;
			}
			object feat = mgr.GetPacketBankFeature();
			if (feat == null)
			{
				return null;
			}
			return GetMember(feat, "_packetBankEntered");
		}
		catch { }
		return null;
	}

	// ================================================================ 建按钮

	/// <summary>
	/// 取游戏自带的两个按钮当模板（`Duplicate()` 后外观完全一致）。
	/// 注意：**不要**直接复用游戏那两个节点（它们由 `_HidePacketBankLegacyButtons()` 管，
	/// 而且被 `_OnPacketBankAnimationFinished` 反复改 Visible）；我们只取一次模板。
	/// </summary>
	private void CacheTemplates(Control panel)
	{
		if (_shopTemplate == null || !GodotObject.IsInstanceValid(_shopTemplate))
		{
			_shopTemplate = panel.GetNodeOrNull<Control>("ShopButton");
		}
		if (_almanacTemplate == null || !GodotObject.IsInstanceValid(_almanacTemplate))
		{
			_almanacTemplate = panel.GetNodeOrNull<Control>("AlmanacButton");
		}
		if (!_templateReported && _shopTemplate != null && _almanacTemplate != null)
		{
			_templateReported = true;
			Info("模板已取到：ShopButton=" + _shopTemplate.GetType().Name
				+ " size=" + _shopTemplate.Size
				+ " / AlmanacButton=" + _almanacTemplate.GetType().Name
				+ " size=" + _almanacTemplate.Size);
		}
	}

	private void BuildButtons(Control panel, ulong panelId)
	{
		try
		{
			// 旧容器先清掉（面板换了实例时，旧容器会随旧面板一起被释放，但保险起见）
			if (_host != null && GodotObject.IsInstanceValid(_host))
			{
				_host.QueueFree();
			}
			_host = null; _shop = null; _almanac = null;

			CacheTemplates(panel);
			if (_shopTemplate == null || _almanacTemplate == null)
			{
				// 模板还没就绪（面板刚实例化）⇒ 下一帧再试，不报错。
				// 但若**反复**拿不到，说明节点名与预期不符 —— 报一次便于排查。
				if (!_reportedNoTemplate && panel.IsInsideTree())
				{
					_reportedNoTemplate = true;
					try
					{
						Diag("警告：选卡面板下找不到模板按钮"
							+ "（ShopButton=" + (_shopTemplate != null)
							+ " AlmanacButton=" + (_almanacTemplate != null)
							+ "），本 Mod 的按钮不会出现。请核对面板子节点名。");
					}
					catch { }
				}
				return;
			}

			// 容器：挂在选卡面板本身上（与游戏自带两个按钮同级，坐标同一空间）。
			//   ⚠️ 面板根高度为 0，所以容器不用锚点，直接给 GlobalPosition。
			Control host = new Control();
			host.Name = HostName;
			host.MouseFilter = Control.MouseFilterEnum.Ignore;   // 容器不吃点击，只有按钮吃
			panel.AddChild(host, false, Node.InternalMode.Disabled);

			Control shop = CloneButton(_shopTemplate, MyShopName);
			Control almanac = CloneButton(_almanacTemplate, MyAlmanacName);
			if (shop == null || almanac == null)
			{
				host.QueueFree();
				return;
			}
			host.AddChild(shop, false, Node.InternalMode.Disabled);
			host.AddChild(almanac, false, Node.InternalMode.Disabled);

			// 回调：
			// ★★★ v1.0.3 修复「横版 UI 下点了没反应」（用户反馈）：
			//   `ShopButtonPressed()` / `AlmanacButtonPressed()` **不在面板节点上**，
			//   而在功能类 `TowerDefenseBattleFeaturePacketBank`（**不是 Node**）上：
			//       TowerDefenseBattleFeaturePacketBank.cs:613  ShopButtonPressed()
			//       TowerDefenseBattleFeaturePacketBank.cs:622  AlmanacButtonPressed()
			//   旧版把回调目标写成 `panel`（`TowerDefenseInGamePacketBank`，一个 Control），
			//   反射 `GetMethod("ShopButtonPressed")` 必然取到 null ⇒ **静默什么都不做**
			//   （`InvokeMethod` 用 `m?.Invoke`，null 就悄悄跳过）。
			//   ⇒ 现在**每次点击时**都重新解析功能对象再调用（面板可能被重建，缓存引用不安全）。
			Action onShop = delegate
			{
				object feat = ResolvePacketBankFeature(panel);
				if (feat == null)
				{
					WarnOnceNoFeature();
					return;
				}
				InvokeMethod(feat, "ShopButtonPressed");
			};
			Action onAlmanac = delegate
			{
				object feat = ResolvePacketBankFeature(panel);
				if (feat == null)
				{
					WarnOnceNoFeature();
					return;
				}
				InvokeMethod(feat, "AlmanacButtonPressed");
			};
			WirePressed(shop, onShop);
			WirePressed(almanac, onAlmanac);

			shop.Visible = true;
			almanac.Visible = true;

			_host = host; _shop = shop; _almanac = almanac; _panelId = panelId;
			Layout();
			Info("已创建右上角按钮（左=商店、右=查看图鉴）。");
		}
		catch (Exception ex)
		{
			if (!_faultReported)
			{
				_faultReported = true;
				Warn("创建按钮异常（只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>`Duplicate()` 模板并改名。`NinePatchButtonBase` 的 `_Ready()` 会重新
	/// `GetNode("%NinePatchTexture")` / `%LabelText` 并套用 `text`/`normalTexture`，
	/// 所以复制体进树后会自己把外观补齐——不需要我们再设贴图。</summary>
	private Control CloneButton(Control template, string newName)
	{
		try
		{
			Node dup = template.Duplicate();
			Control c = dup as Control;
			if (c == null)
			{
				if (dup != null)
				{
					dup.QueueFree();
				}
				return null;
			}
			c.Name = newName;
			c.MouseFilter = Control.MouseFilterEnum.Stop;      // 必须吃点击
			c.ProcessMode = Node.ProcessModeEnum.Always;       // 暂停时也能点（与游戏自带一致）
			c.Visible = true;
			return c;
		}
		catch (Exception ex)
		{
			Warn("复制按钮模板失败：" + ex.Message);
			return null;
		}
	}

	/// <summary>
	/// 给复制体挂点击回调。
	///
	/// ★★★ 2026-10-01 实机血泪：原来走反射 `EventInfo.EventHandlerType` +
	///   `Delegate.CreateDelegate(...)` + `AddEventHandler(...)`，实机直接抛
	///   **`Method not found: 'System.Type System.Reflection.EventInfo.get_EventHandlerType()'`**
	///   —— 游戏那份 .NET 运行时里这个方法**取不到**（被裁剪/不可用），
	///   于是整个 `BuildButtons` 抛异常 → 按钮压根没建出来（日志里那条"创建按钮异常"就是它）。
	///
	/// ⇒ 正解：**不要反射**。csproj 已经 `Reference` 了游戏程序集，
	///   `NinePatchButtonBase` 是 public 类型、`OnPressed` 是 public event，
	///   **直接在编译期订阅**即可（最稳，还省掉一整套反射）。
	///   `NinePatchButtonBase : MarginContainer`，所以从 `Control` 直接强转。
	/// </summary>
	private void WirePressed(Control button, Action handler)
	{
		try
		{
			NinePatchButtonBase nb = button as NinePatchButtonBase;
			if (nb != null)
			{
				nb.OnPressed += () => handler();
				return;
			}
			// 兜底：万一拿到的不是 NinePatchButtonBase，就找内部 TextureButton 挂 Godot 信号
			TextureButton tb = FindNodeByClassName(button, "TextureButton") as TextureButton;
			if (tb != null)
			{
				tb.Pressed += () => handler();
				return;
			}
			Warn("按钮类型 " + button.GetType().Name
				+ " 既不是 NinePatchButtonBase，也找不到内部 TextureButton，点击无效。");
		}
		catch (Exception ex)
		{
			Warn("挂 OnPressed 失败：" + ex.Message);
		}
	}

	// ================================================================ 定位

	/// <summary>
	/// 摆位：**屏幕右上角，横向并排**。
	///
	/// 用户实测反馈（2026-10-01，带截图）：原来摆在右下角（竖排）虽然能用，但位置不对
	/// —— 要求移到**右上角**那一带，并且**保持横向并排**（左「商店」、右「查看图鉴」，
	/// 与截图里那两个按钮的左右关系一致）。
	///
	/// ⚠️ 不能用锚点：面板根节点 `offset_top == offset_bottom == 600`（高度 0），
	///    锚点算出来会在屏幕外。用视口尺寸直接算 `GlobalPosition` 最稳。
	/// ⚠️ 不要盖住右上角的「菜单」按钮（`GUITop/ButtonPause`，约 105×77）——
	///    右边距留 <see cref="MarginRight"/> 来避开。
	/// </summary>
	private void Layout()
	{
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree) || _tree.Root == null)
			{
				return;
			}
			Vector2 vp = _tree.Root.GetVisibleRect().Size;
			if (vp.X < 1f || vp.Y < 1f)
			{
				return;
			}
			Vector2 size = (_shop != null && GodotObject.IsInstanceValid(_shop) && _shop.Size.X > 1f)
				? _shop.Size : FallbackSize;
			float w = Mathf.Max(size.X, 80f);
			float h = Mathf.Max(size.Y, 24f);

			// 从右往左贴：最右是「查看图鉴」，它左边是「商店」
			float xAlmanac = vp.X - MarginRight - w;
			float xShop = xAlmanac - Gap - w;
			float y = MarginTop;

			if (_host != null && GodotObject.IsInstanceValid(_host))
			{
				_host.GlobalPosition = Vector2.Zero;
			}
			if (_shop != null && GodotObject.IsInstanceValid(_shop))
			{
				_shop.GlobalPosition = new Vector2(xShop, y);
				_shop.Size = new Vector2(w, h);
			}
			if (_almanac != null && GodotObject.IsInstanceValid(_almanac))
			{
				_almanac.GlobalPosition = new Vector2(xAlmanac, y);
				_almanac.Size = new Vector2(w, h);
			}
		}
		catch { }
	}

	// ================================================================ 反射工具

	/// <summary>
	/// 解析 `TowerDefenseBattleFeaturePacketBank` —— `ShopButtonPressed()` /
	/// `AlmanacButtonPressed()` 的**真正宿主**（**不是节点**，是战斗功能对象）。
	///
	/// 两条通道：
	///   ① 面板自己的 **public 字段** `packetBankFeature`
	///      （`Registry/Battle/Feature/PacketBank/PacketBank/TowerDefenseInGamePacketBank.cs:56`）；
	///   ② 兜底 `TowerDefenseManager.Instance.GetPacketBankFeature()`
	///      （`Core/TowerDefenseManager/TowerDefenseManager.cs:2330`，
	///        内部走 `currentControl.GetFeature(FeatureName_PacketBank)`）。
	///
	/// ⚠️ 两条都拿不到就返回 null；调用方必须能容忍 null（`InvokeMethod` 对 null 直接返回，
	///   会表现为"点了没反应"，所以调用点额外报了 `WarnOnceNoFeature`）。
	/// </summary>
	private static object ResolvePacketBankFeature(object panel)
	{
		try
		{
			object f = GetMember(panel, "packetBankFeature");
			if (f != null)
			{
				return f;
			}
		}
		catch { }
		try
		{
			object mgr = GetSingleton("TowerDefenseManager");
			if (mgr != null)
			{
				MethodInfo m = mgr.GetType().GetMethod("GetPacketBankFeature",
					BindingFlags.Public | BindingFlags.Instance);
				if (m != null)
				{
					return m.Invoke(mgr, null);
				}
			}
		}
		catch { }
		return null;
	}

	/// <summary>
	/// 取游戏单例（`Instance`）。
	/// ⚠️ 游戏源码里 `Instance` **写法不一致**：`TowerDefenseManager.Instance` /
	///   `Global.Instance` 是**属性**，而 `GameSaveManager.Instance` 是**字段** ⇒ 两种都试。
	/// </summary>
	private static object GetSingleton(string typeName)
	{
		try
		{
			Type t = null;
			foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				try { t = asm.GetType(typeName, throwOnError: false); } catch { }
				if (t != null)
				{
					break;
				}
			}
			if (t == null)
			{
				return null;
			}
			PropertyInfo p = t.GetProperty("Instance",
				BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
			if (p != null)
			{
				return p.GetValue(null);
			}
			FieldInfo f = t.GetField("Instance",
				BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
			if (f != null)
			{
				return f.GetValue(null);
			}
		}
		catch { }
		return null;
	}

	/// <summary>"解析不到功能对象"这条只报一次（避免每次点击刷屏）。</summary>
	private void WarnOnceNoFeature()
	{
		if (_noFeatureReported)
		{
			return;
		}
		_noFeatureReported = true;
		Warn("点不到功能对象 TowerDefenseBattleFeaturePacketBank（Shop/Almanac 回调无处可发）。"
			+ "请检查面板字段 packetBankFeature 与 TowerDefenseManager.GetPacketBankFeature。");
	}

	private static object GetMember(object target, string name)
	{
		if (target == null)
		{
			return null;
		}
		try
		{
			// 必须逐级遍历基类：GetField(NonPublic|Instance) 不查基类私有字段
			for (Type t = target.GetType(); t != null; t = t.BaseType)
			{
				FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic
					| BindingFlags.Instance | BindingFlags.DeclaredOnly);
				if (f != null)
				{
					return f.GetValue(target);
				}
			}
			for (Type t = target.GetType(); t != null; t = t.BaseType)
			{
				PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic
					| BindingFlags.Instance | BindingFlags.DeclaredOnly);
				if (p != null && p.CanRead)
				{
					return p.GetValue(target);
				}
			}
		}
		catch { }
		return null;
	}

	private static void InvokeMethod(object target, string name, params object[] args)
	{
		if (target == null)
		{
			return;
		}
		try
		{
			MethodInfo m = target.GetType().GetMethod(name,
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			m?.Invoke(target, args);
		}
		catch { }
	}

	private static Node FindNodeByClassName(Node node, string className)
	{
		if (node == null)
		{
			return null;
		}
		try
		{
			if (IsSubclassNamed(node.GetType(), className))
			{
				return node;
			}
			int n = node.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				Node r = FindNodeByClassName(node.GetChild(i), className);
				if (r != null)
				{
					return r;
				}
			}
		}
		catch { }
		return null;
	}

	/// <summary>按**节点名**找节点（读游戏自带按钮 `GUITop/ShopButton` 等时用）。</summary>
	private static Node FindNodeByName(Node node, string name)
	{
		if (node == null)
		{
			return null;
		}
		try
		{
			if (node.Name == name)
			{
				return node;
			}
			int n = node.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				Node r = FindNodeByName(node.GetChild(i), name);
				if (r != null)
				{
					return r;
				}
			}
		}
		catch { }
		return null;
	}

	private static bool IsSubclassNamed(Type t, string name)	{
		for (Type cur = t; cur != null; cur = cur.BaseType)
		{
			if (cur.Name == name)
			{
				return true;
			}
		}
		return false;
	}

	private void Info(string msg)
	{
		if (!EnableInfoLog)
		{
			return;
		}
		try { Diag(msg); } catch { }
	}

	private void Warn(string msg)
	{
		try { GD.PrintErr(LogPrefix + msg); } catch { }
	}
}
