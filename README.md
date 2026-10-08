# 图鉴商店快捷按钮（UIExtraButtons）

在**选卡界面右下角竖排**补「查看图鉴 / 商店」两个按钮。

| 项 | 值 |
|---|---|
| Mod ID | `uiextrabuttons` |
| 版本 | **1.0.3** |
| 入口类型 | `UIExtraButtonsEntry` |
| 程序集身份名 | `JTYUIExtraButtons`（包内文件名仍是 `Runtime/ModAssembly.dll`） |
| 成品 | [`dist/UIExtraButtons.pmod`](dist/UIExtraButtons.pmod) |
| 类型 | 纯托管插件（无资源覆盖、无 `provides`/`overrides`） |

> **v1.0.3 变更**（修复「点击无法跳转」）：回调原本挂在**面板节点**上，而 `ShopButtonPressed` / `AlmanacButtonPressed` 实际定义在**功能类** `TowerDefenseBattleFeaturePacketBank` 上，反射取不到方法 ⇒ **静默无反应**；现改为**每次点击先解析功能对象再调用**。
>
> **v1.0.2 变更**（依用户反馈「切换到非横版 UI 的情况下为什么还显示？」）：
> **只在横版 UI（`MobilePreset = true`）下出现**。因为游戏只在横版时才把自带那对按钮藏起来
> （`_UpdateGuiTopButtonVisibility()`：`if (mobileMode || !_packetBankEntered) → Visible=false`）；
> 切成竖版后**游戏自带的按钮自己就显示出来了**，本 Mod 若还显示就会出现**上下两排重复按钮**。
> 判据双重保险：① 读 `GameSaveManager.GetConfigValue("MobilePreset").AsBool()`；
> ② 实测 `GUITop/ShopButton`、`GUITop/AlmanacButton`（游戏自己那份）是否 `IsVisibleInTree()` ——
> 任一"说它在显示"就隐藏我这份（第 ② 条还能兜住游戏将来改判定逻辑）。
>
> **v1.0.1 变更**（依用户截图）：按钮从右下角竖排移到**右上角横向并排**（左「商店」右「查看图鉴」），
> 并新增"只在选卡阶段显示"。

---

## 1. 需求与问题根因

**需求**：横版 UI 下选卡界面里**不显示图鉴和商店按钮**，要求在选卡界面**屏幕右下角竖排**加这两个按钮。

**根因**（`Registry/Battle/Feature/PacketBank/TowerDefenseBattleFeaturePacketBank.cs`）：

```csharp
private void _UpdateGuiTopButtonVisibility(bool mobileMode)
{
    if (IsInstanceValid(_guiTopShopButton) && IsInstanceValid(_guiTopAlmanacButton))
    {
        if (mobileMode || !_packetBankEntered)      // ★ 横版（MobilePreset）= true
        {
            _guiTopShopButton.Visible = false;      // ⇒ 两个按钮被直接藏掉
            _guiTopAlmanacButton.Visible = false;
        }
        ...
```

`mobileMode` 来自 `GameSaveManager.GetConfigValue("MobilePreset").AsBool()`。
同时游戏把**另一套**（面板自带的）按钮也永久藏起来：

```csharp
private void _HidePacketBankLegacyButtons()      // packetBank/ShopButton、packetBank/AlmanacButton
{
    nodeOrNull.Visible = false;
    nodeOrNull2.Visible = false;
}
```

⇒ 横版 UI 下**两套按钮全灭**。本 Mod 不受这两处判断影响，自己补一套常驻的。

---

## 2. 做法（全部来自解包源码，不是猜的）

1. **目标面板** = `TowerDefenseInGamePacketBank`（选卡界面本体，`Control`）。
   ⚠️ 它的根节点 `offset_top == offset_bottom == 600`（**高度为 0**，内容靠 `Translate`
   子节点从 y=600 滑上来）⇒ **不能用锚点定位**，必须按视口尺寸直接算 `GlobalPosition`。
2. **外观来源 = 复制游戏自带的那两个按钮**：
   `TowerDefenseInGamePacketBank/ShopButton`（`text = "商店"`）与
   `/AlmanacButton`（`text = "查看图鉴"`）。它们自己是 `NinePatchButtonBase : MarginContainer`，
   自带九宫格贴图（`PacketBankButton.png` / `PacketBankButtonGlow.png`）、字体、字号、文字颜色。
   `Duplicate()` 之后外观**逐字段一致**，不用自己拼 UI。
   ⚠️ **不直接复用**游戏那两个节点（它们被 `_HidePacketBankLegacyButtons()` 与动画回调
   反复改 `Visible`），只取一次模板。
3. **回调** = 反射调面板脚本上已有的公开方法 `ShopButtonPressed()` / `AlmanacButtonPressed()`
   （内部 `DialogManager.Instance.DialogCreate("Shop"/"Almanac")`）。
   ⇒ 点击行为与游戏原生**完全一致**（商店还要过 `GlobalFeatureManager.IsUnlocked("Shop")`）。
4. **挂事件**：`NinePatchButtonBase.OnPressed` 是 **C# event**，用反射 `AddEventHandler` 挂；
   ⚠️ 不能 `Connect("pressed", …)`——那个 Godot 信号在内部的 `TextureButton` 上，
   挂到外层 `MarginContainer` 会直接报错（已留兜底通道）。

### 位置与顺序（v1.0.1 按用户截图调整）

```
屏幕右上角（左边是游戏自带的「菜单」）
                                    ┌──────────┐  ┌──────────────┐
                                    │  商店     │  │  查看图鉴     │
                                    └──────────┘  └──────────────┘
                                     ↑ 横向并排，与截图里的左右关系一致
```

* 距**上边缘** 26px；两个按钮间距 10px；最右那个距**右边缘** 132px
  （必须留这么宽 —— 右上角 `GUITop/ButtonPause`「菜单」约 105×77，留少了会压住它）。
* 尺寸取模板实测值（157×31）；模板还没 layout（`Size.X <= 1`）时用兜底 157×31。
* **每帧重设位置** —— 面板的入场/出场动画会把子节点搬来搬去。

### 只在「横版 UI + 选卡阶段」显示

用户实测：① 按钮在"出来选卡页面前"与"选卡完成开始游戏后"都还挂着；
② 切成**非横版（竖版）UI** 后按钮还在，和游戏自带的重复了。两个都已修。

现在按**四个判据同时成立**才显示（`ShouldShow()`）：

| # | 判据 | 依据 |
|---|---|---|
| ① | 面板自身可见 | `TowerDefenseInGamePacketBank` 是 `Control`，选卡面板的整体开关 |
| ② | `Translate` 子节点已滑入屏幕（`Position.Y &lt; 500`） | 照抄游戏 `_OnUISwitched()` 的口径；入场前/出场后它都在屏幕外（y=600） |
| ③ | **横版 UI 且游戏自带那对按钮是藏着的** | 见下 |
| ④ | `_packetBankEntered == true` | **正牌状态位**：`PacketBankAnimationPlayer` 的 Enter/MobileEnter → true、Exit/MobileExit → false |

判据 ③ 的双重保险：

1. `GameSaveManager.GetConfigValue("MobilePreset").AsBool()` —— 游戏就是拿它决定藏不藏的；
2. 实测 `GUITop/ShopButton`、`GUITop/AlmanacButton`（**游戏自己那份**）是否 `IsVisibleInTree()`。
   任一"说它在显示"就隐藏我这份；第 2 条还能兜住游戏将来改判定逻辑。

④ 通过 `TowerDefenseManager.GetPacketBankFeature()`（源码 `TowerDefenseManager.cs:2330`）拿 feature
再反射读那个 `private bool`。
⚠️ 刻意**不**从面板节点沿父链找 feature —— 面板是节点、feature 是普通对象，不在同一棵树里
（feature 侧只有 `packetBank.packetBankFeature = this;` 这一个方向的引用）。

四个判据**任意一个不成立就隐藏**，宁可多藏不要漏出来；只有"全取不到"时才回落成显示。

### 出问题时怎么一眼看出卡在哪

这段是**始终输出**的（不受日志总开关门控）：

```
[UIExtraButtons] 显示判据探针：面板可见=True Translate已滑入=True _packetBankEntered=True ⇒ 当前=显示
[UIExtraButtons] 显示状态翻转 ⇒ 隐藏（面板可见=False 已滑入=False entered=False）
```

* 有「探针」行但 `当前=隐藏` ⇒ 看是哪一项 False，就知道是哪个判据没过；
* `_packetBankEntered=<读不到>` ⇒ feature 没拿到（日志会带上原因）。

---

## 3. 硬约束遵守情况

| 约束 | 状态 |
|---|---|
| `runtimeAssembly` 必须是字面量 `Runtime/ModAssembly.dll` | ✅ |
| `runtimeApiVersion` 必须恰好 `1` | ✅ |
| `Runtime/` 下只许一个 `ModAssembly.dll` | ✅（打包护栏 1 已断言） |
| 包内不得出现 `.cs`/`.gd`/`.exe` 等可执行文件 | ✅（`verify_pmod.py` `entry.supported` 通过） |
| `<AssemblyName>` 用本 Mod 唯一名（安卓防撞车） | ✅ `JTYUIExtraButtons` |
| 入口三个回调（`Initialize`/`OnAllModsLoaded`/`Shutdown`）绝不抛 | ✅ 全部 try/catch |
| 不用自定义 Node 的 `_Process`（手写 csproj 无源码生成器 ⇒ 静默失效） | ✅ 走 `SceneTree.process_frame` 信号 |

---

## 4. 目录结构

```
UIExtraButtons/
├── mod.json                       包清单
├── build_and_install.py           编译 → 拷贝到 Runtime/ → 打包 → 装机
├── build_pmod.py                  单打包（含 resources 一致性护栏）
├── runtime_src/
│   ├── UIExtraButtons.csproj
│   ├── UIExtraButtonsEntry.cs     ★ 全部逻辑（约 17 KB）
│   └── bin/Release/JTYUIExtraButtons.dll
├── Runtime/ModAssembly.dll        包内入口程序集
└── dist/UIExtraButtons.pmod       ★ 成品
```

重新构建：

```powershell
python mods\UIExtraButtons\build_and_install.py --install
```

---

## 5. 怎么看它有没有生效（**不用猜，看两行日志**）

启动游戏后，日志文件在这里（本机实测路径）：

```
%APPDATA%\Godot\app_userdata\植物大战僵尸杂交版\logs\godot.log
```

搜 `[UIExtraButtons]`，正常应看到**两行**（这两行**始终输出**，不受日志总开关门控）：

```
[UIExtraButtons] 自检：SceneTree=True process_frame 已连接=True → 每帧通道就绪；按钮将在进入选卡界面时创建。
[UIExtraButtons] 已就位：找到选卡面板 TowerDefenseInGamePacketBank（instanceId=…），开始补右下角按钮。
```

对照判断：

| 日志现象 | 含义 |
|---|---|
| 一行都没有 | Mod 没被加载 → 查 `Mods\enabled_mods.json` 里有没有 `uiextrabuttons` |
| 只有"自检"，进过战斗选卡界面后仍没有"已就位" | 面板类名变了（或没进到选卡界面）；启动 45 秒后会再给一条提示 |
| 有"警告：选卡面板下找不到模板按钮" | 面板子节点名变了（预期是 `ShopButton` / `AlmanacButton`） |
| 两行都在，但按钮看不到 | 位置/尺寸问题 → 把 `EnableInfoLog` 打开，会打出模板实测尺寸 |

> ⚠️ 排查用的详细日志（`EnableInfoLog`）默认 **false**；需要时改成 `true` 重新构建即可，
> 诊断代码全部保留。

---

## 6. 未验证项（需要实机确认）

**v1.0.0 已由用户截图确认过的**：按钮确实出现在选卡界面、外观与游戏自带一致、位置可用。
v1.0.1 是据此把位置与显示时机改掉，下面几项请再确认一遍：

1. **位置**：横版 UI 进选卡界面，两个按钮是否在**右上角**、横向并排（左「商店」右「查看图鉴」）、
   且**不压住右上角的「菜单」按钮**；
2. **显示时机**：选卡面板滑出来之前**看不到**这两个按钮；点「一起摇滚吧」开始游戏后
   它们**消失**；
3. 点击是否分别打开**图鉴**与**商店**（这两项 v1.0.0 已通过，改版后应不受影响）；
4. 4:3 / 16:9 / 手机分辨率下位置是否都合适；
5. 竖版（非 MobilePreset）模式下游戏自带的两个按钮也在右上角 —— 是否**重叠**。
   若重叠且不希望，把 `MarginTop` 调大（让本 Mod 的按钮往下挪一行）或改 `MarginRight`。

> 判据日志见第 5 节；跑一次游戏把 `显示判据探针` 那行贴出来，就能确认三个条件各是什么状态。

> 说明：开发侧**无法**完成实机验证——headless 启动会在联网版本检查
> （`https://api.pvzhe.com/new_version`）处停住，到不了加载 Mod 的 `Loading` 场景。
> 上面第 5 节的日志判据就是为此准备的：**你一跑游戏就能自证**。

