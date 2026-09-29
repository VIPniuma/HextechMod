# HextechMod · 海克斯模组

给 **PEAK** 做的 BepInEx 模组：每局随机抽取「海克斯」强化（青铜 / 白银 / 黄金 / 传说四档品质，
最多叠 3 层），带三选一面板、图鉴、代币商店、行李箱抽奖、代价与诅咒机制，自带云更新与一键安装器。

仓库里有三个工程：

| 工程 | 产物 | 说明 |
|---|---|---|
| `src/HextechMod` | `PeakModder.HextechMod.dll` | 模组本体（netstandard2.1 + BepInEx 5.4.21 + HarmonyX） |
| `src/HextechMod.Installer` | `HextechModInstaller.exe` | 模组安装器（net48 WPF，自动装 BepInEx 和模组） |
| `src/HextechMod.Configurator` | `HextechModConfigurator.exe` | 平衡配置器（开发者工具，玩家不可见） |

## 快速开始

```powershell
# 编译模组
dotnet build src\HextechMod\HextechMod.csproj -c Release

# 编译后直接拷进游戏的 BepInEx\plugins（需要已装 BepInEx）
dotnet build src\HextechMod\HextechMod.csproj -c Release -p:DeployModFiles=true

# 编译安装器
dotnet build src\HextechMod.Installer\HextechMod.Installer.csproj -c Release
```

产物统一输出到 `artifacts\`。没装游戏也能编译 —— 构建会从 `lib\` 里取游戏程序集。

**完整说明（目录结构 / 怎么改数值和词条 / 怎么编译打包 / 需要什么工具）见 [`使用教程.md`](使用教程.md)。**

## 隐私说明

源码里**不含任何服务器地址、账号或密钥**：所有地址都是占位符。
真实的更新源地址只写在本地 `Config.Build.user.props`（该文件已被 `.gitignore` 排除）。

## 许可与素材

- 代码：见 [`LICENSE`](LICENSE)
- 技能图标来自 [game-icons.net](https://game-icons.net)（CC BY 3.0），来源见 `src/HextechMod/Assets/Icons/SOURCE.txt`
- 第三方库：BepInEx、HarmonyX、MahApps.Metro（MIT）、ControlzEx、Costura.Fody、PolySharp
- `lib\` 里的 dll 是 PEAK 游戏本体的程序集，仅作编译参考，不随模组分发

本模组为玩家自制内容，与 PEAK 官方无关。
