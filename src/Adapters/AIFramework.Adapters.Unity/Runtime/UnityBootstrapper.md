# 在场景里怎么接（Unity 宿主）

1. 新建空 GameObject，命名 `AIFrameworkHost`。
2. `Add Component` → `AIFrameworkBehaviour`（唯一装配入口，`Awake` 里建 Host 并 `Initialize()`）。
3. 放一个 Capsule 当单位（自带 `CapsuleCollider`）。判定平面是 Unity 的 **X-Z 平面**：`Vec2(X, Y)` 对应世界 `(X, 0, Y)`，所以摆位改 x / z，y 只影响视觉高度。
4. 把 Capsule 拖进 `AIFrameworkBehaviour` 的 `Scene Units` 数组；Play 时 `Start` 会自动 `BeginRealtime` 并逐个绑定。数组第 1 项默认挂 `UnitShieldActive` 标签（护盾 = 主角）。
5. 不想用 Inspector 就纯代码绑，顺序不能反：
   ```csharp
   var behaviour = hostObject.GetComponent<AIFrameworkBehaviour>();
   behaviour.BeginRealtime(levelId: 1, randomSeed: 20261118, unitCount: 2);
   behaviour.BindUnit(capsule.GetComponent<Collider>(), 1f, 5, 100, GameplayTagMask.Empty);
   ```
6. `BeginRealtime` 之前没有活跃会话，`BindUnit` 只会打警告并返回无效句柄（故意不抛，避免场景脚本被流程问题炸掉）。
7. `BindUnit` 内部就是 `BattleWorld.SpawnUnit(...)` + `UnitySpatialQuery.RegisterCollider(collider, handle)`；已有句柄要自己登记就直接调 `behaviour.Spatial.RegisterCollider(collider, handle)`。
8. 单位销毁/入池前调 `behaviour.Spatial.Unregister(collider)`，否则映射字典会随场景反复加载一直变大。
9. 签到演示在 `Start` 里跑一次，Console 打印天序号与 7 格状态，不需要任何 UI。
10. 会话结束由 `LateUpdate` 自动 `EndAndCollect` 并打印胜负、帧数、反射/打断次数；要自己结算就关掉 `Auto Begin Realtime On Start` 改走代码。
