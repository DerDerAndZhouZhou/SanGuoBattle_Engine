# Resources/vfx/ — VFX Prefab 主包目录

本目录是 **VFX Prefab 的主包归宿**（不在 `Game/art/`，因为 Unity 的 `Resources.Load` 必须从主包读 Prefab）。

## 设计决策（GDD §0.1）

- **MVP 阶段**：VFX 走 `Resources.Load`，进 Unity 主包，**不热更**
- **M3+ 阶段**：迁到 Addressables / AssetBundle，才支持 CDN 热更 Prefab

## 命名

- 配置表 `vfx.txt` 的 `prefab_key` 字段 = 本目录下的相对路径（不带 `.prefab`）
- 例：`vfx_merge_up` 行的 `prefab_key = "vfx/merge_up"` → `Resources.Load<GameObject>("vfx/merge_up")`

## Prefab 内容约束

- Root 必须是 `ParticleSystem` 或带 `Animator` 的 GameObject
- 必须 self-contained（不能引用场景对象）
- 自动销毁通过 `vfx.txt` 的 `duration` 字段控制（C# 侧定时回收到 ObjectPool）
- 若 `loop=true`（如冰冻常驻），需手动调用 `Stop()` 才回收

## 占位

- `Resources/vfx/default.prefab` 是占位（空 ParticleSystem 或 1 帧闪光）
- `LuaHost.LoadPrefab` 加载失败时尝试 `default`，仍失败返回 null + LogWarning

## 当前状态

空目录，等美术接入。
