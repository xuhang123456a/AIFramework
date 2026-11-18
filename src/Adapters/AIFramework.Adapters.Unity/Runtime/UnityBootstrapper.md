# 在场景里怎么接（Unity 宿主）

1. 新建空 GameObject → `Add Component` → `AIFrameworkBehaviour`（唯一装配入口，`Awake` 里建 Host 并 `Initialize()`）。
2. 放一个 Capsule 当单位（自带 `CapsuleCollider`）。判定平面是 **X-Z**：`Vec2(X, Y)` 对应世界 `(X, 0, Y)`，摆位改 x / z，y 只影响视觉高度。
3. 把 Capsule 拖进 `Scene Units` 数组；`Start` 会自动 `BeginRealtime` 并逐个绑定，第 1 项默认挂 `UnitShieldActive`（护盾 = 主角）。
4. 纯代码绑也可以，顺序不能反（先开战局再绑）：
   `behaviour.BeginRealtime(1, 20261118, 2);`
   `behaviour.BindUnit(capsule.GetComponent<Collider>(), 1f, 5, 100, GameplayTagMask.Empty);`
5. `BeginRealtime` 之前没有活跃会话，`BindUnit` / `BindProjectile` 只打警告并返回无效句柄（故意不抛，免得场景脚本被流程问题炸掉）。
6. 飞行物同理：`behaviour.BindProjectile(ball.GetComponent<Collider>(), ownerHandle, new Vec2(0f, -6f), 10, GameplayTagMask.Empty)`；不绑就没有"物理体 → 句柄"映射，护盾反射查不到它。
7. 内部关系：`BindUnit` = `BattleWorld.SpawnUnit(...)` + `Spatial.RegisterCollider(collider, handle)`；只想登记现成句柄就直接调 `behaviour.Spatial.RegisterCollider(...)`。
8. 单位销毁/入池前调 `behaviour.Spatial.Unregister(collider)`，否则映射字典会随场景反复加载一直变大。
9. 签到演示在 `Start` 跑一次，Console 打印天序号与 7 格状态，不需要任何 UI；想手动领一次可调 `behaviour.Host.SignInView.SimulateClaimClicked()`。
10. 会话结束由 `LateUpdate` 自动 `EndAndCollect` 并打印胜负/帧数/反射次数；要自己结算就关掉 `Auto Begin Realtime On Start` 并自行调 `EndAndCollect`。
