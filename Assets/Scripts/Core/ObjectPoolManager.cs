using System.Collections.Generic;
using UnityEngine;

namespace HeroDefense.Core
{
    /// <summary>
    /// 对象池管理器 - 单例。
    /// 塔/敌人/投射物/特效等频繁创建销毁的对象走这里。
    /// </summary>
    public class ObjectPoolManager : MonoBehaviour
    {
        private static ObjectPoolManager _instance;
        public static ObjectPoolManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    GameObject go = new GameObject("[ObjectPoolManager]");
                    _instance = go.AddComponent<ObjectPoolManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        [System.Serializable]
        private class Pool
        {
            public string poolName;
            public GameObject prefab;
            public Queue<GameObject> availableObjects = new Queue<GameObject>();
            public List<GameObject> activeObjects = new List<GameObject>();
            public Transform parentTransform;
            public int initialSize;
        }

        private Dictionary<string, Pool> _pools = new Dictionary<string, Pool>();
        private Transform _poolRoot;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
            _poolRoot = new GameObject("PoolRoot").transform;
            _poolRoot.SetParent(transform);
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        public void CreatePool(string poolName, GameObject prefab, int initialSize = 5)
        {
            if (_pools.ContainsKey(poolName)) return;

            Pool pool = new Pool
            {
                poolName = poolName,
                prefab = prefab,
                initialSize = initialSize
            };

            GameObject poolParent = new GameObject("Pool_" + poolName);
            poolParent.transform.SetParent(_poolRoot);
            pool.parentTransform = poolParent.transform;

            for (int i = 0; i < initialSize; i++)
            {
                GameObject obj = Instantiate(prefab, pool.parentTransform);
                obj.name = poolName + "_" + i;
                obj.SetActive(false);
                pool.availableObjects.Enqueue(obj);
            }

            _pools[poolName] = pool;
        }

        public GameObject Get(string poolName, Vector3 position, Quaternion rotation)
        {
            if (!_pools.ContainsKey(poolName)) return null;

            Pool pool = _pools[poolName];
            GameObject obj;

            if (pool.availableObjects.Count > 0)
            {
                obj = pool.availableObjects.Dequeue();
            }
            else
            {
                obj = Instantiate(pool.prefab, pool.parentTransform);
                obj.name = poolName + "_expand";
            }

            obj.transform.position = position;
            obj.transform.rotation = rotation;
            obj.SetActive(true);
            pool.activeObjects.Add(obj);

            IPoolable poolable = obj.GetComponent<IPoolable>();
            if (poolable != null) poolable.OnSpawn();

            return obj;
        }

        public GameObject Get(string poolName)
        {
            return Get(poolName, Vector3.zero, Quaternion.identity);
        }

        public void Return(string poolName, GameObject obj)
        {
            if (!_pools.ContainsKey(poolName))
            {
                Destroy(obj);
                return;
            }

            Pool pool = _pools[poolName];

            IPoolable poolable = obj.GetComponent<IPoolable>();
            if (poolable != null) poolable.OnDespawn();

            obj.SetActive(false);
            obj.transform.SetParent(pool.parentTransform);
            pool.activeObjects.Remove(obj);
            pool.availableObjects.Enqueue(obj);
        }

        public void ReturnAll(string poolName)
        {
            if (!_pools.ContainsKey(poolName)) return;
            Pool pool = _pools[poolName];
            var activeList = new List<GameObject>(pool.activeObjects);
            foreach (var obj in activeList)
            {
                Return(poolName, obj);
            }
        }

        public void ReturnAllPools()
        {
            foreach (var kvp in _pools)
            {
                ReturnAll(kvp.Key);
            }
        }

        public int GetActiveCount(string poolName)
        {
            return _pools.ContainsKey(poolName) ? _pools[poolName].activeObjects.Count : 0;
        }
    }

    /// <summary>可池化对象接口。</summary>
    public interface IPoolable
    {
        void OnSpawn();
        void OnDespawn();
    }
}
