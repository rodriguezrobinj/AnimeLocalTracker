using System;
using System.Collections.Generic;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Caché LRU real y thread-safe (ARQ-04b): todo acceso e inserción marca la entrada
/// como recientemente usada; al superar la capacidad se expulsa la menos usada.
/// Reemplaza la evicción aproximada (Take de primeras entradas) de los cachés de imágenes.
/// </summary>
internal sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _maxEntries;
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map = new();
    private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new();
    private readonly object _lock = new();

    public LruCache(int maxEntries)
    {
        _maxEntries = Math.Max(1, maxEntries);
    }

    public int Count
    {
        get { lock (_lock) return _map.Count; }
    }

    /// <summary>Devuelve el valor y lo marca como recientemente usado.</summary>
    public bool TryGetValue(TKey key, out TValue? value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
            value = default;
            return false;
        }
    }

    /// <summary>Elimina la entrada si existe (p. ej. al borrar un anime de la biblioteca).</summary>
    public void Remove(TKey key)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _map.Remove(key);
            }
        }
    }

    /// <summary>Inserta o actualiza y, si supera la capacidad, expulsa la entrada menos usada.</summary>
    public void Set(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                node.Value = new KeyValuePair<TKey, TValue>(key, value);
            }
            else
            {
                node = new LinkedListNode<KeyValuePair<TKey, TValue>>(new KeyValuePair<TKey, TValue>(key, value));
                _map[key] = node;
            }
            _order.AddFirst(node);

            while (_map.Count > _maxEntries && _order.Last != null)
            {
                _map.Remove(_order.Last.Value.Key);
                _order.RemoveLast();
            }
        }
    }
}
