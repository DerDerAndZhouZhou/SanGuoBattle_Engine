using UnityEngine;

namespace HeroDefense.UI
{
    /// <summary>
    /// UI 节点查找工具（2026-05-06 重构 — 替代 [SerializeField] Inspector 拖拽）。
    ///
    /// 三类查找入口：
    ///   1. <see cref="FindPanelByTag(string)"/>            — 按 Tag 全场景查找面板根 GO
    ///   2. <see cref="FindChildByName{T}"/>                — 在某 root 子树**递归**查找指定 name 的子节点 + 拿 T 组件
    ///   3. <see cref="FindChildByPath{T}"/>                — 在某 root 下按**全路径**查找（"Left/Avatar/Image"）
    ///
    /// 设计原则：
    ///   - 找不到 → <c>Debug.LogWarning</c> + 返回 null（不抛异常，不阻塞流程；调用方 null check 防御）
    ///   - 全部 static，无状态；不缓存结果（重复调用代价由调用方 Awake/Start 一次缓存控制）
    /// </summary>
    public static class UIFinder
    {
        // ----------------------------------------------------------------------
        // 1) 按 Tag 找面板
        // ----------------------------------------------------------------------

        /// <summary>
        /// 按 Tag 全场景查找面板 GO。Tag 不存在 / 没有 GO 标记此 Tag → LogWarning + 返回 null。
        /// </summary>
        public static GameObject FindPanelByTag(string tag)
        {
            if (string.IsNullOrEmpty(tag))
            {
                Debug.LogWarning("[UIFinder] FindPanelByTag: tag 为空");
                return null;
            }
            try
            {
                var go = GameObject.FindGameObjectWithTag(tag);
                if (go == null)
                {
                    Debug.LogWarning($"[UIFinder] 找不到 tag={tag} 的 GameObject（panel 未加载 / tag 未绑定）");
                }
                return go;
            }
            catch (UnityException)
            {
                // Unity 在 tag 字符串未在 TagManager 注册时抛 UnityException
                Debug.LogWarning($"[UIFinder] tag={tag} 未在 Unity TagManager 中注册");
                return null;
            }
        }

        /// <summary>FindPanelByTag + GetComponent&lt;T&gt; 一步到位。</summary>
        public static T FindPanelByTag<T>(string tag) where T : Component
        {
            var go = FindPanelByTag(tag);
            if (go == null) return null;
            var c = go.GetComponent<T>();
            if (c == null)
            {
                Debug.LogWarning($"[UIFinder] tag={tag} 对应的 GameObject 上无 {typeof(T).Name} 组件");
            }
            return c;
        }

        // ----------------------------------------------------------------------
        // 2) 按 name 递归查找子节点
        // ----------------------------------------------------------------------

        /// <summary>
        /// 在 root 子树**递归**查找第一个名为 <paramref name="name"/> 的子节点 + 拿 <typeparamref name="T"/> 组件。
        /// 多个同名时取深度优先第一个匹配。
        /// </summary>
        public static T FindChildByName<T>(Transform root, string name) where T : Component
        {
            var t = FindChildByName(root, name);
            if (t == null) return null;
            var c = t.GetComponent<T>();
            if (c == null)
            {
                Debug.LogWarning($"[UIFinder] {root.name}/.../{name} 上无 {typeof(T).Name} 组件");
            }
            return c;
        }

        /// <summary>同 <see cref="FindChildByName{T}"/>，仅返回 Transform（不取组件）。</summary>
        public static Transform FindChildByName(Transform root, string name)
        {
            if (root == null)
            {
                Debug.LogWarning($"[UIFinder] FindChildByName: root 为 null（查找 name={name}）");
                return null;
            }
            if (string.IsNullOrEmpty(name))
            {
                Debug.LogWarning("[UIFinder] FindChildByName: name 为空");
                return null;
            }
            var t = FindByNameRecursive(root, name);
            if (t == null)
            {
                Debug.LogWarning($"[UIFinder] {root.name} 子树中找不到 name={name}");
            }
            return t;
        }

        static Transform FindByNameRecursive(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name == name) return child;
                var found = FindByNameRecursive(child, name);
                if (found != null) return found;
            }
            return null;
        }

        // ----------------------------------------------------------------------
        // 3) 按全路径查找
        // ----------------------------------------------------------------------

        /// <summary>
        /// 按全路径在 root 下查找子节点 + 拿 <typeparamref name="T"/> 组件。
        /// 路径格式："Left/Avatar/Image"（与 Unity transform.Find 一致；逐级匹配，不递归）。
        /// </summary>
        public static T FindChildByPath<T>(Transform root, string path) where T : Component
        {
            var t = FindChildByPath(root, path);
            if (t == null) return null;
            var c = t.GetComponent<T>();
            if (c == null)
            {
                Debug.LogWarning($"[UIFinder] {root.name}/{path} 上无 {typeof(T).Name} 组件");
            }
            return c;
        }

        /// <summary>同 <see cref="FindChildByPath{T}"/>，仅返回 Transform。</summary>
        public static Transform FindChildByPath(Transform root, string path)
        {
            if (root == null)
            {
                Debug.LogWarning($"[UIFinder] FindChildByPath: root 为 null（path={path}）");
                return null;
            }
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogWarning("[UIFinder] FindChildByPath: path 为空");
                return null;
            }
            var t = root.Find(path);
            if (t == null)
            {
                Debug.LogWarning($"[UIFinder] {root.name}/{path} 路径查找失败");
            }
            return t;
        }
    }
}
