# Seraph Civilization Patches

Vintage Story 整合包兼容补丁，模组 ID 为 `vsscpmod`，版本为 `1.0.2`，支持游戏 `1.22.3+`。客户端和服务端均需安装同一发布包，相关兼容功能随对应模组启用。

## 功能

| 范围 | 功能 |
| --- | --- |
| 制作 | RKN 制作数量、物品消耗和异常耐久修复，Toolsmith 工具柄配方兼容 |
| 搬运 | Logging 与 Carry On 树干库存保护、搬运解锁，石板负重、背包栏位和容器限制 |
| 方块 | 围栏连接变化时保留加固与锁定数据，Fields of Salt 岩石连接兼容 |
| 车船与石板 | 车船连续货位占用、石板装卸、铁链牵引、草地选中修复、光照与投影 |
| 渲染 | Toolsmith 和玩家网格恢复，Dead 存活玩家可见性兼容，披风渲染恢复及失效纹理图集防护 |
| 界面 | 创意分类滚动、Jaunt 骑乘 HUD、Komet 快捷键和中文字体兼容 |
| 技能与生理 | Seraph Leveling 与 SLSS 技能效果面板，满足兼容条件时迁移旧 BodyPhys 营养数据 |
| 物品与资产 | Billposting 张贴与取下、VintageCanvas 绘画与纹理、Envelopes 戒指预览及 Candela 火把修复 |
| 配方与显示 | 披风、蜂箱木桶、海草、硫酸铬、派、Long-term food 配方与食品兼容 |
| 启动 | Komet 与 Primitive Survival 共存兼容 |
| 信息限制 | 时间、坐标、调试信息与查询命令限制，Immersive Maps 坐标界面兼容 |

同时安装 Seraph Leveling 与 SLSS 时，按 C 可在人物界面查看“技能效果”。石板可用铁链长按捆绑，双手空手右键开始或停止牵引，Shift＋右键放置并回收材料。

## 指令

| 指令 | 权限与条件 | 用途 |
| --- | --- | --- |
| `/vsscpmod` | 玩家 | 查看服务端指令提示 |
| `/vsscpmod info status` | 管理员玩家，`controlserver` | 查看自己的信息限制状态，省略 `status` 时效果相同 |
| `/vsscpmod info off` | 管理员玩家，`controlserver` | 解除自己本次连接的信息限制，重连后恢复默认 |
| `/vsscpmod info on` | 管理员玩家，`controlserver` | 恢复自己的信息限制 |
| `/vsscpmod info reload` | 管理员玩家，`controlserver` | 重载信息限制配置并同步在线玩家 |
| `/vsscpmod fixstack` | 玩家，启用 RKN 兼容 | 清除主手物品的 RKN 异常耐久属性 |
| `/vsscpmod trunkrelease` | 玩家，启用 Logging 搬运兼容 | 将手中搬运的树干放到身旁并解除双手锁定 |
| `.vsscpmod inventory start` | 客户端，已进入世界 | 开启 30 秒库存鼠标交互诊断 |
| `.vsscpmod inventory stop` | 客户端 | 停止库存诊断，日志标记为 `[vsscpmodinvdiag]` |

## 配置

配置保存在服务端游戏数据目录的 `ModConfig` 中，启动时自动生成。

### `vsscpmod-privacy.json`

修改后使用 `/vsscpmod info reload` 生效。

| 字段 | 默认值 | 作用 |
| --- | --- | --- |
| `InformationRestrictionsEnabled` | `true` | 信息限制总开关 |
| `HideTime` | `true` | 隐藏精确时间 |
| `HideCoordinates` | `true` | 隐藏坐标 |
| `HideDebugInfo` | `true` | 隐藏相关调试信息 |
| `BlockInformationCommands` | `true` | 按对应信息限制拦截查询命令 |
| `HideImmersiveMapsCoordinateUi` | `true` | 隐藏 Immersive Maps 坐标界面 |
| `RedactCoordinateNotifications` | `true` | 隐去通知中的坐标 |

### `vsscpmod-quarry-carry.json`

启用 Stone Quarry 兼容时生成，修改后重启服务端生效。数量按石板实际包含的岩石块数计算。

| 字段 | 默认值 | 作用 |
| --- | --- | --- |
| `Enabled` | `true` | 石板随身负重限制开关 |
| `SlowdownStartsAtBlocks` | `32` | 超过此总块数后逐渐减速 |
| `ImmobileAtBlocks` | `224` | 停止移动阈值，与 `MaximumCarryBlocks` 中较小值生效 |
| `BackpackSlotAtBlocks` | `32` | 单块石板达到此块数时须放入背包栏位 |
| `MaximumCarryBlocks` | `64` | 随身总块数达到此值时停止移动 |
| `MaximumCarriedSlabs` | `1` | 随身石板数量上限，超过后停止移动 |
| `RestrictedActionsAtBlocks` | `16` | 达到后最多半速，限制冲刺、陆地跳跃、滑翔及登车骑乘 |

## 数据文件

以下路径相对于各端游戏数据目录。

| 路径 | 内容 |
| --- | --- |
| `ModData/vsscpmod-bodyphys-backups/` | 服务端旧 BodyPhys 数据迁移备份及迁移记录 |
| `Logs/client-main.log`、`Logs/client-debug.log` | 客户端渲染、兼容及库存诊断日志 |

## 版权

Copyright (c) 2026 HansJack。采用 MIT 许可证，完整条款见 LICENSE。
