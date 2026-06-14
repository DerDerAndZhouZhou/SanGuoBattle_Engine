using UnityEngine;

namespace HeroDefense.Utils
{
    /// <summary>
    /// 泛型单例基类 - 所有 Manager 继承此类自动获得单例功能。
    /// </summary>
    public abstract class SingletonMono<T> : MonoBehaviour where T : SingletonMono<T>
    {
        private static T _instance;

        public static T Instance
        {
            get { return _instance; }
        }

        /// <summary>是否在场景切换时保留（DontDestroyOnLoad）。子类可覆写为 false。</summary>
        protected virtual bool IsPersistent => true;

        protected virtual void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = (T)this;

            if (IsPersistent)
            {
                DontDestroyOnLoad(gameObject);
            }

            OnSingletonInit();
        }

        protected virtual void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }

        protected virtual void OnSingletonInit() { }
    }
}
