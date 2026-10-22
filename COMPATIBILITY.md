# 兼容性与接口约定

[English](COMPATIBILITY.en.md) | [简体中文](COMPATIBILITY.md)

## 运行范围

| 项目 | 范围与限制 |
| --- | --- |
| 系统 | Windows x64，本地单人与固定P1/P2热座；其他系统未验证 |
| 游戏 | 本机核对Steam版本2.17.3.16375、Unity 6000.3.17f1；不承诺其他构建 |
| 加载器 | 用户自行安装MelonLoader 0.7.3 Open-Beta，依赖不随项目分发 |
| Python | 标准库，代码下限3.10；本机3.11.4，CI配置3.10/3.11/3.13；实际运行结果见仓库Actions |
| Steam | 官方离线模式与本地对局；在线登录启动、在线对局不列入支持 |
| 安装 | 首次生成依赖可能联网；生命周期接口加入前的源码选集已完成本机独立首装、核心单人操作、人工保存恢复和Bridge回退重装；不自动转用于后续运行时代码；其他机器、完全断网首装和完整卸载加载器未验证 |
| 原生适配 | 已核对构建按指纹使用显示规则；其他构建不猜HP显示尺度或缺失值 |

## 已有功能验证

玩家观察、动态候选、实际行动回执、普通单人终局、固定双席位热座交接/通知/保存恢复均有本机现场证据。当前版已有Cloak接近、离开、和平时排除及解除和平恢复的提示日志证据；第三方外交原生广播、遗迹科技/星星/单位/Explorer/人口奖励实际应用及升级中断续查均有现场证据。

这不意味着所有种族、技能和条件组合均已实测。Cymanti、破冰、特殊种族完整流程和遗迹产城仍在已验范围之外；未知/部分结果按字段表达。原生测试依赖用户本机游戏，公开仓库不分发反编译输出或生成程序集。

MelonInfo内的0.3.0目前为内部标记，尚无已发布tag；同版本号不表示相同DLL。实际二进制以SHA256及验证输入区分。

## 变更与错误处理

正式发行前保持草稿标记。之后兼容新增可选字段可进入同一schema修订；删除/改名、单位变化、状态含义变化或必需字段变化应更新相应schema并给出迁移说明，不静默重解释旧会话。该规则是发布约定，当前没有实现跨大版本会话迁移。

动作和候选绑定episode/player/stream/stateVersion。游戏重启、恢复或换局后不得把新票据解释成旧票据；过期候选重新观察，unknown/timeout查询原请求而不重发。原生签名/构建不匹配时返回不可用或partial，不能靠缺字段默认成功。

首次安装的回退恢复为Bridge DLL不存在，升级回退恢复原DLL；都不改变存档、受控配置或其他mod。部署要求游戏进程已停止，首次安装要求正常游戏程序在规定布局的根目录，目标必须位于该游戏目录的Mods中。

本地SDK提供正式玩家信息和动作接口；底层诊断命令及audit参数不是安全沙箱或远程权限边界。公共CI只运行SDK、冻结回执和临时目录工具测试，不下载、编译依赖或上传游戏资产；真实原生回归仍在拥有游戏的本机进行。

## 启动就绪字段

`bridge-startup-1`的pid/started/transport/队列和受控座位字段保持传输身份含义。新增`startupUiReady`与`startupUiPhase`表达本次启动是否已到原生主菜单；新版启动工具要求true和start_menu，并标记`readinessContract=native_start_menu`。需配套同批DLL，缺字段的旧初始化文件不会被当作菜单已可用。

该证明在启动成功后保留，不能拿它判断当前仍在菜单或地图可操作；后续用observe/handoff读取当前对局状态。就绪文件阶段更新不改变同一进程的started，不破坏已建立的传输会话；超时不自动重启、关窗、选择存档或提交游戏命令。

## 正式接口和数据层

以下是当前 `PolytopiaSDK` 的公开方法对应关系。CLI 与方法同名，但 `resume` 对应 `resume_pending`；`handoff_start` 仅有 SDK 方法，CLI 使用 `watch_handoff`；`close` 及 Python 上下文退出只释放本地会话锁，不退出游戏。方法签名、参数与默认值以源码及 `agentcli.py --help` 为准。

| SDK 方法组 | 返回位置与用途 | 操作边界 |
| --- | --- | --- |
| `setup_status`、`setup_action`、`wait_setup` | 独立建局菜单、真实候选和原建局回执 | 管理入口，不进入对局行动候选；只创建明确选择的单人局，不自行恢复/覆盖热座存档 |
| `saved_games`、`load_game`、`save_game`、`return_to_menu`、`quit_game`、`wait_lifecycle` | 独立`game-lifecycle-1`管理回执和operationId；热座列表读取原生显示内容 | 恢复需新SDK会话；保存验证目标文件写入；保存后才回菜单/退出；不在另一玩家回合执行，不强制停止进程；超时查询原ID |
| `observe` | 外层 `binding/world/popup/enemyMemory`；details 增加城市、外交、榜单和通知域 | 读玩家观察；暗幕/人工回合返回交接信息，`world=null`；混合版本的详细域整体丢弃 |
| `game_info`、`unit_properties` | 外层 envelope 的 `native`；`game-info-1`、`unit-properties-1` | 原生设置和已公开玩家；己方及当前可见敌军面板，不能据此遍历隐藏对手 |
| `score_snapshot`、`result` | `native`；进行中 `score-snapshot-1`、结果查询 `result-1`，冻结记录 `native-final-1` | 进行中统计与终局反馈分开；显式已归档 episode 可在菜单读取，仍核对所属座位 |
| `threat_map`、`attack_preview`、`composite_preview` | `native`；`visible-threat-1`、`attack-preview-1`、`move-composite-preview-1` | 预测不等于实际伤害；不运行敌军隐藏路径规划。原生不支持的条件效果保持 partial/unknown |
| `tribe_info`、`leaderboard` | `native`，`tribe-info-1`、`leaderboard-1`；模式 open/read/close | 默认 read 被动读取，open/close 是原生 UI 写操作；opening/closing 之后查询原操作，不重发导航 |
| `list_actions` | `binding/actions/sources/discovery`，每行包含 `actionId/scope/command/args/eligible/native` | 原生候选动态生成；eligible=false/null 不提交；描述符只是本次版本的提议，不能作为跨局永久 ID |
| `execute`、`wait_result`、`resume_pending` | `native/ticket/binding/retryAllowed` 及 SDK status | 提交前复核候选，写前保存意图；有原票据就查询该票据，unknown/timeout 不重发 |
| `acknowledge_settled_unknown` | `settled_effect_unknown`、`ok=false/canContinue=true/retryAllowed=false` | 仅在明确接受未知效果、原票据已完成且空闲/同作用域后解除阻挡，不改写为成功 |
| `events`、`notifications` | `native`，`visible-events-1`、`public-notifications-1`；各自 stream/cursor | 游标归各自流，分页保留 historyGap/hasMore；缺口只要求重新同步观察，不补发游戏动作 |
| `choose_popup`、`dismiss_resolved_reward` | SDK status 与原生回调/展示关闭凭据 | index 是原生按钮 ID；回调提交或关闭展示不自动证明奖励/父行动完成，随后重新观察或查询原票据 |
| `handoff_status`、`handoff_start`、`watch_handoff` | 原生 `handoff-1`，SDK 交接状态及目标地图 | 只接管固定受控座位；不越过人工回合，开始回合回调提交后仍等原生地图就绪 |

`polytopia-sdk-1` 是 Python 返回 envelope，`polytopia-sdk-session-1` 是本地持久检查点，不能互当网络响应。原生 `world` 为 `decision-world-3`，原始 `schema` 查询 `bridge-schema-3` 是接口说明；各领域还有独立 schema。`action-settlement-1` 是原票据结果，`visible-combat-state-1` 是当前观察管线状态，两者不能互换。原生字段通常在 `native` 内；observe、候选发现和建局管理有各自上述外层结构，不把整个响应一律按 `native.world` 解包。

内部 `CombatFrame/CombatTicket`、原生包装类型及反射缓存不是公共数据模型，不能依赖其 CLR 字段或直接序列化。对外应消费明确 JSON 投影；`audit` 和 raw call/get/set 是独立诊断通道，不是正式 SDK 方法，也不是权限沙箱。

## 字段、状态和错误处理

| 字段组 | 对外约定 |
| --- | --- |
| `episodeId/playerId/streamId/stateVersion` | 描述符及原收据绑定；恢复/重启后的新流不能取代旧流。observationRevision 仅是观察修订号，不代表行动已完成 |
| 玩家/单位选择器 | 玩家 ID 为1～254，index 是独立下标；0和255不能作为受控座位。原生单位 ID 为 uint，读不到不是0；敌军笔记 sightingId 只做本地记忆关联 |
| 坐标 | 世界整数 `(x,y)`，不是屏幕像素；实际构造和查找还核对地图宽高。不得用折行后的数组下标冒充越界坐标 |
| HP和战斗数值 | 原始 health/damage 保持原生 HP 尺度；公开 UI stats 和经过已核对构建规则转换的显示 HP 分列，未知构建不猜显示值 |
| 分数与评级 | Perfection 分数为 points；Domination 各评级为 percent-points（100表示100%），倍率与timeGoal单列，不混作分数或0～1概率 |
| 空、未知、部分 | `null/unknown/unavailable` 不等同0、false或完整空列表；partial 可保留已知行，完整性字段决定能否把缺项当不存在。弹窗不完整总数为null，observedButtonCount仅为已读行数 |
| 行动收据 | accepted/submitted只表示受理/派发；settlementComplete表达管线完成，effectConfirmation表达所请求效果。actionCompleted只有明确效果确认才为true；审计snapshotComparison不决定命令完成 |

SDK `observed/discovered/archived` 是读取或候选状态，不能当行动成功；`confirmed` 才表示原行动效果确认。`needs_resolution` 表示先处理明确选择，`handoff/human_turn` 表示交接边界，`state_changed` 表示整帧需要重读。`rejected`、`unknown`、`not_confirmed`、`timeout`、`unavailable` 的恢复方式不同，调用方须同时保留原 ticket、binding 和 pending 写入状态。

错误对象的 `errorCode` 是机器分支依据，`error/reason` 用于说明，`native` 保留原生结果。当前 SDK 固定错误码按以下职责分组；原生传回的新 errorCode 仍可能透传，调用方不得把未认识的代码归作成功或自动重试。

| 错误族 | 当前固定代码 | 处理 |
| --- | --- | --- |
| 参数和描述符 | INVALID_ARGUMENT、INVALID_ACTION、INVALID_BINDING、INVALID_RESPONSE、BINDING_REQUIRED | 修正输入/重新观察；已有未决请求不因输入错误自动取消 |
| 座位与版本 | IDENTITY_UNVERIFIED、EPISODE_UNKNOWN、EPISODE_CHANGED、SEAT_CHANGED、SEAT_MISMATCH、STREAM_CHANGED、VERSION_UNKNOWN、STATE_CHANGED、STALE_ACTION | 拒绝混用；新观察/新会话不能把旧请求直接记成功 |
| 候选、读取和预览 | ACTION_NOT_AVAILABLE、NOT_CONTROLLED_TURN、READ_NOT_READY、READ_SCOPE_CHANGED、READ_SCOPE_UNKNOWN、READ_TARGET_CHANGED、PREVIEW_NOT_READY、PREVIEW_SCOPE_CHANGED | 重新读取对应域/等待当前管线；不重复可能生效的写入 |
| 未决与票据 | UNRESOLVED_ACTION、RECOVERY_REQUIRED、ACTION_NOT_SETTLED、RECEIPT_MISMATCH、EXPLICIT_ACK_REQUIRED | 查询原票据或保留未知；不强制解锁、不另开票据代替原请求 |
| 会话文件 | SESSION_CLOSED、SESSION_INVALID、SESSION_IO_ERROR、SESSION_REQUIRED、SESSION_SCOPE_MISMATCH、ACTIVE_SDK_PRESERVED | 处理本地会话/锁和作用域；不得删除未知意图来继续发动作 |
| 建局管理 | INVALID_SETUP_CONTEXT、INVALID_SETUP_RECEIPT、SETUP_ACTION_UNAVAILABLE、SETUP_IDENTITY_UNVERIFIED、SETUP_PENDING、SETUP_RECEIPT_MISMATCH、SINGLEPLAYER_SEAT_ONE_REQUIRED | 重读菜单或查询原建局回执，不能重复continue |
| 生命周期 | FRESH_SESSION_REQUIRED、LIFECYCLE_RECEIPT_REQUIRED、LIFECYCLE_SCOPE_MISMATCH | 使用正确会话/原操作，不重发写入；原生GAME_PIPELINE_NOT_IDLE、QUIT_RECEIPT_NOT_DURABLE也不表示成功 |
| 弹窗、交接和公开展示 | POPUP_CHANGED、REWARD_DISPLAY_NOT_CURRENT、HANDOFF_NOT_READY、PUBLIC_DISPLAY_SCOPE_CHANGED、PUBLIC_DISPLAY_SCOPE_UNKNOWN、NOTIFICATION_SCOPE_CHANGED | 重新观察当前展示/交接，不替用户选择新弹窗 |

CLI 另外将输入文件/用法问题表示为 `INVALID_INPUT` 等，将传输异常表示为 `TRANSPORT_UNAVAILABLE`；传输和原生错误族以保留的原响应为准。例如 `STALE_ACTION_CONTEXT` 要求重读候选，`SETTLEMENT_TICKET_UNKNOWN` 不能证明原动作没有执行，`INVOKE_FAILED/REQUEST_FAILED` 可在执行后出现，必须保留原票据并核对。只有明确且一致的 `accepted=false/dispatched=false/outcome=not_dispatched` 等未派发证据，才能按未执行处理；单一错误码或 `ok=false` 不够。

CLI 每次正常输出一个 JSON 对象。退出码：0为成功读取/确认，2为argparse用法错误，3为rejected，4为timeout，5为unknown/not_confirmed/unavailable或ok=false，6为已受理待后续、opening/closing、needs_resolution、handoff/human_turn、history_gap或state_changed。**退出码3不是“原行动肯定没执行”的证明**：SDK参数/作用域异常也会产生rejected，仍须检查会话未决和原生派发凭据。退出码0的读取成功也不代表任何游戏行动完成。

这份对应表说明当前正式接口的字段与版本，不增加技能候选白名单，不承诺未来原生字段/错误全集。新增可选字段继续按本页既有兼容约定处理；破坏字段、单位或状态含义时更新对应schema并提供迁移说明。具体候选和部署身份由本地完整验证记录、DLL/SDK哈希和部署凭据绑定，源码审阅稿的0.3.0不能替代这些证据。

## 决策投影

`ownedTechs`为原生已有科技集合。只有ownedTechsStatus=ok时空数组才表示确切没有，不可完整读取时保留null/unavailable；弃用的availableTechs不覆盖正式未知值。

规范化保留tiles.visualStyle的原生tribe/skin和状态，以及land、explored/currentlyVisible和最后观察元数据。当前构建inSight是explored别名，独立瞬时可见性未确认时为null。风格不等同领土，显式未探索格不进入地形投影。

currentState/matchEnded只表达阶段，不推断winner或终局细项；冻结结果使用SDK.result()，详细城市、任务和建设使用observe(details=True)等领域。bare-world没有嵌入所有领域不代表这些领域未实现。

## 城市数值和中断恢复

cityState.hasCityWall可为null：奖励读取不完整且没有肯定证据；读到城墙为true，完整列表无城墙为false。populationNeededToUpgrade读取失败为null并附reasonCodes，真实0保留为0。

city-options-6的可选cityUpgradeSemantics说明原始canCityBeUpgraded是带缓存的建设人口潜力，保存恢复后不保证同值，不等于当前升级资格。人口差额读populationNeededToUpgrade，奖励只从pendingCityReward和真实候选取得。

需要选择的父票据下，可明确提交当前有效城市奖励或peace_response。子动作仍先持久化、重新发现原生候选，保持父票据；回应完成后续查原父票据。普通新行动、未知写入或已有未决子动作仍受恢复门约束，不能借此重复EndTurn或自动接受和约。

## 历史容量与可读性

敌军笔记512是旧笔记软上限，不裁掉当前可见单位；离开视野后清理旧笔记并披露截断计数。原生票据历史128、公开事件日志2048，过期票据返回未知，事件缺口返回historyGap。不能将过期当作失败后自动重试。文本审计文件另按保留策略归档，磁盘日志不是自动有界。

通知只读取受控玩家实际显示中的DiplomacyNotification文本，不用旧解析缓存或内部双方对象补齐。public-notifications-1的captureComplete=false或unreadableNoticeCount非零表示实际Show但可见文字不可读，不能把events=[]解释为没有通知。这与容量淘汰的historyGap不同，也不覆盖观察器启动前的消息；旧帧缺字段不能当新版本完整证明。

visible-events-1的own_invisible_threat_changed事件只用unitId/coords定位己方探测单位，before/after为present/status/authority。before=null是本流无先前记录，present=null是读取未知，二者都不当false。actingPlayerId/actorId/nativeAction/nativeTargetId/nativeTargetCoord不附带来源，避免泄露隐藏行动。未变值去重，单位移除不写安全false，历史不代替当前world提示。

## 退出进度持久化

生命周期记录与战术票据历史独立。调用原生Quit前，将最终进度按operationId原子写入lifecycle-receipts，包含PID、bridgeStarted和controlledPlayerId，供原进程结束后的延迟查询。匹配的回执还必须结合原进程确已结束，才确认退出效果；仅有文件或队列异常均不够，exitCause仍为not_verified。落盘失败返回QUIT_RECEIPT_NOT_DURABLE并中止自动Quit；这些记录不是公开源码或游戏存档。

## 诊断边界

回复日志不是送达或动作完成证明。ticket指本请求实际新票据，requestedTicket仅为查询意图；context=last_frame表示最后观察上下文。普通错误只保留公开阶段/类别，不用原生异常正文补充隐藏对象。

启动与性能诊断可带readPreparation，表示桥接自身托管代码的准备状态，不执行游戏查询。complete不证明游戏、地图或行动成功；它不参与startupUiReady判定。本地OnUpdate计时也不是GPU或画面FPS。

底层反射/读取缓存和原生包装对象不是公共数据模型。原生字段不可读时保留unknown/partial，不在异常后换另一读取路径拼出看似完整的响应。更换游戏构建仍需核对；首次安装、跨机器运行与云端CI的证据不能由暖态性能或旧候选结果替代。
