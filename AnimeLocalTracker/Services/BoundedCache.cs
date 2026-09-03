using System;
using System.Collections.Concurrent;
using System.Linq;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Entrada de caché con expiración. Tipo explícito (no tupla) para que los nombres
/// de campos sobrevivan al paso por tipos genéricos.
/// </summary>
internal sealed class CacheEntry<T>
{
    public CacheEntry(T data, DateTime expiration)
    {
        Data = data;
        Expiration = expiration;
    }

    public T Data { get; }
    public DateTime Expiration { get; }
}

/// <summary>
/// Evicción acotada para cachés ConcurrentDictionary compartidas (ARQ-04):
/// al superar el tope se expulsan primero las entradas expiradas y luego las más
/// antiguas (por expiración) en lote, manteniendo el consumo de RAM constante.
/// Los límites son aproximados (sin lock global): suficiente para un caché.
/// </summary>
internal static class BoundedCache
{
    public static void Insert<TKey, TValue>(ConcurrentDictionary<TKey, CacheEntry<TValue>> cache, TKey key,
        TValue data, int maxEntries, TimeSpan lifetime)
        where TKey : notnull
    {
        cache[key] = new CacheEntry<TValue>(data, DateTime.UtcNow.Add(lifetime));

        if (cache.Count <= maxEntries)
            return;

        ExpulsarExcedente(cache, maxEntries);
    }

    /// <summary>Caché sin expiración: solo acota el tamaño (evicción aproximada de las primeras entradas).</summary>
    public static void InsertNoExpiry<TKey, TValue>(ConcurrentDictionary<TKey, TValue> cache, TKey key,
        TValue data, int maxEntries)
        where TKey : notnull
    {
        cache[key] = data;

        if (cache.Count <= maxEntries)
            return;

        foreach (var kv in cache.Take(32))
        {
            cache.TryRemove(kv.Key, out _);
        }
    }

    private static void ExpulsarExcedente<TKey, TValue>(ConcurrentDictionary<TKey, CacheEntry<TValue>> cache,
        int maxEntries)
        where TKey : notnull
    {
        var ahora = DateTime.UtcNow;

        // 1. Primero expirar entradas vencidas (gratis, no reordena nada)
        foreach (var kv in cache)
        {
            if (kv.Value.Expiration <= ahora)
            {
                cache.TryRemove(kv.Key, out _);
            }
        }

        if (cache.Count <= maxEntries)
            return;

        // 2. Aún sobre el tope: expulsar las ~32 entradas más antiguas (por expiración)
        //    en lote, para no re-ordenar el caché completo en cada inserción posterior.
        foreach (var kv in cache.OrderBy(kv => kv.Value.Expiration).Take(32))
        {
            cache.TryRemove(kv.Key, out _);
        }
    }
}
