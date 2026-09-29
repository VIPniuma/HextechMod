using System;
using System.Collections.Generic;

namespace PeakModder.HextechMod;

internal static class Localization
{
    public const string Chinese = "简体中文";
    public const string English = "English";

    public static bool IsEnglish => ModConfig.Language != null
        && string.Equals(ModConfig.Language.Value, English, StringComparison.Ordinal);

    private static readonly Dictionary<string, string> EnglishText = new(StringComparer.Ordinal)
    {
        ["设置"] = "Settings", ["语言"] = "Language",
        ["选择模组界面使用的语言。"] = "Choose the language used by the mod interface.",
        ["常规"] = "General", ["技能"] = "Skills", ["界面"] = "Interface",
        ["商店"] = "Shop", ["游戏玩法"] = "Gameplay", ["行李箱"] = "Luggage",
        ["防挂机"] = "AFK Protection", ["修复"] = "Fixes", ["海克斯"] = "Hextech",
        ["更新"] = "Updates", ["日志上报"] = "Bug Reports", ["商店调价"] = "Shop Prices",
        ["物资调价"] = "Item Prices", ["启用模组"] = "Enable Mod",
        ["释放技能"] = "Use Skill", ["切换技能"] = "Cycle Skill", ["复活队友"] = "Revive Teammate",
        ["打开海克斯页面"] = "Open Hextech Codex", ["禁用海克斯"] = "Ban Hextech",
        ["打开海克斯设置"] = "Open Hextech Settings", ["上传日志"] = "Upload Logs",
        ["重新打开三选一"] = "Reopen Choice", ["打开商店"] = "Open Shop",
        ["启用商店"] = "Enable Shop", ["共享代币"] = "Shared Tokens",
        ["每分钟代币"] = "Tokens Per Minute", ["物价倍率"] = "Price Multiplier",
        ["功能溢价"] = "Utility Premium", ["正面海克斯概率"] = "Positive Hextech Chance",
        ["负面海克斯概率"] = "Negative Hextech Chance", ["出代币概率"] = "Token Chance",
        ["代币上限"] = "Token Maximum", ["篝火内不积累代币"] = "Pause Tokens Near Campfire",
        ["篝火范围（米）"] = "Campfire Radius (m)", ["扛人兜底"] = "Carry Safety Fix",
        ["传说词条权重"] = "Legendary Weight", ["更新源地址"] = "Update Feed URL",
        ["已忽略版本"] = "Ignored Version", ["海克斯设置"] = "Hextech Settings",
        ["开"] = "On", ["关"] = "Off", ["未绑定"] = "Unbound", ["按键中…"] = "Press a key...",
        ["主动技能"] = "Active Skill", ["青铜"] = "Bronze", ["白银"] = "Silver",
        ["黄金"] = "Gold", ["传说"] = "Legendary", ["默认价"] = "Default price",
        ["已售罄"] = "Sold out", ["购买"] = "Buy", ["取消"] = "Cancel", ["确定"] = "OK",
        ["复制"] = "Copy", ["已复制"] = "Copied", ["删除"] = "Delete", ["确认"] = "Confirm",
        ["保存"] = "Save", ["返回"] = "Back", ["刷新"] = "Reroll",
        ["效果："] = "Effect: ", ["代价："] = "Drawback: ", ["尚未选择技能"] = "No skill selected",
        ["这一类暂时没有商品"] = "No items are available in this category",
        ["发现新版本"] = "New version available",
        ["发现新版本（建议更新）"] = "New version available (recommended)",
        ["正在下载新版本…"] = "Downloading the new version...",
        ["下载完成，正在准备替换…"] = "Download complete. Preparing the update...",
        ["已取消更新，可以继续游戏"] = "Update cancelled. You can continue playing.",
        ["本局持有"] = "Owned This Run", ["海克斯商店"] = "Hextech Shop",
        ["海克斯页面"] = "Hextech Codex", ["数值恢复默认"] = "Reset Numeric Values",
        ["恢复默认"] = "Reset", ["重置全部调价"] = "Reset All Prices",
        ["保存为预设"] = "Save as Preset", ["选择预设"] = "Choose Preset",
        ["海克斯漂浮"] = "Hextech Float", ["疾风步"] = "Windstep", ["净化"] = "Cleanse",
        ["治疗波"] = "Healing Wave", ["肾上腺素"] = "Adrenaline", ["无敌"] = "Invincibility",
        ["机械手"] = "Mechanical Hand", ["领头羊"] = "Trailblazer", ["拉拉手"] = "Helping Hand",
        ["厨师"] = "Chef", ["背包客"] = "Backpacker", ["我还有光"] = "Light Within",
        ["烫手"] = "Hot Hands", ["士气大提升"] = "Morale Boost", ["人人为我"] = "All for Me",
        ["讲价高手"] = "Master Haggler", ["百毒不侵"] = "Toxinproof", ["围炉谈心"] = "Campfire Talk",
        ["收藏家"] = "Collector", ["资本家"] = "Capitalist", ["抬轿人"] = "Bearer",
        ["送葬人"] = "Undertaker", ["骨质疏松"] = "Osteoporosis", ["羽落"] = "Feather Fall",
        ["二段跳"] = "Double Jump", ["弹簧腿"] = "Spring Legs", ["空灵之体"] = "Ethereal Body",
        ["回光返照"] = "Last Stand", ["蜘蛛侠"] = "Wall Crawler", ["石化抗性"] = "Petrification Resistance",
        ["赌徒"] = "Gambler", ["不死鸟"] = "Phoenix", ["皮糙肉厚"] = "Thick Skin",
        ["抗毒体质"] = "Poison Resistance", ["隔热层"] = "Insulation", ["破咒者"] = "Cursebreaker",
        ["沉重之躯"] = "Heavy Body", ["腿软"] = "Weak Knees", ["迟钝"] = "Sluggish",
        ["大胃王"] = "Big Eater", ["冷血体质"] = "Cold Blooded", ["玻璃体质"] = "Glass Constitution",
        ["僵直"] = "Stiff", ["病去如抽丝"] = "Lingering Illness", ["睡不醒"] = "Drowsy",
        ["高烧不退"] = "Persistent Fever", ["手残"] = "Clumsy Hands", ["倒吊人"] = "The Hanged Man",
        ["强健双腿"] = "Strong Legs", ["疾跑者"] = "Sprinter", ["轻装疾行"] = "Lightfoot",
        ["节能食客"] = "Efficient Eater", ["拾荒者"] = "Scavenger", ["攀爬健将"] = "Expert Climber",
        ["铁胃"] = "Iron Stomach", ["耐力储备"] = "Stamina Reserve", ["月面重力"] = "Lunar Gravity",
        ["自我修复"] = "Self Repair", ["温暖之躯"] = "Warm Body", ["饱腹"] = "Satiated",
        ["精致胃袋"] = "Refined Palate", ["防晒霜"] = "Sunscreen", ["能量饮料"] = "Energy Drink",
        ["甜食冲刺"] = "Sugar Rush", ["守护者护盾"] = "Guardian Shield", ["全能治疗"] = "Full Recovery",
        ["极限攀爬"] = "Extreme Climbing", ["人猿泰山"] = "Vine Master", ["坚韧之躯"] = "Resilient Body",
        ["幸运旅客"] = "Lucky Traveler", ["硬皮"] = "Hard Skin", ["清醒"] = "Clear Mind",
        ["温室体质"] = "Warm-Blooded", ["投掷专家"] = "Throwing Expert", ["长跑运动员"] = "Marathon Runner",
        ["不倒翁"] = "Tumbler", ["吐纳"] = "Breathing Technique", ["死而复生"] = "Resurrection",
        ["手滑"] = "Slippery Hands", ["夜盲"] = "Night Blindness", ["拖后腿"] = "Dead Weight",
        ["海克斯：漂浮"] = "Hextech: Float", ["海克斯：疾风步"] = "Hextech: Windstep",
        ["海克斯：净化"] = "Hextech: Cleanse", ["海克斯：治疗波"] = "Hextech: Healing Wave",
        ["海克斯：肾上腺素"] = "Hextech: Adrenaline", ["海克斯：无敌"] = "Hextech: Invincibility",
        ["海克斯：机械手"] = "Hextech: Mechanical Hand",
        ["石化转化的代价是体温：夜间寒冷积累速度 +20%。"] = "The conversion costs body heat: night cold buildup +20%.",
        ["篝火狂欢之后的疲软：饥饿积累速度 +15%。"] = "After the campfire rush, hunger buildup +15%.",
        ["身体跟着僵化：地面移动速度 -10%。"] = "Your body stiffens: ground movement speed -10%.",
        ["落地太轻，蹬地也使不上劲：地面移动速度 -8%。"] = "Your light footing reduces ground movement speed by 8%.",
        ["空中全靠手臂硬拉：攀爬耐力消耗 +12%。"] = "Climbing stamina cost +12%.",
        ["起跳那一下会抽走冲刺的力气：冲刺耐力消耗 +15%。"] = "Sprint stamina cost +15%.",
        ["身体太轻，蹬地也没劲：跳跃高度 -8%。"] = "Your light body reduces jump height by 8%.",
        ["烧的是自己的储备：饥饿积累速度 +20%。"] = "Hunger buildup +20%.",
        ["技能同样吃体力：饥饿积累速度 +25%。"] = "Hunger buildup +25%.",
        ["手脚都黏在墙上，走路反而不利索：地面移动速度 -12%。"] = "Ground movement speed -12%.",

        // ── 商店分类 / 调价页表头 ─────────────────────────────────
        ["普通物资"] = "Common Items", ["精良物资"] = "Uncommon Items",
        ["稀有物资"] = "Rare Items", ["传说物资"] = "Legendary Items",
        ["商店调价"] = "Shop Prices",
        ["商店调价 · 商店已关闭"] = "Shop Prices · shop is closed",
        ["商店调价 · 联机时只有房主能改"] = "Shop Prices · only the host can edit in multiplayer",
        ["已恢复默认价"] = "price reset to default",
        ["：已恢复默认价"] = ": price reset to default",

        // ── 稀有度 / 商店卡片描述 / 调价页 ────────────────────────
        ["普通"] = "Common", ["精良"] = "Uncommon", ["稀有"] = "Rare",
        ["史诗"] = "Epic", ["传奇"] = "Legendary", ["神话"] = "Mythic",
        ["荒诞"] = "Ridiculously Rare",
        ["购买后直接进你的物品栏（满了才会掉在面前）。"] =
            "Drops straight into your inventory (it lands at your feet if the inventory is full).",
        ["一座石化童军雕像，照买家形象定制，生成在你面前（联机由房主代为生成）。只是个撞狠了会碎的物理物件 —— 它不能复活死人。"] =
            "A petrified Scout statue styled after the buyer, spawns in front of you (the host spawns it in multiplayer). Just a breakable prop — it cannot revive anyone.",
        ["随机 1 项正面强化，以青铜为主，小概率直接开出白银或黄金。整局限购 1 次。"] =
            "One random positive Hextech, mostly Bronze with a small chance of Silver or Gold. Once per run.",
        ["保底白银，有一定概率直接开出黄金。整局限购 1 次。"] =
            "Guaranteed Silver, with a chance of Gold. Once per run.",
        ["必定开出 1 项黄金强化。整局限购 1 次。"] = "Always grants one Gold Hextech. Once per run.",
        ["一次开出 3 项互不重复的正面强化，比单抽划算。整局限购 1 次。"] =
            "Three different positive Hextechs at once — better value than single draws. Once per run.",
        ["上岛之后再买"] = "Buy it on the island",
        ["改过的价一键恢复默认（怎么改见下面的说明）"] = "Reset every custom price (see the rows below for how editing works)",
        ["重置全部"] = "Reset All",
        ["把现在这一版价格存起来，起个名字以后随时换回来"] = "Save the current prices under a name and restore them anytime",
        ["保存为预设"] = "Save as Preset",
        ["换一份存好的价格，或者一键回到默认配置"] = "Load a saved price set, or reset everything to defaults",
        ["选择预设 ▾"] = "Choose Preset ▾",
        ["倍率 / 概率 / 速率这些数值一键调回默认（开关与按键不动）"] = "Restore every numeric value (rates / chances / caps) to defaults. Toggles and keybinds stay.",
        ["恢复默认"] = "Reset",
        ["复制编号"] = "Copy Code",
        ["商店价格已全部恢复默认"] = "All shop prices have been reset to defaults",
        ["日志上报 · 本机上传记录（服务器只留 {0} 小时）"] = "Bug reports · your uploads (the server keeps them {0} h)",
        ["点这一行把编号复制回剪贴板，发给我就能取这份日志 · 服务器只留 {0} 小时"] =
            "Click this row to copy the code — send it to me and I can fetch the log · the server keeps it {0} h",
        ["点 − / ＋ 改这件商品的基础价（按住 Shift 一次 5 枚）· 点中间的数字恢复默认价"] =
            "Use − / ＋ to edit this item's base price (hold Shift for steps of 5) · click the number in the middle to reset it",
        ["把上面那些数值项（倍率 / 概率 / 速率 / 上限）恢复成默认值 · 开关与按键不受影响"] =
            "Restore the numeric values above (rates / chances / caps) to defaults · toggles and keybinds are not affected",

        // ── v0.3.66 补：Toast / 提示 / 插值模板（之前英文模式漏翻）──────
        ["海克斯模组已启用"] = "Hextech Mod enabled",
        ["已抵达海滩 · 送你一个海克斯强化"] = "You reached the beach · here's a Hextech boost",
        ["现在没有待选的海克斯强化"] = "No Hextech choice is pending right now",
        ["商店已关闭（在海克斯设置里打开「启用商店」）"] = "Shop is closed (enable it in Hextech Settings)",
        ["只能回到机场再禁用词条"] = "You can only disable Hextechs back at the airport",
        ["日志还在上传中，稍等一下"] = "The log is still uploading, please wait a moment",
        ["还没有海克斯技能 · 点燃阶段篝火才能抽"] = "No Hextech skill yet · light a campfire in the kindling phase to draw",
        ["商店已关闭，调价不可用"] = "Shop is closed, pricing is unavailable",
        ["共享代币只能在机场设置"] = "Shared tokens can only be set at the airport",
        ["这条记录已经不在了，重新打开面板看看"] = "This record is gone — reopen the panel to refresh",
        ["商店已关闭，无法调价"] = "Shop is closed, can't adjust prices",
        ["联机时只有房主能调整商店价格"] = "Only the host can adjust shop prices in multiplayer",
        ["这张已经刷新过了"] = "This one is already rerolled",
        ["已经没有别的强化可以换了"] = "There are no other Hextechs to swap to",
        ["雕像暂时生成不了"] = "The statue can't be spawned right now",
        ["不死鸟：这一摔没能把你带走"] = "Phoenix: that fall didn't take you out",
        ["已切回默认配置：调价清零，倍率等数值回默认"] = "Reverted to defaults: prices cleared, rates and values reset",
        ["「死而复生」这一局已经用掉了"] = "Resurrection is already used this run",
        ["价格预设"] = "Price Presets",
        ["更新内容"] = "Update Notes",
        ["暂时不可用"] = "Temporarily unavailable",
        ["海克斯 · 本局持有"] = "Hextech · Owned This Run",
        ["确认"] = "Confirm",

        // 插值模板（配合 T(key, args) 使用）
        ["死而复生：获得 1 次复活机会 · 按 {0} 复活队友"] = "Resurrection: gained 1 revive charge · press {0} to revive a teammate",
        ["已更新预设「{0}」"] = "Updated preset \"{0}\"",
        ["已保存为预设「{0}」"] = "Saved as preset \"{0}\"",
        ["已应用预设「{0}」"] = "Applied preset \"{0}\"",
        ["已删除预设「{0}」"] = "Deleted preset \"{0}\"",
        ["「{0}」不是数字，这项没改"] = "\"{0}\" is not a number, this setting was not changed",
        ["编号 {0} 已复制 · 发给作者就能取这份报告"] = "Code {0} copied · send it to the author to fetch this report",
        ["编号 {0} 已复制 · 尽快发给我（服务器只留 {1} 小时）"] = "Code {0} copied · send it soon (the server keeps it {1} h)",
        ["拾荒者：+{0:0.0} 商店代币（本局 {1:0.0} 枚）"] = "Scavenger: +{0:0.0} shop tokens (this run {1:0.0})",
        ["商店代币 +{0}（共 {1} 枚）· 按 {2} 打开商店"] = "Shop tokens +{0} (total {1}) · press {2} to open the shop",
        ["死而复生：把 {0} 拉到了你面前（剩余 {1} 次）"] = "Resurrection: pulled {0} to you ({1} charges left)",
        ["礼包开出：{0}"] = "Crate opened: {0}",
        ["机械手：交互距离复原 · 冷却 {0:0} 秒"] = "Mechanical Hand: interaction range restored · cooldown {0:0}s",
        ["中途加入 · 补足 {0}"] = "Joined mid-run · caught up with {0}",
        ["已恢复掉线前的海克斯与代币"] = "restored your Hextechs and tokens from before the disconnect",
        ["代价"] = "Cost",
        ["代价缠身：{0} · {1}"] = "Cursed: {0} · {1}",
        ["登岛诅咒 · 抽一项代价"] = "Landing curse · drawing a cost",
        ["篝火诅咒 · 重新抽一项代价"] = "Campfire curse · redrawing a cost",
        ["黄毛追求者：蘑菇僵尸找上你了"] = "Blonde Admirer: a mushroom zombie is after you",
        ["你跑不过我你信不信？"] = "Try Outrunning Me, I Dare You",
        ["你跑不过我你信不信？：{0} 只蘑菇僵尸与童子军领队找上了你"] = "Try Outrunning Me, I Dare You: {0} mushroom zombies and a Scoutmaster are after you",
        ["你跑不过我你信不信？：这个场景刷不出僵尸，只来了童子军领队"] =
            "Try Outrunning Me, I Dare You: no zombies could spawn here, only the Scoutmaster showed up",
        ["海克斯还没选 · 已给你留着（还欠 {0} 次）· {1}"] = "Hextech not chosen yet · kept for you ({0} draws still owed) · {1}",
        ["日志上传失败：{0}"] = "Log upload failed: {0}",
        ["获得强化：{0} · {1}"] = "Gained Hextech: {0} · {1}",
        ["当前 v{0} → 最新 v{1}"] = "current v{0} → latest v{1}",
        ["v{0} 已就绪 · 游戏即将退出并自动重启；若没有自动重开，手动启动即可（更新已完成）"] =
            "v{0} is ready · the game will close and restart itself; if it doesn't reopen, just start it manually (the update is done)",
        ["已忽略 v{0} · 想更新的话清掉配置里的「更新.已忽略版本」"] = "Ignored v{0} · to update later, clear \"Updates.Ignored Version\" in the config",
        ["滚轮浏览全部词条 · 点击一行禁用 / 取消 · 按 {0} 关闭"] = "Scroll to browse all Hextechs · click a row to ban / unban · press {0} to close",
        ["机场里可以按 {0} 禁用本局不想看到的词条"] = "At the airport you can press {0} to ban Hextechs you don't want this run",
        ["（这份预设里没有改过单件价）"] = "(no single-item prices were changed in this preset)",
        [" · 联机时价格以房主为准"] = " · prices follow the host in multiplayer",
        ["面板上的提示里写着重新打开的按键"] = "the hint on the panel shows the reopen key",
        ["按 {0} 重新打开"] = "press {0} to reopen",
        ["暂时没有可以给的强化了"] = "no Hextechs left to give",
        ["{0} 或 ESC"] = "{0} or ESC",
        ["新版 dll 写不进去（目录没有写权限？）：{0}"] = "Could not write the new DLL (no write permission for the folder?): {0}",
        ["更新脚本写不进去：{0}"] = "Could not write the update script: {0}",
        ["请求超时（超过 {0:0} 秒没回应）"] = "Request timed out (no response after {0:0} seconds)",
        ["日志已上传"] = "Log Uploaded",
    };

    private static readonly Dictionary<string, string> EnglishHextechDescriptions = new(StringComparer.Ordinal)
    {
        ["天之弃子"] = "Adds 50 petrification and 10% curse the moment you draw it.",
        ["你跑不过我你信不信？"] = "Move speed +20%. Three seconds after you draw it, 5 mushroom zombies and a Scoutmaster spawn right next to you.",
        ["急性铁中毒"] = "An arrow is stuck in your head; pull it out and another goes straight back in.",
        ["黄毛追求者"] = "Stay more than 50 m from every teammate for 2 minutes and a mushroom zombie comes for you "
                       + "(only one at a time; another can spawn after that one dies).",
        ["纯氧"] = "Stamina cost -50%, but +1% poison and +1% drowsiness every 60 seconds (these two no longer wear off; "
                  + "campfires and lanterns clear drowsiness, playing dead clears it fast but adds +2 cold per second). "
                  + "While you have it, the three-card choice becomes two cards with costs mixed into the pool.",
        ["强健双腿"] = "Jump height +{0}.",
        ["疾跑者"] = "Sprint speed +{0}; sprint stamina cost -{0}.",
        ["轻装疾行"] = "Ground movement speed +{0}.",
        ["节能食客"] = "Hunger buildup -{0}.",
        ["拾荒者"] = "Gain 0.3 shop tokens the first time you pick up each item.",
        ["攀爬健将"] = "Climbing speed +{0}.",
        ["铁胃"] = "Poison recovery speed +{0}.",
        ["耐力储备"] = "Climbing stamina cost -{0}.",
        ["月面重力"] = "Maximum falling speed -{0}, increasing air time.",
        ["自我修复"] = "Recover 1 injury point every 15 seconds.",
        ["温暖之躯"] = "Remove 1 point of cold every 5 seconds.",
        ["饱腹"] = "Remove 1 point of hunger every 60 seconds.",
        ["精致胃袋"] = "Wild food is half as effective; packaged food is twice as effective.",
        ["防晒霜"] = "Heat buildup recovery speed +{0}.",
        ["能量饮料"] = "Movement and climbing speed +{0}.",
        ["甜食冲刺"] = "Immediately gain unlimited stamina for 24 seconds.",
        ["守护者护盾"] = "Gain a protective shield for 20 seconds.",
        ["全能治疗"] = "Immediately remove up to 64 points of negative status buildup.",
        ["极限攀爬"] = "Climbing stamina cost -{0}, and keep climbing longer after exhaustion.",
        ["人猿泰山"] = "On vines, stamina cost -25% and climbing speed +30%.",
        ["坚韧之躯"] = "Negative status buildup -25%.",
        ["幸运旅客"] = "Your luggage rolls one extra reward per stack.",
        ["硬皮"] = "Spore and thorn recovery speed +{0}.",
        ["清醒"] = "Remove a small amount of drowsiness every 2 seconds.",
        ["温室体质"] = "Night cold buildup -{0}.",
        ["投掷专家"] = "Throw force is greatly increased.",
        ["长跑运动员"] = "Sprint stamina cost is greatly reduced.",
        ["不倒翁"] = "Recover faster after falling or being launched.",
        ["吐纳"] = "Recover 1% stamina per second per stack while not climbing.",
        ["羽落"] = "Fall injuries -60%.",
        ["二段跳"] = "Gain one extra jump in midair.",
        ["弹簧腿"] = "Jump height +{0}, plus one extra midair jump.",
        ["空灵之体"] = "Permanently enter a low-gravity state.",
        ["回光返照"] = "Become immune to all damage and negative statuses for 12 seconds.",
        ["蜘蛛侠"] = "Do not consume stamina while hanging still from ledges, rope ladders, or vines.",
        ["石化抗性"] = "Petrification buildup -33%.",
        ["赌徒"] = "Every three-choice Hextech draw becomes a four-choice draw.",
        ["不死鸟"] = "Fall damage cannot fully knock you out; it leaves at least 20 injury points.",
        ["皮糙肉厚"] = "Injury buildup -25%.",
        ["抗毒体质"] = "Poison buildup -25%.",
        ["隔热层"] = "Heat buildup -20%.",
        ["破咒者"] = "Curse buildup -20%.",
        ["沉重之躯"] = "Ground movement speed -10%.",
        ["腿软"] = "Jump height -12%.",
        ["迟钝"] = "Sprint speed -15%; sprint stamina cost +25%.",
        ["大胃王"] = "Hunger buildup +25%.",
        ["冷血体质"] = "Night cold buildup +20%; heat buildup -50%.",
        ["玻璃体质"] = "Negative status buildup +25%.",
        ["僵直"] = "Turning speed -25%.",
        ["病去如抽丝"] = "Poison, spore, and thorn recovery speed -25%.",
        ["睡不醒"] = "Drowsiness recovery speed -30%.",
        ["高烧不退"] = "Heat recovery speed -30%.",
        ["手残"] = "Helping-hand interaction distance -25%.",
        ["倒吊人"] = "Immediately clear every negative status, but fill most of your injury meter.",
        ["领头羊"] = "Teammates below your height consume 12% less stamina.",
        ["拉拉手"] = "Helping-hand range +50%; each successful assist restores 20 stamina.",
        ["厨师"] = "Food you cook gains one random bonus: stamina, healing, shield, speed, or climbing efficiency.",
        ["背包客"] = "Funny backpacks you carry can hold 2 extra items.",
        ["我还有光"] = "Petrification above half capacity is converted into bonus stamina, then normal stamina.",
        ["烫手"] = "The first time you pick up a raw food item, it is cooked instantly.",
        ["士气大提升"] = "Leaving a lit campfire grants speed and unlimited stamina for 16 seconds.",
        ["人人为我"] = "When a teammate dies, gain 60 bonus stamina.",
        ["讲价高手"] = "All Hextech Shop prices are halved.",
        ["百毒不侵"] = "Spores can no longer turn you into a zombie.",
        ["围炉谈心"] = "While close to a teammate, recover 1 injury point every 10 seconds.",
        ["收藏家"] = "Shop token generation speed +50%.",
        ["资本家"] = "Gain 0.5 extra shop tokens per minute per stack.",
        ["抬轿人"] = "Movement speed +{0} while carrying a downed teammate.",
        ["送葬人"] = "When a teammate is downed within 10 m, restore 20 stamina and gain 4 seconds of speed.",
        ["骨质疏松"] = "Become a skeleton, gaining immunity to most negative status buildup and curses.",
        ["死而复生"] = "Gain one chance per run to revive a dead teammate at your position.",
        ["手滑"] = "Throw force -30%.",
        ["夜盲"] = "Become blind for 4 seconds every 90 seconds; each stack halves the interval.",
        ["拖后腿"] = "Teammates within 6 m consume 12% more stamina per stack.",
        ["海克斯：漂浮"] = "Unlock Hextech Float: enter low gravity and float toward your view direction at the cost of hunger.",
        ["海克斯：疾风步"] = "Unlock Windstep: temporarily run and climb without consuming stamina, at the cost of hunger.",
        ["海克斯：净化"] = "Unlock Cleanse: remove part of every negative status and convert the removed amount into hunger.",
        ["海克斯：治疗波"] = "Unlock Healing Wave: restore injury points at the cost of petrification.",
        ["海克斯：肾上腺素"] = "Unlock Adrenaline: temporarily boost movement and climbing speed and prevent drowsiness, at the cost of hunger.",
        ["海克斯：无敌"] = "Unlock Invincibility: temporarily ignore damage and negative statuses at the cost of petrification.",
        ["海克斯：机械手"] = "Unlock Mechanical Hand: extend interaction range until the next successful interaction, at the cost of petrification.",
    };

    private static readonly Dictionary<string, string> EnglishDescriptions = new(StringComparer.Ordinal)
    {
        ["语言"] = "Choose the language used by the mod interface.",
        ["启用模组"] = "Enable or disable Hextech Mod.",
        ["释放技能"] = "Key used to cast the currently selected Hextech skill.",
        ["切换技能"] = "Key used to cycle through unlocked Hextech skills.",
        ["复活队友"] = "Key used by the resurrection Hextech to revive a teammate.",
        ["打开海克斯页面"] = "Hold this key to view your skills and Hextechs for the current run.",
        ["禁用海克斯"] = "Open the Hextech ban panel at the airport.",
        ["打开海克斯设置"] = "Open this settings panel at any time.",
        ["上传日志"] = "Upload a diagnostic report when reporting a bug.",
        ["重新打开三选一"] = "Reopen any pending Hextech choice.",
        ["打开商店"] = "Open the Hextech shop.",
        ["启用商店"] = "Disable this to turn off the shop and token generation.",
        ["共享代币"] = "Share one token pool across the lobby. This can only be changed at the airport.",
        ["每分钟代币"] = "Number of shop tokens earned per minute during a run. Set to 0 to disable generation.",
        ["物价倍率"] = "Multiplier applied to every shop price.",
        ["功能溢价"] = "Strength of utility-based price adjustments. 0 disables them; 1 is the default.",
        ["正面海克斯概率"] = "Chance for each luggage slot to award a positive Hextech.",
        ["负面海克斯概率"] = "Chance for each luggage slot to award a negative Hextech.",
        ["出代币概率"] = "Chance for each luggage slot to award shop tokens.",
        ["代币上限"] = "Maximum number of tokens awarded by one luggage roll.",
        ["篝火内不积累代币"] = "Pause local token generation while standing near a lit campfire.",
        ["篝火范围（米）"] = "Distance from a lit campfire that counts as being inside the safe area.",
        ["扛人兜底"] = "Automatically put down a carried teammate after they recover or die.",
        ["传说词条权重"] = "Draw weight for Legendary Hextechs. Set to 0 to disable them.",
        ["更新源地址"] = "Address of version.json. Leave empty to use the built-in update feed.",
        ["已忽略版本"] = "A remote version that should no longer trigger an update prompt.",
    };

    public static string T(string? text)
    {
        if (string.IsNullOrEmpty(text) || !IsEnglish)
        {
            return text ?? string.Empty;
        }

        return EnglishText.TryGetValue(text, out var translated) ? translated : text;
    }

    /// <summary>带占位符的翻译：先按 key 取英文模板，再用 string.Format 填参数。</summary>
    public static string T(string? key, params object[] args)
    {
        return string.Format(T(key), args);
    }

    public static string ConfigDescription(string key, string fallback)
    {
        if (!IsEnglish)
        {
            return fallback;
        }

        return EnglishDescriptions.TryGetValue(key, out var translated) ? translated : fallback;
    }

    public static string HextechDescription(string title, string fallback)
    {
        if (!IsEnglish || !EnglishHextechDescriptions.TryGetValue(title, out var translated))
        {
            return fallback;
        }

        return translated;
    }

    public static string SkillDescription(string title, string fallback)
    {
        if (!IsEnglish)
        {
            return fallback;
        }

        return title switch
        {
            "海克斯漂浮" => $"Enter low gravity and float toward your view direction; costs {SkillRegistry.SuperJumpHungerCost:0} hunger.",
            "疾风步" => $"Unlimited stamina while running and climbing for {SkillRegistry.SprintDurationSeconds:0} seconds; costs hunger after use.",
            "净化" => $"Remove {SkillRegistry.CleansePercentPerStatus * 100f:0.#}% of each negative status ({SkillRegistry.CleanseColdPercent * 100f:0.#}% for cold), converting the removed amount into hunger.",
            "治疗波" => $"Recover {SkillRegistry.HealInjuryPoints:0} injury points; costs {SkillRegistry.HealPetrifyCostPoints:0} petrification.",
            "肾上腺素" => $"Greatly increase movement and climbing speed for {SkillRegistry.AdrenalineDurationSeconds:0} seconds and prevent drowsiness; costs hunger.",
            "无敌" => $"Become immune to all damage and negative statuses for {SkillRegistry.InvincibleDurationSeconds:0} seconds; costs {SkillRegistry.InvinciblePetrifyCost:0} petrification.",
            "机械手" => $"Increase interaction range by {HextechAdvancedPatches.MechanicalHand.ExtraDistance:0} m until one successful interaction; costs petrification.",
            _ => fallback,
        };
    }
}
