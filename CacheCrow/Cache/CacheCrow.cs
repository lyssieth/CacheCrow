using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Timers;
using CacheCrow.CacheProviders;
using CacheCrow.Model;
using CacheCrow.Timers;

namespace CacheCrow.Cache
{
    /// <summary>
    /// Represents A simple LFU based Cache that supports data expiry.
    /// </summary>
    /// <typeparam name="K">Key</typeparam>
    /// <typeparam name="V">Value</typeparam>
    public sealed class CacheCrow<K, V> : ICacheCrow<K, V>
    {
        /// <summary>
        /// Raised when CacheCrow is empty.Note: It is also periodically raised by Cleaner when CacheCrow is empty.
        /// </summary>
        public event EventHandler<EventArgs> EmptyCacheEvent;
        public int DormantCacheCount => _secondaryCache.Count;
        private readonly int _cacheSize;
        private readonly int _activeCacheExpire;
        private static ConcurrentDictionary<K, CacheData<V>> _cacheDic;
        private static ConcurrentDictionary<K, Timer> _timerDic;
        private static CacheCrow<K, V> _cache;
        private static Timer _cleaner;
        private readonly ISecondaryCache<K, V> _secondaryCache;
        /// <summary>
        /// Initializes CacheCrow using default secondary cache.
        /// </summary>
        /// <param name="size">Count of total entries in Active(in-memory) CacheCrow</param>
        /// <param name="activeCacheExpire">Milliseconds before each entry in Active CacheCrow is expired</param>
        /// <param name="cleanerSnoozeTime">Milliseconds before Cleaner cleans Dormant CacheCrow. Note: Cleaner is called after every cleanersnoozetime milliseconds</param>
        /// <returns>Returns instance of ICacheCrow</returns>
        public static ICacheCrow<K, V> Initialize(int size = 1000, int activeCacheExpire = 300000, int cleanerSnoozeTime = 400000)
        {
            (_cache ??= new CacheCrow<K, V>(size, activeCacheExpire, cleanerSnoozeTime)).LoadCache();
            return _cache;
        }

        /// <summary>
        /// Initializes CacheCrow and uses secondaryCache as the dormant cache.
        /// </summary>
        /// <param name="secondaryCache">Instance of ISecondaryCache></param>
        /// <param name="size">Count of total entries in Active(in-memory) CacheCrow</param>
        /// <param name="activeCacheExpire">Milliseconds before each entry in Active CacheCrow is expired</param>
        /// <param name="cleanerSnoozeTime">Milliseconds before Cleaner cleans Dormant CacheCrow. Note: Cleaner is called after every cleanersnoozetime milliseconds</param>
        /// <returns>Returns instance of ICacheCrow</returns>
        public static ICacheCrow<K, V> Initialize(ISecondaryCache<K, V> secondaryCache, int size = 1000, int activeCacheExpire = 300000, int cleanerSnoozeTime = 400000)
        {
            (_cache ??= new CacheCrow<K, V>(size, activeCacheExpire, cleanerSnoozeTime, secondaryCache)).LoadCache();
            return _cache;
        }

        /// <summary>
        /// Returns instance of ICacheCrow if it has been initialized
        /// </summary>
        public static ICacheCrow<K, V> GetCacheCrow => _cache;

        /// <summary>
        /// Gets number of entries in Active CacheCrow
        /// </summary>
        public int ActiveCount => _cacheDic.Count;

        /// <summary>
        /// Gets total number of entries in CacheCrow
        /// </summary>
        public int Count => ActiveCount + ReadBinary().Count;

        /// <summary>
        /// Gets a previously calculated total number of entries in CacheCrow. Note: Should be considered if realtime values are not required.
        /// </summary>
        public int PreviousCount => ActiveCount + DormantCacheCount;

        /// <summary>
        /// Removes all entries from CacheCrow, including entries in dormant cache and raises EmptyCacheEvent.
        /// </summary>
        public void Clear()
        {
            _cacheDic.Clear();
            _timerDic.Clear();
            _cleaner.Stop();
            _cleaner.Start();
            WriteCache();
            EmptyCacheEvent?.Invoke(this, EventArgs.Empty);
        }
        /// <summary>
        /// Inputs entry in Active CacheCrow if its size is not exceeded else adds the entry in Dormant CacheCrow.
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="data">Value</param>
        public void Add(K key, V data)
        {
            Add(key, data, null);
        }
        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        /// <param name="key"></param>
        /// <param name="data"></param>
        /// <param name="UpdateOnExpire"></param>
        public void Add(K key, V data, Func<V> UpdateOnExpire)
        {
            _secondaryCache.EnsureExists();
            if (data != null)
            {
                Add(key, data, 1, UpdateOnExpire);
            }
        }
        /// <summary>
        /// Searches the key in both Active and Dormant CacheCrow and if found then updates the value.
        /// </summary>
        /// <param name="key">Existing key</param>
        /// <param name="data">New value</param>
        /// <returns>True if value was updated else false</returns>
        public bool Update(K key, V data)
        {
            _secondaryCache.EnsureExists();
            if (data != null)
            {
                if (_cacheDic.ContainsKey(key))
                {
                    var cacheData = _cacheDic[key];
                    cacheData.ModifiedDate = DateTime.Now;
                    cacheData.Data = data;
                    _cacheDic[key] = cacheData;
                    _timerDic[key].Stop();
                    _timerDic[key].Start();
                    return true;
                }
                else
                {
                    var dic = ReadBinary();
                    if (dic.ContainsKey(key))
                    {
                        var cacheData = _cacheDic[key];
                        cacheData.ModifiedDate = DateTime.Now;
                        cacheData.Data = data;
                        _cacheDic[key] = cacheData;
                        _timerDic[key].Stop();
                        _timerDic[key].Start();
                        return true;
                    }
                }
            }
            return false;
        }
        /// <summary>
        /// Checks if key is present in the Active CacheCrow.
        /// </summary>
        /// <param name="key">The key to find</param>
        /// <returns>True if key is found in Active CacheCrow, else false</returns>
        public bool ActiveLookUp(K key)
        {
            if (_cacheDic.ContainsKey(key))
            {
                var cacheData = _cacheDic[key];
                cacheData.Frequency++;
                _cacheDic[key] = cacheData;
                return true;
            }
            return false;
        }
        /// <summary>
        /// Checks if key is present in CacheCrow(Active+Dormant)
        /// </summary>
        /// <param name="key">The key to find</param>
        /// <returns>True if key is found, else false</returns>
        public bool LookUp(K key)
        {
            if (_cacheDic.ContainsKey(key))
            {
                var cacheData = _cacheDic[key];
                cacheData.Frequency++;
                _cacheDic[key] = cacheData;
                return true;
            }
            return DeepLookUp(key);
        }
        /// <summary>
        /// Removes the entry from Active CacheCrow corresponding to the key.
        /// </summary>
        /// <param name="key">The key to corresponding value to remove</param>
        /// <returns>If removed then returns removed value as CacheData, else returns empty CacheData</returns>
        public CacheData<V> ActiveRemove(K key)
        {
            CacheData<V> i = new();
            if (_cacheDic.ContainsKey(key) && (_timerDic.ContainsKey(key)))
            {
                var cacheTimer = _timerDic[key];
                _cacheDic.TryRemove(key, out i);
                cacheTimer.Elapsed -= ElapsedEvent;
                cacheTimer.Enabled = false;
                cacheTimer.AutoReset = false;
                cacheTimer.Stop();
                cacheTimer.Close();
                cacheTimer.Dispose();
                if (DormantCacheCount == 0 && ActiveCount == 0)
                {
                    EmptyCacheEvent?.Invoke(this, EventArgs.Empty);
                }
            }
            return i;
        }
        /// <summary>
        /// Removes the entry from CacheCrow(Active+Dormant) corresponding to the key.
        /// </summary>
        /// <param name="key">The key to corresponding value to remove</param>
        /// <returns>If removed then returns removed value as CacheData, else returns empty CacheData</returns>
        public CacheData<V> Remove(K key)
        {
            CacheData<V> i = new();
            if (_cacheDic.ContainsKey(key) && (_timerDic.ContainsKey(key)))
            {
                var cacheTimer = _timerDic[key];
                _cacheDic.TryRemove(key, out i);
                cacheTimer.Elapsed -= ElapsedEvent;
                cacheTimer.Enabled = false;
                cacheTimer.AutoReset = false;
                cacheTimer.Stop();
                cacheTimer.Close();
                cacheTimer.Dispose();
                if (DormantCacheCount == 0 && ActiveCount == 0)
                {
                    EmptyCacheEvent?.Invoke(this, EventArgs.Empty);
                }
            }
            else
            {
                var dic = ReadBinary();
                if (dic.ContainsKey(key))
                {
                    if (dic.TryRemove(key, out i))
                    {
                        WriteBinary(dic);
                    }
                }
            }
            return i;
        }
        /// <summary>
        /// Lookups the key in Active+Dormant CacheCrow, if found then increments the frequency.
        /// </summary>
        /// <param name="key">Key to corresponding value</param>
        /// <returns>If V is reference type and it is present then Object V else if V is value-type and it is not present then default value of V</returns>
        public V GetValue(K key)
        {
            ConcurrentDictionary<K, CacheData<V>> dic;
            if (ActiveLookUp(key))
            {
                return _cacheDic[key].Data;
            }
            else if ((dic = ReadBinary()) != null && dic.ContainsKey(key))
            {
                var cacheData = dic[key];
                cacheData.Frequency++;
                dic[key] = cacheData;
                WriteBinary(dic);
                PerformLFUAndReplace(key, cacheData);
                return cacheData.Data;
            }
            else
            {
                return default;
            }
        }
        /// <summary>
        /// Lookups the key in Active CacheCrow, if found then increments the frequency. Note: LFU maybe performed and entries maybe swapped between Active and Dormant CacheCrow.
        /// </summary>
        /// <param name="key">Key to corresponding value</param>
        /// <returns>If V is reference type and it is present then Object V else if V is value-type and it is not present then default value of V</returns>
        public V GetActiveValue(K key)
        {
            if (ActiveLookUp(key))
            {
                return _cacheDic[key].Data;
            }
            return default;
        }
        /// <summary>
        /// Disposes and writes the entries in Active CacheCrow to Dormant CacheCrow.
        /// </summary>
        public void Dispose()
        {
            var dic = ReadBinary();
            if (dic.Count > _cacheDic.Count)
            {
                foreach (var t in _cacheDic)
                {
                    if (dic.ContainsKey(t.Key))
                    {
                        dic[t.Key] = t.Value;
                    }
                    else
                    {
                        dic.TryAdd(t.Key, t.Value);
                    }
                }
                WriteBinary(dic);
            }
            else
            {
                foreach (var t in dic)
                {
                    _cacheDic.TryAdd(t.Key, t.Value);
                }
                WriteBinary(_cacheDic);
            }
            foreach (var cacheTimer in _timerDic)
            {
                cacheTimer.Value.Elapsed -= ElapsedEvent;
                cacheTimer.Value.Enabled = false;
                cacheTimer.Value.AutoReset = false;
                cacheTimer.Value.Stop();
                cacheTimer.Value.Close();
                cacheTimer.Value.Dispose();
            }
            _cacheDic = null;
            _timerDic = null;
        }

        static CacheCrow()
        {
            _cacheDic = new ConcurrentDictionary<K, CacheData<V>>();
            _timerDic = new ConcurrentDictionary<K, Timer>();
        }

        /// <summary>
        /// Loads entries from Dormant CacheCrow into Active CacheCrow
        /// </summary>
        private void LoadCache()
        {
            var dic = ReadBinary();
            var orderedDic = dic.OrderByDescending(x => x.Value.Frequency).ToList();
            for (int i = 0; i < _cacheSize && i < orderedDic.Count; i++)
            {
                Add(orderedDic[i].Key, orderedDic[i].Value);
            }
            _cleaner.Start();
        }
        private CacheCrow(int size = 1000, int activeCacheExpire = 300000, int cleanerSnoozeTime = 120000, ISecondaryCache<K, V> secondaryCache = null) : base()
        {
            if (secondaryCache != null)
            {
                _secondaryCache = secondaryCache;
            }
            else if (_secondaryCache == null)
            {
                _secondaryCache = SecondaryCacheProvider<K, V>.GetSecondaryCache();
            }
            _cacheSize = size;
            _activeCacheExpire = activeCacheExpire;
            _cleaner = new Timer(cleanerSnoozeTime);
            _cleaner.Elapsed += Cleaner_Event;
        }

        private void WriteBinary(K item, CacheData<V> value)
        {
            if (value == null)
            {
                return;
            }
            var dic = ReadBinary();
            dic.TryRemove(item, out _);
            dic.TryAdd(item, value);
            WriteBinary(dic);
        }
        private void WriteBinary(ConcurrentDictionary<K, CacheData<V>> dic)
        {
            _secondaryCache.WriteCache(dic);
        }
        private void WriteCache()
        {
            _secondaryCache.WriteCache(_cacheDic);
        }
        private ConcurrentDictionary<K, CacheData<V>> ReadBinary()
        {
            _secondaryCache.EnsureExists();
            return _secondaryCache.ReadCache();
        }
        private void Add(K item, CacheData<V> cacheData, bool force = true)
        {
            if (cacheData == null)
            {
                return;
            }
            Add(item, cacheData.Data, cacheData.Frequency, cacheData.OnExpire, force);
        }
        private void Add(K item, V data, int frequency, Func<V> updateOnExpire = null, bool force = true)
        {
            if (item != null && !string.IsNullOrWhiteSpace(item.ToString()))
            {
                var cacheData = new CacheData<V>(data, frequency)
                {
                    OnExpire = updateOnExpire
                };
                if (ActiveCount < _cacheSize)
                {
                    if (_cacheDic.TryAdd(item, cacheData))
                    {
                        _cacheDic[item] = cacheData;
                        _timerDic.TryRemove(item, out _);
                        var timer = new CacheTimer<K>(item, _activeCacheExpire);
                        timer.Elapsed += ElapsedEvent;
                        timer.Start();
                        _timerDic.TryAdd(item, timer);
                        var dic = ReadBinary();
                        dic.TryRemove(item, out cacheData);
                        WriteBinary(dic);
                        return;
                    }
                }
                else
                {
                    if (!force)
                    {
                        return;
                    }
                    if (!PerformLFUAndReplace(item, cacheData))
                    {
                        WriteBinary(item, cacheData);
                    }
                }
            }
        }

        /// <summary>
        /// Performs LFU and replaces.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        private bool PerformLFUAndReplace(K key, CacheData<V> value)
        {
            CacheData<V> tmp = null;
            bool updated = false;
            int emptySlots = _cacheSize - _cacheDic.Count;
            try
            {
                if (emptySlots > 0)
                {
                    var dic = ReadBinary();

                    foreach (var cacheData in dic.Where(x => x.Value.Frequency > value.Frequency).Take(emptySlots))
                    {
                        dic.TryRemove(cacheData.Key, out tmp);
                        Add(cacheData.Key, cacheData.Value.Data);
                        updated = true;
                    }

                    if (updated)
                    {
                        WriteBinary(dic);
                    }
                    else
                    {
                        Add(key, value);
                        updated = true;
                    }
                }
                else
                {
                    var dic = _cacheDic;
                    var leastFrequencyCacheData = dic.OrderBy(x => x.Value.Frequency).FirstOrDefault();
                    if (leastFrequencyCacheData.Value.Frequency >= value.Frequency)
                    {
                        WriteBinary(key, value);
                        updated = true;
                    }
                    else
                    {
                        if (_cacheDic.TryRemove(leastFrequencyCacheData.Key, out tmp))
                        {
                            WriteBinary(leastFrequencyCacheData.Key, tmp);
                            _cacheDic.TryAdd(key, value);
                            updated = true;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }

            return updated;
        }

        private bool DeepLookUp(K item)
        {
            Console.WriteLine("Performing Deep Lookup..");
            var dic = ReadBinary();
            if (dic.ContainsKey(item))
            {
                var cacheData = dic[item];
                cacheData.Frequency++;
                cacheData.CreationDate = DateTime.Now;
                dic[item] = cacheData;
                WriteBinary(dic);
                PerformLFUAndReplace(item, cacheData);
                return true;
            }

            return false;
        }
        private void Cleaner_Event(object sender, ElapsedEventArgs e)
        {
            System.Threading.Thread cleaner = new(() =>
            {
                var dic = ReadBinary();
                WriteBinary(dic);
                if (DormantCacheCount == 0 && ActiveCount == 0)
                {
                    EmptyCacheEvent?.Invoke(this, EventArgs.Empty);
                }
            });
            cleaner.Start();
        }
        private void ElapsedEvent(object sender, ElapsedEventArgs e)
        {
            if (_cacheDic == null)
            {
                return;
            }
            var cacheTimer = (CacheTimer<K>)sender;
            var cacheData = _cacheDic.ContainsKey(cacheTimer.Key) ? _cacheDic[cacheTimer.Key] : null;
            if (cacheData?.OnExpire != null)
            {
                V newData = cacheData.OnExpire.Invoke();
                Update(cacheTimer.Key, newData);
            }
            else
            {
                System.Threading.Thread cacheCleaner = new(() =>
                {
                    var cachetimer = (CacheTimer<K>)sender;
                    cachetimer.Elapsed -= ElapsedEvent;
                    cachetimer.Close();
                    _cacheDic.TryRemove(cachetimer.Key, out _);
                    if (DormantCacheCount == 0 && ActiveCount == 0 && EmptyCacheEvent != null)
                    {
                        EmptyCacheEvent(this, EventArgs.Empty);
                    }
                    else if (DormantCacheCount != 0)
                    {
                        var pair = ReadBinary().OrderByDescending(x => x.Value.Frequency).FirstOrDefault();
                        PerformLFUAndReplace(pair.Key, pair.Value);
                    }
                });
                cacheCleaner.Start();
            }
        }
    }
}
